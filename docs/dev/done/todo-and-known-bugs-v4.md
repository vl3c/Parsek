# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18. Closed during archival: PR #307 career-earnings-bundle post-review follow-ups (all four fixes confirmed in code — `PickScienceOwnerRecordingId`, `DedupKey` serialization, `FundsAdvance` in ContractAccepted, `MilestoneScienceAwarded` field), #337 (same-tree EVA LOD culling — fix shipped in PR #260, stale), #368 / #367 / #364 (PR #240 / #242 / #229 follow-ups — done).

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## Priority queue — deterministic-timeline correctness

The four top-of-queue correctness fixes (#431, #432, #433, #434) shipped in the v0.8.2 cycle, and the follow-up epoch-retirement cleanup shipped on 2026-04-23. `MilestoneStore.CurrentEpoch` is now gone from production; abandoned-branch exclusion is owned by discard/unstash recording visibility and ghost-only event filtering. Legacy `epoch` fields remain load-only compatibility for older saves. See #431's archived notes in `done/todo-and-known-bugs-v3.md`.

---

## Rewind to Separation — v0.9 shipped

The feature shipped on `feat/rewind-staging` across v0.9's development cycle. See:

- Design doc: `docs/parsek-rewind-to-separation-design.md` (post-implementation, final).
- Pre-implementation spec (archived): `docs/dev/done/parsek-rewind-separation-design.md`.
- Roadmap entry: `docs/roadmap.md` → Completed → v0.9.
- CHANGELOG: `CHANGELOG.md` → v0.9.0 features + Internals block.

Follow-up shipped after the `2026-04-23_1855_logs-package` audit: RP-backed unfinished-flight rows now route through `RewindInvoker` from both the virtual group and the normal recordings table. The log showed `Kerbal X Probe` had gone through the legacy branch-recording launch rewind (`Kerbal X` at UT 8.28 into Space Center); the fix makes the normal row resolve the child's Rewind Point slot first, blocks disabled slots before a scene load, and leaves non-crashed branch children on the regular temporal controls. The same audit also found that load-time cleanup could purge normal staging RPs before the pending-tree merge dialog; normal RPs without a creating session now survive that load, promote to persistent once the tree is accepted, crash-terminal RP children are stamped `CommittedProvisional`, and `Unfinished Flights` membership accepts both legacy `Immutable` crashes and current `CommittedProvisional` crashes.

Carryover follow-ups (tracked in the design doc under Known Limitations / Future Work):

- Index-to-recording-id refactor to lift the 13 grep-audit exemptions added in Phase 3.
- Halt `EffectiveRecordingId` walk at cross-tree boundaries (v1 does not produce cross-tree supersedes; latent-invariant guard).
- Wider v2 tombstone scope (contracts, milestones) when safe.

---

# Known Bugs

## ~~566. Post-switch attitude-only docking alignment could miss deliberate station rotation and replay RELATIVE docking geometry against the wrong frame~~

**Source:** docking-alignment follow-up planned and implemented on 2026-04-24 after the broader `#546` post-switch watcher shipped. The remaining gap showed up in the capsule/station flow where the player switches to a nearby station, rotates it with SAS / reaction wheels to line up the docking port, switches back, and docks.

**Concern:** `#546` fixed the general "first meaningful modification after switch" gap, but its trigger set still favored translation, engine/RCS activity, and authored vessel-state changes. Pure attitude alignment could stay below those seams and never start recording. Separately, the RELATIVE frame contract was inconsistent: offsets were stored as world-space deltas while playback multiplied rotation by the live anchor transform as if the stored rotation were anchor-local. That meant even newly recorded docking approaches could drift once the nearby vessel had been rotated in place.

**Fix:** post-switch watching now tracks baseline world rotation and accepts a dedicated `AttitudeChange` trigger only after a sign-canonicalized `Quaternion.Angle` exceeds the 3 degree threshold and survives a short debounce, so focus switch alone stays ignored while deliberate alignment starts or promotes recording. Active sampling is now attitude-aware so wheel/SAS alignment emits points even when velocity/orbit barely change, the trigger-start path seeds baseline + current pose so the first few degrees are not lost, and recording format `v6` makes new RELATIVE sections truly anchor-local for both position and rotation. Legacy `v5`-and-older RELATIVE sections stay on the old playback path, and unrelated format normalization no longer silently upgrades old recordings onto the v6 contract.

**Review follow-up (same PR):** dropped an unused `boundaryAnchor` parameter on `StartRecording` / `PromoteRecordingFromBackground` (baseline seeding runs through `SeedTrajectoryPoint` instead); collapsed the redundant version branch in `ResolveRelativePlaybackRotation` since both v5 and v6 reconstitute world rotation with the same `anchor * stored` formula (only the storage semantics differ); added a `ShouldSkipSeedDueToRelativeSection` guard so a baseline seed built from `v.srfRelRotation` can never be committed into an anchor-relative track section; and added diagnostic log lines when `OnPhysicsFrame` samples a packed vessel or when `ResolveActiveRecordingFormatVersion` has to fall back to the current format version.

**Files:** `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/FlightRecorder.cs`, `Source/Parsek/TrajectoryMath.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek/TrajectorySidecarBinary.cs`, `Source/Parsek.Tests/PostSwitchAutoRecordTests.cs`, `Source/Parsek.Tests/AdaptiveSamplingTests.cs`, `Source/Parsek.Tests/RelativePlaybackTests.cs`, `Source/Parsek.Tests/FormatVersionTests.cs`, `docs/dev/done/plans/fix-post-switch-attitude-docking.md`, `.claude/CLAUDE.md`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-24. Fixed for v0.9.0.

## ~~567. Scene-enter auto-record never resumed on vessels with a committed recording~~

Observed in the `2026-04-23_2200_playtest-post-v083` playtest: launch a vessel, commit the recording, exit to KSC, re-enter the same vessel via Tracking Station, drive it. Auto-record stayed `mode=none` for the entire session. The `FlightRecorder.OnVesselGoOffRails` handler early-returns when `IsRecording` is false, and the design spec (`recording-system-design.md` §4.5 "When the player returns to flight, vessels come off rails → new checkpoints captured → physics sampling resumes") expected the resume path to run here.

Root cause: the existing `TryRestoreCommittedTreeForSpawnedActiveVessel` pipeline (triggered from `OnFlightReady`) does look up the committed tree by active vessel pid, but its filter in `TryFindCommittedTreeForSpawnedVessel` requires both `rec.VesselSpawned == true` AND `rec.SpawnedVesselPersistentId == activeVesselPid`. Only the pid is persisted; the flag was not re-derived from the pid on load, so after every save/load `VesselSpawned` dropped back to its default `false` and the filter silently skipped the match.

Fix: both load paths (`RecordingTree.Load` and `ParsekScenario.OnLoad`'s tree-rec mutable-state restore) now set `rec.VesselSpawned = (spawnedPid != 0)` alongside `rec.SpawnedVesselPersistentId`. The invariant is straight from the existing `BuildSpawnedVesselPidSet(ERS)` / spawner `rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;` pattern; the fix brings the load side in line. Regression tests in `RecordingTreeTests.RecordingTree_SpawnedPid_RestoresVesselSpawnedFlagOnLoad` + `RecordingTree_NoSpawnedPid_LeavesVesselSpawnedFalseOnLoad` and `VesselSwitchTreeTests.TryFindCommittedTreeForSpawnedVessel_MatchesTreeReloadedFromConfigNode`.

Related playtest observation (separate bug, not fixed here): the KSC-view adoption (`VesselSpawner.TryAdoptExistingSourceVesselForSpawn`) still treats the original on-pad launch vessel as the terminal real-spawn, so ghosts playing from launch → terminal vanish at end without a visible terminal-position vessel in KSC view. Once auto-record-on-resume starts producing new commits, the adopted vessel position drifts with the recording lineage and this surfaces less often, but a principled fix (move / re-spawn at terminal on commit; or render a persistent terminal indicator in KSC) is still open.

Follow-up the same hour (`2026-04-23_2305_post-fix-regression`): once the restore path reliably fires, the detach-for-resume step in `TryTakeCommittedTreeForSpawnedVesselRestore` leaves the resumed recording carrying its prior-commit `VesselSpawned=true` / `SpawnedVesselPersistentId=<pid>`. The merge dialog's `CanPersistVessel` → `ShouldSpawnAtRecordingEnd` chain reads that as "already spawned, skip" and defaults the leaf to ghost-only at the next commit, which nulls the `VesselSnapshot` and makes subsequent KSC spawn attempts fail with `no vessel snapshot`. `TryTakeCommittedTreeForSpawnedVesselRestore` now clears `VesselSpawned` and `SpawnedVesselPersistentId` on the resumed recording immediately after detaching from committed storage so the re-commit re-evaluates persist eligibility from scratch; the KSC adoption path re-establishes the flags post-commit when the source vessel is still around. Regression in `VesselSwitchTreeTests.TryTakeCommittedTreeForSpawnedVesselRestore_ClearsPriorSpawnFlagsOnResumedRecording`.

UX polish (same branch): the FlightRecorder `StartRecording(isPromotion: true)` path suppresses the inner `Recording STARTED` screen toast, so scene-enter resume used to happen silently. `TryRestoreCommittedTreeForSpawnedActiveVessel` now surfaces `Recording STARTED (resume)` after a successful restore, matching the `(auto)` and `(auto - post switch)` messages on the other auto-record paths.

Companion UX polish (2026-04-24): fresh auto-record starts that already post contextual screen messages now suppress the generic `Recording STARTED` toast. This covers pad/runway launch auto-record, post-switch first-modification auto-record, and deferred EVA-from-pad auto-record; chain continuations remain unchanged because their promotion path already suppresses the generic toast.

## ~~505. Merge-time flat-trajectory preservation could keep a duplicated or non-monotonic suffix just because the rebuilt track-section payload matched the front of the list~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `SessionMergerTests.MergeTree_NonMonotonicFlatTail_RebuildsFromTrackSectionsInsteadOfPreservingBadCopy`.

**Concern:** `RecordingStore.FlatTrajectoryExtendsTrackSectionPayload()` accepted any longer flat copy whose prefix matched the rebuilt track-section payload. That let a malformed tail like `[0,10,20,0,10,20]` count as a valid extension, so merge preserved the bad flat copy instead of rebuilding from the authoritative sections.

**Fix:** flat-tail preservation now requires a safe suffix boundary that appends new points / orbit segments after the rebuilt payload and remains monotonic after dedupe stitching. If the suffix cannot satisfy that shape, merge falls back to rebuilding the flat trajectory from track sections.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~506. Headless `ParsekUI` xUnit coverage still touched Unity GUI object APIs after the shared opaque-window fix~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `ParsekUITests.NormalizeOpaqueWindowTitleTextColors_ReplacesDarkFocusedStatesWithReadableBaseColor` and `ParsekUITests.ParsekUI_Ksc_Ctor_Exposes_CareerStateWindowUI`.

**Concern:** the production title-color normalization path is pure color selection, but the xUnit regression still constructed `GUIStyle` directly in plain .NET, which trips Unity native GUI calls. The `ParsekUI` smoke test also called `Cleanup()`, and the shared opaque-window fix now tears down copied textures during cleanup, which likewise touches Unity GUI objects that do not exist in headless xUnit.

**Fix:** extracted a pure color-based `NormalizeOpaqueWindowTitleTextColors(...)` helper that the `GUIStyle` overload delegates to, so xUnit can assert the same focused/active text-color normalization without constructing `GUIStyle`. `ParsekUI` opaque-background teardown now also treats wrapped headless `SecurityException` chains as a no-op during cleanup, so the KSC smoke test can still verify the accessor wiring without crashing in teardown.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~507. The new headless body-index seam coverage did not actually hit the production resolver, and the landed surface-repair fixture forgot to install the seam at all~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `SpawnSafetyNetTests.ResolveBodyIndex_UsesResolverOverride` and `BuildValidatedRespawnSnapshot_SurfaceTerminalWithStaleOrbit_UsesEndpointSurfaceRepair`.

**Concern:** the seam test was only calling its own helper instead of `VesselSpawner.TryResolveBodyIndex(...)`, so it could pass or fail without telling us whether the production override was wired correctly. Separately, the landed surface-repair fixture resolved body names through the new seam but never installed the matching body-index override, so the `REF` rewrite stayed on the stale orbit body even though the endpoint repair path was otherwise correct.

**Fix:** the seam test now invokes the private production resolver via reflection, the landed surface-repair fixture installs `BodyIndexResolverForTesting`, and the fixture-local body-index resolver now uses the install order list directly instead of dictionary enumeration.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~508. Several remaining xUnit failures were assertion drift, not live logic regressions~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `Bug219_PopulateTerminalOrbitFromLastSegmentTests.*DoesNotOverwrite`, `BallisticExtrapolatorTests.Extrapolate_SoiTransitions_PreserveFrozenPlaybackWorldRotationAcrossSegments`, and `SceneExitFinalizationIntegrationTests.SeedPredictedSegmentOrbitalFrameRotations_PreservesBoundaryWorldRotationAcrossSegments`.

**Concern:** the two Bug219 preserve tests were filtering with `Contains("PopulateTerminalOrbitFromLastSegment")`, which also matches the newer `ShouldPopulateTerminalOrbitFromLastSegment...` preserve diagnostic and turns a passing preserve path into a false failure. The orbital-frame continuity tests were also comparing raw quaternion components on non-unit rotations, so equivalent preserved attitudes could fail the exact tuple check after canonicalization / recomposition even though the represented rotation stayed the same.

**Fix:** tightened the Bug219 filter to the exact `PopulateTerminalOrbitFromLastSegment:` production prefix, and the orbital-frame assertions now normalize and canonicalize both quaternions before comparing components. That keeps the regressions pinned to real behavior changes instead of representational float drift.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~509. Safe flat-tail detection rejected the valid “predicted orbit segment appended after the checkpoint payload” shape~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `RecordingStorageRoundTripTests.CurrentFormatTrajectorySidecar_PredictedTailBeyondTrackSections_FallsBackToFlatBinaryAndRoundTrips`.

**Concern:** the first safe-suffix hardening pass correctly rejected duplicated / non-monotonic tails, but it also rejected the valid current-format case where the flat trajectory only appends one new orbit segment immediately after the rebuilt checkpoint payload. That made the helper report no safe extension even though the writer should still use the flat-binary fallback path for the extra predicted segment.

**Fix:** `FindSafeOrbitSegmentSuffixStart()` now accepts the immediate post-payload suffix slot directly when the remainder of the flat orbit-segment list is monotonic. That keeps malformed tails rejected while preserving the intended “extra predicted orbit tail” fallback contract.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~510. A few xUnit follow-ups were still test-harness drift after the earlier cleanup pass~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `ParsekUITests.ParsekUI_Ksc_Ctor_Exposes_CareerStateWindowUI`, `SpawnSafetyNetTests.ResolveBodyIndex_UsesResolverOverride`, `BuildValidatedRespawnSnapshot_SurfaceTerminalWithStaleOrbit_UsesEndpointSurfaceRepair`, and the two Bug219 preserve tests.

**Concern:** `ParsekUI` cleanup still touched GUI-style state before the narrower destroy-site catch could run, the body-index override test was relying on `List<T>.IndexOf()` for Unity objects instead of explicit reference matching, and the Bug219 preserve tests were still matching `ShouldPopulateTerminalOrbitFromLastSegment...` because the substring filter was too broad.

**Fix:** wrapped the whole opaque-style cleanup pass in the same headless-safe guard, restored the body-index seam helper to explicit `ReferenceEquals` matching over the installed test bodies, and tightened the Bug219 negative log assertions to the exact `[Flight] PopulateTerminalOrbitFromLastSegment:` prefix.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~511. Body-index repair could still fail even after the new seam landed because Unity object equality was too brittle for headless test bodies~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `SpawnSafetyNetTests.ResolveBodyIndex_UsesResolverOverride` and `BuildValidatedRespawnSnapshot_SurfaceTerminalWithStaleOrbit_UsesEndpointSurfaceRepair`.

**Concern:** the new body-index seam covered the right production path, but the fallback still depended on Unity object equality / `IndexOf(body)` semantics. In headless seam tests, uninitialized `CelestialBody` instances can fail those equality checks even when the body is present in the injected list, so landed repair leaves the stale `ORBIT.REF` untouched.

**Fix:** `TryResolveBodyIndex()` now treats the seam as the first choice but falls back to an explicit scan of the loaded body list using `ReferenceEquals` and then stable body-name matching. That keeps the live path deterministic and makes the headless seam tests exercise the same repair outcome.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~512. The predicted-tail flat-fallback regression fixture appended its extra orbit segment at an earlier absolute UT than the checkpoint payload it was supposed to extend~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `RecordingStorageRoundTripTests.CurrentFormatTrajectorySidecar_PredictedTailBeyondTrackSections_FallsBackToFlatBinaryAndRoundTrips`.

**Concern:** after the safe-suffix hardening, the helper correctly rejected malformed non-monotonic tails. The test fixture, however, was still appending its “extra predicted tail” segment at `630 -> 930` even though the section-authoritative codec fixture lives around `20030 -> 20630`. That made the test assert a malformed earlier segment instead of a real tail beyond the track-section payload.

**Fix:** the test now derives the appended predicted segment from the current last flat orbit segment's `endUT`, so it extends the payload exactly the way the production fallback path is supposed to handle.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~513. SOI attitude preservation still drifted when a frozen playback quaternion arrived as a scaled-but-non-unit value~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `BallisticExtrapolatorTests.Extrapolate_SoiTransitions_PreserveFrozenPlaybackWorldRotationAcrossSegments`.

**Concern:** the orbital-frame path already canonicalized quaternion sign, but it kept whatever magnitude the incoming frozen playback quaternion had. A scaled-but-non-unit orbital-frame quaternion still represents the same attitude after normalization, but carrying that scale through the SOI reframe path let the preserved world rotation drift enough for the continuity regression to fail.

**Fix:** `BallisticExtrapolator` now normalizes and canonicalizes orbital-frame quaternions at the encode/decode seam, so frozen playback attitudes keep a stable unit representation across `ComputeOrbitalFrameRotationFromState()` and `ResolveWorldRotation()`.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~514. Two remaining xUnit failures were still test-harness drift, not production regressions~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `ParsekUITests.ParsekUI_Ksc_Ctor_Exposes_CareerStateWindowUI` and the two Bug219 preserve tests.

**Concern:** the `ParsekUI` KSC smoke test still called `Cleanup()` unguarded in a headless process even though the test only cares about constructor wiring, and the Bug219 negative log assertions were still matching the longer `ShouldPopulateTerminalOrbitFromLastSegment...` diagnostic because the filter was not anchored to the full production prefix.

**Fix:** the KSC smoke test now treats headless `SecurityException` teardown as irrelevant to the constructor/accessor assertion, and the Bug219 negative log assertions now match the full `[Parsek][INFO][Flight] PopulateTerminalOrbitFromLastSegment:` prefix.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~515. The new body-index seam test still used Unity null semantics, so the xUnit override declined valid synthetic bodies and left landed snapshot repair on the stale orbit~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `SpawnSafetyNetTests.ResolveBodyIndex_UsesResolverOverride` and `BuildValidatedRespawnSnapshot_SurfaceTerminalWithStaleOrbit_UsesEndpointSurfaceRepair`.

**Concern:** the production seam was already correct, but the xUnit helper backing `BodyIndexResolverForTesting` still used `body == null`. For synthetic `CelestialBody` instances created with `FormatterServices`, Unity's overloaded null semantics can report a perfectly valid test fixture as null, so the override returned `false` and the landed repair path never rewrote `ORBIT.REF`.

**Fix:** the body-index test helper now uses `object.ReferenceEquals(body, null)` and reference-based list matching, so the override consistently resolves the installed synthetic bodies and the landed surface-repair fixture exercises the real production seam.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~516. Hyperbolic predicted segments could rebuild the SOI handoff on the wrong outbound branch because true anomaly reconstruction used plain `Atan`~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `BallisticExtrapolatorTests.Extrapolate_SoiTransitions_PreserveFrozenPlaybackWorldRotationAcrossSegments`.

**Concern:** after the earlier quaternion normalization/canonicalization follow-up, the remaining SOI rotation failure was not in the attitude seam anymore. The parent-body segment itself was reconstructing its hyperbolic start state with `2 * Atan(...)`, which folds the outbound branch into the wrong quadrant for some escape states. That changed the rebuilt parent-frame velocity vector at the exact handoff UT, so the preserved world rotation was being compared against the wrong orbital frame even though the stored quaternion itself was stable.

**Fix:** `BallisticExtrapolator.TwoBodyOrbit.GetStateAtUT()` now reconstructs hyperbolic true anomaly with the equivalent `Atan2` form that preserves the correct branch/quadrant. Added a direct regression that asserts the parent-body predicted segment starts from the exact transformed SOI boundary state before checking the preserved frozen playback attitude.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~517. Headless landed snapshot repair still treated synthetic endpoint bodies as null inside the final ORBIT rewrite helpers~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `SpawnSafetyNetTests.BuildValidatedRespawnSnapshot_SurfaceTerminalWithStaleOrbit_UsesEndpointSurfaceRepair`.

**Concern:** even after the body-index resolver seam was fixed, the landed repair path still passed a synthetic `CelestialBody` into `ApplySurfaceOrbitToSnapshot()` and `ReplaceSnapshotOrbitNode()`, and those helpers were still using Unity's overloaded `body == null` check. In headless xUnit that pseudo-null check can reject a perfectly valid test fixture before the repaired `ORBIT` node is written, leaving the stale `SMA=700000` snapshot in place.

**Fix:** the headless-sensitive orbit rewrite helpers now use `object.ReferenceEquals(body, null)`, so the synthetic endpoint body reaches the real surface-orbit rewrite path and the repaired `REF` value is written through the same production helper as live KSP.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~518. Equatorial hyperbolic SOI handoffs still serialized the parent segment with the wrong periapsis heading because `argumentOfPeriapsis` was left at zero when the ascending node was undefined~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `BallisticExtrapolatorTests.Extrapolate_HyperbolicHomeEscape_PreservesParentSegmentStartState` and `Extrapolate_SoiTransitions_PreserveFrozenPlaybackWorldRotationAcrossSegments`.

**Concern:** the remaining SOI handoff failures were not in the quaternion seam anymore. The parent segment is an equatorial eccentric/hyperbolic orbit, so the ascending node is undefined, but `TwoBodyOrbit.TryCreate()` still left `argumentOfPeriapsis = 0` in that case. That discards the real periapsis heading from the eccentricity vector, so `TryPropagate(segment, startUT)` rebuilds the parent segment on the wrong in-plane orientation and the preserved frozen attitude is resolved against the wrong world state.

**Fix:** equatorial eccentric/hyperbolic segment creation now derives periapsis orientation directly from `atan2(eccentricityVector.y, eccentricityVector.x)` when the ascending node is undefined. The regression keeps asserting both the exact parent-segment start-state preservation and the frozen playback world-rotation continuity across the SOI handoff.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~519. The final SOI attitude xUnit failure was comparing exact quaternion components even when the handoff preserved the same rotation as `q` versus `-q`~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `BallisticExtrapolatorTests.Extrapolate_SoiTransitions_PreserveFrozenPlaybackWorldRotationAcrossSegments`.

**Concern:** after the hyperbolic handoff fixes, the only remaining mismatch was an exact sign-flip on the normalized quaternion (`q` versus `-q`). Those two tuples represent the same 3D rotation, so component-by-component comparison was stricter than the actual continuity contract the tests are meant to enforce.

**Fix:** the shared SOI/finalization quaternion test helpers now compare normalized rotation equivalence using the absolute quaternion dot product instead of raw component equality, so the assertions fail only when the represented rotation changes, not when the same rotation chooses the opposite sign.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~520. `FinalizeIndividualRecording` overwrites a correctly-set terminal orbit body when a same-UT orbit segment reports a different body, even if `Points` anchor the original body~~

**Source:** port attempt from the superseded `Parsek-fix-batch-terminalorbit` on 2026-04-21. When its `FinalizeIndividualRecording_LeafWithExistingTerminalOrbit_DoesNotOverwriteFromLaterOrbitEndpoint` test was run against current `main`, the assertion `Assert.Equal("Mun", rec.TerminalOrbitBody)` failed with an actual value of `"Kerbin"`.

**Concern:** with a leaf recording where `TerminalOrbitBody = "Mun"`, one `Points` entry at `ut=1000, bodyName="Mun"`, and one `OrbitSegment` on `"Kerbin"` spanning `startUT=1000, endUT=2000`, `FinalizeIndividualRecording` overwrites the cached terminal orbit from Mun to Kerbin. Main's heal path was otherwise correct for the "orbit-only stale body" case (covered by the ported `LeafWithOrbitOnlyEndpoint_HealsStaleTerminalOrbitBody` test), so the gap was specific to "the last point still anchors one body at the segment start, but the later segment claims another".

**Fix:** `FinalizeIndividualRecording()` now handles the same-UT point-anchor case before it reaches the shared `PopulateTerminalOrbitFromLastSegment()` helper. If the last point shares the last segment's `startUT`, the point body matches the cached `TerminalOrbitBody`, and the later segment reports another body, finalize keeps that cached body authoritative; if there is earlier same-body orbit evidence, finalize heals the stale tuple from the last matching-body segment instead of from the conflicting later segment. The shared helper itself is unchanged, so `#484` / `#497` keep their intended load/backfill behavior.

**Resolution:** landed on 2026-04-21 in the dedicated issue-520 worktree. Added two `FinalizeIndividualRecording` xUnit regressions (preserve and stale-tuple-heal-from-matching-body) and updated the existing in-game finalize-backfill test so the same-UT point-anchor case now asserts finalize-specific preserve logs while the orbit-only heal case still asserts the shared stale-cache repair path.

**Files:** `Source/Parsek/ParsekFlight.cs`, `Source/Parsek.Tests/Bug278FinalizeLimboTests.cs`, `Source/Parsek.Tests/BugFixTests.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~521. Main Parsek page can visibly flicker while clicking around the Parsek windows~~

**Source:** user observation from `logs/2026-04-21_2335_live-collect-script`. The package does not contain a screenshot/video, but `KSP.log` shows `CareerStateWindow: rebuilt VM ...` being emitted every ~16-25 ms for long stretches even when the data is stable (for example around 23:29:45-23:29:57 with `contracts=2/2`, `strategies=0/0`, `facilities=9`, `milestones=4/4` unchanged). The same session also keeps logging `Main window position: x=20 y=100 w=250 h=0`.

**Concern:** this did not look like the old transparent-window path from closed `#487`; it looked like the Career State VM and/or the shared main window layout was rebuilding at draw cadence instead of only on cache invalidation. That would explain why the main Parsek page flickered when the user clicked controls in Parsek-owned windows even though the underlying data was not changing.

**Fix:** `CareerStateWindowUI` was comparing the cached VM against the raw `double` live UT from `Planetarium.GetUniversalTime()`, so the supposedly cached VM rebuilt every draw frame while time advanced. The cache now expires on the next observable UI boundary instead of on every sub-frame delta, but that boundary is mode-specific: Career invalidates on displayed `F0` live-UT changes or when the next visible projected action crosses from future into current; Science Sandbox skips the hidden banner-time check and only invalidates on visible time-sensitive facility/milestone boundaries; sandbox-like modes ignore those hidden time-driven boundaries entirely. The build path still uses exact `<= liveUT` classification, so action boundaries have to invalidate precisely. Explicit cache invalidation, game-mode changes, transient fallback VMs, and backwards time jumps in modes with visible timeline state still rebuild immediately. Added headless regression coverage for the display-rounding boundary, the next-relevant-action boundary, and the rewind/mode-change cases. The `Main window position ... h=0` breadcrumb turned out to be incidental logging noise, not the visual driver.

**Status:** CLOSED 2026-04-23. Fixed for v0.9.0.

---

## ~~522. Timeline milestone rewards can still look double-counted even when the ledger itself does not warn~~

**Source:** user observation from the same `2026-04-21_2335_live-collect-script` package. When the Timeline opened at 23:30:36, `KSP.log` reported `Build complete: 35 entries (2 recording, 33 action, 0 legacy)`, and the package does not contain milestone-funds reconciliation WARNs of the kind that motivated closed `#477`. The session does, however, contain dense milestone activity (`FirstLaunch`, `RecordsSpeed`, `RecordsAltitude`, `RecordsDistance`, plus later repeatable `stays effective` rows), so it is a good repro corpus for "this looks duplicated in the Timeline".

**Concern:** this currently looks more like a presentation regression than a real funds over-credit. Closed `#464` fixed the old gray `GameStateEvent` shadow rows in Timeline Details, and closed `#477` fixed the false-positive milestone over-attribution. This package does not show either exact old signal. If the Timeline still looks double-counted, suspect duplicate/ambiguous milestone action rows or text formatting in the Timeline render path before touching the ledger math again.

**Fix:** Timeline milestone presentation now compacts same-moment milestone rows for the same milestone into one richer entry instead of rendering both near-duplicates. The pass matches rows within the same 0.1s UT window, still works when another same-timestamp row sits between the duplicates, keeps the union of non-zero funds/rep/science reward legs, and leaves genuinely conflicting reward values split. Timeline milestone text also now includes science rewards, closing the last missing-leg path that made the same milestone look like two separate payouts.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3 by `#545`.

---

## ~~523. Two SPACECENTER strategy canaries fail because strategy readiness never hydrates~~

**Source:** `logs/2026-04-21_2335_live-collect-script/parsek-test-results.txt` records two SPACECENTER failures: `FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` and `FlightIntegrationTests.FailedActivation_DoesNotEmitEvent`, both with `StrategyLifecycle readiness never stabilized: Administration.Instance is null (stock Strategy.CanBeActivated dereferences it before Administration finishes hydrating)`. The same package's `KSP.log` also shows an early `[StrategySystem]: Found 0 strategy types` during KSC setup.

Follow-up source: `logs/2026-04-23_1829_logs-package/parsek-test-results.txt` again recorded two SPACECENTER failures, but with a narrower shape. `ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` expected `Strategy.IsActive` to remain true after `Strategy.Activate()` returned true; actual was false after the test yielded one frame, even though `KSP.log` shows Parsek emitted `StrategyActivated key='BailoutGrant'` and wrote the `StrategyActivate` ledger action first. `FailedActivation_DoesNotEmitEvent` then expected a hydrated Administration singleton; actual was `Administration.Instance` null after the previous test destroyed its hidden canvas and Unity completed that destruction during the next warmup window.

**Root cause (2026-04-22, refined 2026-04-23):** local stock decompile showed that this was not "late KSC hydration" in the generic sense. `KSP.UI.Screens.Administration.Instance` is the Administration window singleton, and `Strategies.Strategy.CanBeActivated()` / `Strategy.Activate()` both dereference it. In plain SPACECENTER that singleton does not exist until the Administration canvas is instantiated. Follow-up decompile of `KSP.UI.Screens.AdministrationSceneSpawner` also showed that the stock `onGUIAdministrationFacilityDespawn` path is not test-neutral: it overwrites `persistent.sfs` via `GamePersistence.SaveGame("persistent", ..., OVERWRITE)` and calls `MusicLogic.fetch.UnpauseWithCrossfade()`.

The 2026-04-23 18:29 package showed the remaining failures were harness races rather than a missing `StrategyLifecyclePatch` emission. The first canary yielded after the synchronous stock `Activate()` call, giving the hidden Administration UI / stock strategy row update a frame to reconcile and clear `IsActive` before the assertion. The second canary could enter with `Administration.Instance` apparently present, skip hidden-canvas creation, then see the singleton become null during warmup because Unity completes `Object.Destroy()` at frame end.

**Fix:**

- `RuntimeTests.WaitForStableActivatableStockStrategy(...)` now creates a hidden stock Administration canvas when the SPACECENTER career tests need strategy readiness and `Administration.Instance` is still null.
- The helper uses its own bounded hydration-frame wait, keeps the existing readiness probe on top of that stock singleton, and destroys the hidden canvas directly in teardown instead of firing the stock despawn event.
- `StrategyLifecycleProbeSupport` now uses dedicated hydration diagnostics/logs (including a precise timeout reason), and the xUnit coverage splits the request predicate cases instead of packing three false cases into one `[Fact]`.
- Follow-up: `WaitForStableActivatableStockStrategy(...)` now re-runs the hidden Administration hydration check after the warmup frames, so a singleton destroyed at the end of the previous canary gets recreated before the readiness poll starts.
- Follow-up: `ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` now verifies `StrategyActivated` / `StrategyDeactivated` emission and `IsActive` state in the same frame as the stock `Activate()` / `Deactivate()` calls, matching the synchronous Harmony postfix contract instead of treating next-frame stock UI reconciliation as part of the patch behavior.
- Review follow-up: `EnsureAdministrationSingletonForStrategyProbe(...)` now takes an `attemptTag` parameter so the "creating hidden Administration canvas for readiness probe" info log and its associated warnings/destruction logs differentiate the `pre-warmup` and `post-warmup` attempts in the same canary, plus an xmldoc documenting the re-entrant destruction-then-rebuild contract.
- Review follow-up: the post-warmup rehydrate decision is now gated by a pure `StrategyLifecycleProbeSupport.ShouldRehydrateAdministrationAfterWarmup(canvasExists, administrationAvailable)` predicate with full 4-case xUnit truth-table coverage in `StrategyLifecycleProbeSupportTests`, and the paired `WaitForStableActivatableStockStrategy` comment now names the "prior StrategyLifecycle canary's Dispose tear-down" explicitly.

**Validation (2026-04-23):**

- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter StrategyLifecycleProbeSupportTests` — passed (`25` tests after the predicate-extraction review follow-up; `21` before).
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~StrategyLifecycleProbeSupportTests|FullyQualifiedName~StrategyCaptureTests"` — passed (`44` tests).
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` — passed (`8306` tests, `0` failed, `0` skipped after the rebase onto origin/main).
- `dotnet build Source/Parsek/Parsek.csproj --no-restore` — compile passed (`0` errors); the post-build KSP deploy copy emitted locked-DLL warnings because the running game had `GameData\Parsek\Plugins\Parsek.dll` mapped.
- Live SPACECENTER rerun was not executed from this worktree because the repo only exposes the in-game runner via manual KSP GUI interaction (`Ctrl+Shift+T` in SPACECENTER); there is no non-interactive launcher/test harness path to drive that run from this terminal session.

**Files:** `Source/Parsek/InGameTests/StrategyLifecycleProbeSupport.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek.Tests/StrategyLifecycleProbeSupportTests.cs`, `Source/Parsek/Parsek.csproj`.

**Status:** CLOSED 2026-04-23. Fixed for v0.9.0. Headless validation is clean; live SPACECENTER evidence still requires a manual KSP run.

---

## 524. ~~Timeline row action buttons use inconsistent widths (`W=30`, `FF=35`, `R=25`, `L=25`)~~

**Source:** user observation plus code read in `Source/Parsek/UI/TimelineWindowUI.cs`.

**Concern:** the row-action column visibly jitters because the watch, fast-forward, rewind, and loop buttons all use different hard-coded widths. The mismatch is cosmetic, but it makes dense Timeline rows harder to scan and undermines the otherwise deliberate table alignment.

**Files:** `Source/Parsek/UI/TimelineWindowUI.cs`, `Source/Parsek.Tests/TimelineWindowUITests.cs`.

**Resolution (2026-04-22):** CLOSED. The current tree already had the short Timeline row actions (`W`, `FF`, `R`, `L`) aligned at the `FF` width (35px); this follow-up makes that existing contract explicit with a shared row-action width helper, keeps `GoTo` on its intentional wider width, and adds focused xUnit coverage so future UI edits cannot quietly drift the short buttons back apart.

**Status:** ~~TODO / UI polish. Cheap fix, low risk.~~ Closed.

---

## ~~525. Watch-mode ghost explosions can still end up visually buried if FX anchors to the raw ghost root instead of a clearance-checked position~~

**Source:** user observed multiple "explosion happened underground" cases in watch mode. The current `2026-04-21_2335_live-collect-script` package did not capture the underground variant directly, but it did capture a watched destroyed ghost (`"x"`) exploding at 23:32:42 immediately after a terrain-correction log (`Ghost terrain clamp: alt=65.3 terrain=64.8 -> 66.8 (clearance=2.0m)`).

**Concern:** `GhostPlaybackEngine.TriggerExplosionIfDestroyed()` currently anchors the FX at `state.ghost.transform.position`. If watch mode or a late playback frame ever leaves the ghost root below terrain for even one frame, the stock/custom explosion FX and the watch hold will both anchor underground. This package narrows the bug even though it does not show the bad frame itself: explosion placement still trusts the raw ghost root instead of re-validating clearance at fire time.

**Files:** `Source/Parsek/GhostPlaybackEngine.cs`, `Source/Parsek/TerrainCorrector.cs`, `Source/Parsek/ParsekFlight.cs`, plus a new in-game regression in the terrain/watch test coverage.

**Fix (2026-04-22):** flight playback now routes destruction anchoring through the host positioner before spawning the explosion FX. `ParsekFlight` re-resolves the explosion anchor against the current body/PQS terrain and the same distance-aware watch clearance floor used by landed ghost terrain correction, then writes the corrected world position back to the ghost before the engine emits stock/custom explosion FX and the loop/overlap watch hold payloads. That keeps the visual blast and the watch camera bridge anchored to the same terrain-safe point instead of the raw buried root.

**Verification:** added headless coverage for the body-name resolver that chooses the explosion-anchor body, and an in-game `TerrainClearance` regression that drives the loop-explosion engine path in Flight and asserts the emitted watch hold anchor and loop-restart explosion payload both use the same terrain-clamped position above `terrain + clearance`.

**Status:** ~~OPEN. User-observed; current package provides a nearby watch-destruction repro and code-path confirmation.~~ Closed.

---

## ~~526. Timeline FF can falsely auto-start a new recording on the real pad vessel after the time jump~~

**Source:** `logs/2026-04-21_2335_live-collect-script/KSP.log` around 23:32:24-23:32:25. After `Timeline FF button clicked: "x"` and `FastForwardToRecording: jumping to UT=102.9`, the real vessel `r0` is put on rails/unrails for the forward jump. Immediately after, Parsek starts a fresh tree recording on `r0`: `Recording started: vessel="r0"` plus `Auto-record started (PRELAUNCH -> FLYING)`. The new recording seeds `Start location captured: body=Kerbin, biome=Shores, situation=Flying, launchSite=Launch Pad`, then 0.5s later the recorder corrects back to `SurfaceStationary`.

**Concern:** the FF/time-jump path transiently makes the real launchpad vessel look like a `PRELAUNCH -> FLYING` launch transition, so the normal auto-record logic creates a bogus recording even though the vessel is just sitting on the pad. This is a direct regression with a tight repro sequence in the collected package.

**Fix:** Time-jump launch-auto-record suppression now covers both `ExecuteForwardJump()` and `ExecuteJump()` while the jump is in progress and for a short shared frame-bounded tail afterward. `OnVesselSituationChange()` threads that transient into `EvaluateAutoRecordLaunchDecision()`, logs the skip at `INFO`, and ignores the stock `PRELAUNCH/LANDED -> FLYING` callback instead of starting a new tree recording on the real pad vessel. Added headless coverage for the shared suppression boundary plus an isolated FLIGHT canary that fast-forwards from a real pad vessel, asserts the suppression path fires, and verifies no auto-record starts.

**Resolution (2026-04-23):** CLOSED for v0.9.0. Investigation confirmed the bogus recording was coming from stock time-jump situation-change noise, not from post-switch auto-record or playback spawn ownership. Follow-up hardening widened the initial proof-of-concept from a 2-frame FF-only window into one shared frame-bounded suppression path for both Timeline FF and Real Spawn Control jumps, so earlier-save loads cannot resurrect stale suppression and normal launches stay unchanged outside the jump transient.

**Files:** `Source/Parsek/ParsekFlight.cs` (`OnVesselSituationChange` / `EvaluateAutoRecordLaunchDecision`), `Source/Parsek/TimeJumpManager.cs` (shared Timeline FF / Real Spawn Control jump suppression), `Source/Parsek/InGameTests/RuntimeTests.cs`, `docs/dev/manual-testing/test-auto-record.md`.

**Status:** CLOSED 2026-04-23. Fixed for v0.9.0.

---

## ~~527. Rewind's deferred recalc can drop `utCutoff` and restore future funds/contracts before replay catches up~~

**Source:** `logs/2026-04-21_2335_live-collect-script/KSP.log` around 23:30:25-23:30:28. The rewind path first does the expected cutoff walk (`cutoffUT=19.159999999999467`), dropping funds from `53861.7` to `21495.0` and removing two active contracts. About five seconds later, a deferred FLIGHT recalc runs with `cutoffUT=null` and restores the end-of-timeline funds/contracts before any replay has advanced.

**Concern:** one rewind caller is re-entering `RecalculateAndPatch()` without forwarding the rewind cutoff. That makes career state snap back toward the future timeline immediately after rewind, explains the new `PatchFunds: suspicious drawdown` warning in the same package, and breaks the expected "rewind means pre-cutoff state until replay progresses" model.

**Files:** `Source/Parsek/ParsekScenario.cs`, `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek.Tests/RewindUtCutoffTests.cs`.

**Fix:** the generic scene-load follow-up path now applies the current-UT cutoff only in the specific post-rewind FLIGHT case with no pending/live restore state, via `ParsekScenario.ShouldUseCurrentUtCutoffForPostRewindFlightLoad(...)` and `LedgerOrchestrator.RecalculateAndPatchForPostRewindFlightLoad(loadedUT)`. That keeps the later FLIGHT `OnLoad` pass aligned with the rewound clock without bypassing normal pending-tree patch deferral or same-branch repeatable-record preservation. The dispatch `Info` log now records every decision input, the other deferred `ParsekScenario.RecalculateAndPatch()` sites were audited as true revert / initial-load full-ledger paths, and `Source/Parsek/InGameTests/RuntimeTests.cs` now contains a manual-only live rewind canary for this exact regression.

**Status:** CLOSED 2026-04-23. Fixed for v0.9.0.

---

## ~~558. Rewind top-bar funds/science could show gross or future-inflated values instead of the spendable balance~~

**Source:** follow-up investigation of `logs/2026-04-23` rewind/cutoff behavior and the April 23 design pass. The visible ledger walk was correctly scoped to the rewound UT, but the top-bar values still needed to represent what the player could actually spend at that moment.

**Concern:** after a rewind, the top bar should show the spendable funds/science at the current UT, not a future value and not a blunt subtraction of all future spending. Future spendings should reserve current headroom only when the projected balance would dip below the current running balance; future earnings that arrive before those spendings should be allowed to cover them. Reputation stays current-UT only because it is not a spend-blocking resource.

**Fix:** cutoff recalculation now keeps all visible/current aggregates filtered to the current UT, then runs an isolated full-ledger cashflow projection to install the spendable top-bar funds/science value. The projection follows the committed future cashflow in chronological order and exposes the minimum balance reachable from the current UT forward, clamped to the current running balance. The isolated projection uses cloned modules and suppressed logging so future actions do not leak through the live rewind walk.

**Files:** `Source/Parsek/GameActions/RecalculationEngine.cs`, `Source/Parsek/GameActions/IResourceModule.cs`, `Source/Parsek/GameActions/FundsModule.cs`, `Source/Parsek/GameActions/ScienceModule.cs`, `Source/Parsek/GameActions/ContractsModule.cs`, `Source/Parsek/GameActions/StrategiesModule.cs`, `Source/Parsek/ParsekLog.cs`, `Source/Parsek.Tests/RewindUtCutoffTests.cs`, `docs/parsek-game-actions-and-resources-recorder-design.md`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3.

---

## ~~568. Real-spawned vessel post-resume stands on its side when physics activates~~

**Source:** filed on `bug/scene-enter-resume-recording` after `logs/2026-04-23_2333_spawn-orientation` and reproduced in `logs/2026-04-24_0004_orientation-regression`. The 2026-04-24 in-flight real-spawn logged `srfRel=-0.000111888832,-0.133055791,-0.000464609941,0.991108477` for an upright rover with about 15 degrees of yaw, then wrote `world=0.000474858942,-0.885830998,5.37633787E-05,-0.464007854` into the snapshot `rot` field. KSP immediately classified the spawned LANDED vessel through a non-active `0 -> ORBITING` transition, matching the observed "on its side" physics activation.

**Root cause:** Parsek-authored ProtoVessel snapshots were treating `VESSEL.rot` as a Unity world-space transform rotation and pre-composing `body.bodyTransform.rotation * srfRelRotation` before `ProtoVessel.Load()`. The in-repo ProtoVessel decompilation note shows the opposite contract: `rot` is parsed into `ProtoVessel.rotation`, and `ProtoVessel.Load()` assigns that value directly to `vesselRef.srfRelRotation`. Writing the composed value double-applied the body frame when KSP loaded the real vessel.

**Fix:** `VesselSpawner` now writes the sanitized recorded surface-relative quaternion directly to `VESSEL.rot` for all ProtoVessel spawn-node paths: normal snapshot respawns, `SpawnAtPosition`, EVA/breakup snapshot prep, chain-tip spawns through `VesselGhoster`, and flag ProtoVessel spawns. Live ghost `Transform.rotation` placement still uses `body.bodyTransform.rotation * srfRelRotation`; only ProtoVessel node authoring changed. `SpawnRotationInGameTests` now assert the surface-relative ProtoVessel invariant for Kerbin and Mun fixtures, null-body rotation prep, `SpawnAtPosition`, and snapshot override rotation rewrites.

**Files:** `Source/Parsek/VesselSpawner.cs`, `Source/Parsek/VesselGhoster.cs` (audited caller), `Source/Parsek/GhostVisualBuilder.cs`, `Source/Parsek/TrajectoryPoint.cs`, `Source/Parsek/InGameTests/SpawnRotationInGameTests.cs`, `Source/Parsek/InGameTests/ExtendedRuntimeTests.cs`, `AGENTS.md`, `.claude/CLAUDE.md`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-24. Fixed for v0.9.0.

---

## ~~569. Time-jump chain-tip spawns can leave the materialized recording unmarked~~

**Source:** spawn audit while validating #568 / PR #519. `TimeJumpManager.SpawnCrossedChainTips(...)` called `VesselGhoster.SpawnAtChainTip(...)` and removed the chain key after a non-zero spawned PID, but the time-jump caller did not mirror the normal Flight caller's `VesselSpawned=true` / `SpawnedVesselPersistentId=<pid>` update onto the tip recording.

**Concern:** after a time jump materialized a crossed chain tip, Parsek metadata could still describe the tip as ghost-only/unspawned. Later spawn-death tracking, watch handoff, background-map checks, and spawn policy would have to rediscover the live vessel by adoption instead of following the spawned PID.

**Fix:** `VesselGhoster` now marks the chain-tip recording spawned immediately after successful real-vessel materialization on the normal, blocked-retry, and trajectory-walkback chain-tip paths. `TimeJumpManager` now names its returned values as chain dictionary keys instead of spawned vessel PIDs, and the blocked-retry path no longer logs a successful spawn before checking for `pid=0`.

**Files:** `Source/Parsek/VesselGhoster.cs`, `Source/Parsek/TimeJumpManager.cs`, `Source/Parsek.Tests/VesselGhosterTests.cs`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-24. Fixed for v0.9.0.

---

## ~~565. Continued scene-enter resume replay spawns a prior endpoint as an intermediate vessel~~

**Source:** `logs/2026-04-24_1928_bug-559-intermediate-spawn-after-resume/KSP.log`. The player rewound `Crater Crawler` (`Timeline rewind button clicked` at line 8780, confirmed at line 8781), but KSP loaded `Butterfly Rover.craft` as the scene-entry active vessel at line 8988. During replay, #226's duplicate-source bypass then matched the non-target active Butterfly source PID at lines 9656-9657 and spawned the standalone Butterfly endpoint at line 9677. On the next replay, that old standalone Butterfly recording spawned again at UT 61.1 (`Vessel spawn for #0 (Butterfly Rover) pid=3394657290` at line 13067) before the continued Crater/Butterfly tree reached its final end around UT 115.8.

**Root cause:** the replay duplicate-source exception was scoped only to `SceneEntryActiveVesselPid` / current active vessel PID, not to the recording the user actually rewound. That let any committed recording whose source PID matched the scene-entry active vessel spawn a duplicate, even when it was not the rewind target. After the player continued from that spawned endpoint, the older terminal recording also remained a normal spawnable timeline endpoint; `ResetAllPlaybackState()` cleared transient `VesselSpawned` / `SpawnedVesselPersistentId` on later rewinds, so the old endpoint could materialize again before the newer continuation reached its final spawn.

**Fix:** launch-point rewind now arms a replay-target source PID, and `VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(...)` rejects #226 duplicate-source bypasses for non-target recordings while the rewind replay is active. When a newly committed tree continues a vessel that was previously materialized by an older recording, `RecordingStore` persists `terminalSpawnSupersededBy=<continuedRecordingId>` on the old endpoint; `GhostPlaybackLogic` and `TimelineBuilder` keep its ghost playback but suppress the terminal real-vessel spawn and spawn-row entry. The target scope intentionally survives committed-list reload during `ParsekScenario.OnLoad` and is cleared only by real commit/discard/clear paths; `ResetAllPlaybackState()` also repairs the already-polluted saved shape where the old endpoint was spawned a second time and no longer carries the PID that the continuation used.

**Files:** `Source/Parsek/VesselSpawner.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek/RecordingTree.cs`, `Source/Parsek/Recording.cs`, `Source/Parsek/GhostPlaybackLogic.cs`, `Source/Parsek/Timeline/TimelineBuilder.cs`, `Source/Parsek.Tests/VesselSpawnerExtractedTests.cs`, `Source/Parsek.Tests/TreeCommitTests.cs`, `Source/Parsek.Tests/ChainSpawnSuppressionTests.cs`, `Source/Parsek.Tests/TimelineBuilderTests.cs`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-24. Fixed for v0.9.0.

---

## ~~566. Spawn audit follow-ups still left KSC and chain-tip materialization on older respawn paths~~

**Source:** 2026-04-24 Parsek spawn audit across `VesselSpawner`, `ParsekFlight.SpawnTreeLeaves`, `ParsekKSC.TrySpawnAtRecordingEnd`, `VesselGhoster` chain-tip spawns, `GhostVisualBuilder` flag spawns, and the related design notes.

**Concern:** even after the in-flight EVA / breakup stale-orbit fixes, several secondary materialization paths still diverged from the shared spawn contract. KSC materialization still prepared a raw snapshot override and called `RespawnVessel` directly, chain-tip blocked/walkback spawns still relied on the older mutate-then-respawn pattern, blocked-clear chain-tip rechecks only used the propagated position for collision testing instead of for the actual materialization state, and failed `ProtoVessel.Load()` on several spawn paths could leave orphaned `ProtoVessel` entries behind in `flightState.protoVessels`.

**Fix:** failed `ProtoVessel.Load()` cleanup is now centralized in `VesselSpawner.CleanupFailedSpawnedProtoVessel(...)` and applied to normal respawns, `SpawnAtPosition`, and flag spawning. `ParsekFlight.SpawnTreeLeaves` now routes through `SpawnOrRecoverIfTooClose`, KSC materialization now prepares a private snapshot copy and uses the same endpoint/rotation/EVA-breakup spawn prep plus `SpawnAtPosition` / validated-respawn split as flight, and `VesselGhoster` now routes chain-tip normal spawns, blocked-clear spawns, and walkback spawns through explicit resolved spawn state. Chain-tip walkback now uses the subdivided walkback helper with interpolated trajectory points, while the older point walkback remains only as a body-resolution fallback.

**Files:** `Source/Parsek/VesselSpawner.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/ParsekKSC.cs`, `Source/Parsek/VesselGhoster.cs`, `Source/Parsek/SpawnCollisionDetector.cs`, `Source/Parsek/GhostVisualBuilder.cs`, `Source/Parsek.Tests/VesselSpawnerExtractedTests.cs`, `Source/Parsek.Tests/EndOfRecordingWalkbackTests.cs`, `docs/dev/todo-and-known-bugs.md`, `docs/dev/done/plans/eva-spawn-position-fix.md`, `docs/dev/done/todo-and-known-bugs-v2.md`.

**Status:** CLOSED 2026-04-24. Fixed in the `spawn-audit-fixes` worktree; full build/test verification remains blocked on the local .NET Framework 4.7.2 targeting pack / KSP dependency environment.

---

## ~~528. Launchpad science gathered before recording start is still being committed onto the later flight recording~~

**Source:** the same package records launchpad science subjects before `r0` starts, then later commits those `KerbinSrfLandedLaunchPad` subjects under recording id `3c32a9406c044f3daf00c79d0852dbf3` / `startUT=29.16`. The resulting run also emits the familiar science-mismatch warnings: `Earnings reconciliation (sci): store delta=7.7 vs ledger emitted delta=11.0`, plus post-walk misses for `ScienceTransmission` / `VesselRecovery`.

**Root cause:** commit-time science ownership still treated the entire `PendingScienceSubjects` batch as belonging to the recording being committed. For launchpad science captured before the true recording window opened, `ConvertScienceSubjects` still accepted subjects carrying that later recording id instead of rejecting the stale subject, and the tree-commit path later routed the whole pending-science snapshot to one arbitrary owner recording, which could drop valid tagged science from earlier recordings in the same tree once cross-recording rejection was tightened. `RecordingStore` had also been mirroring the raw stale batch into `GameStateStore.committedScienceSubjects` before the ledger conversion even ran.

**Fix:** `ConvertScienceSubjects` now rejects tagged subjects whose `captureUT` is invalid, predates the owning recording window, or belongs to another recording, while untagged captures must already fall inside that same window. Tree, chain, and fallback standalone commits now all route per-recording subsets using the same gap-adjusted start window, remove only the subset that actually committed, leave untouched pending science visible after pre-ledger failures instead of clearing it prematurely, and mirror `committedScienceSubjects` only after the matching `ScienceEarning` actions are safely in the ledger.

**Files:** `Source/Parsek/GameStateRecorder.cs`, `Source/Parsek/GameActions/GameStateEventConverter.cs`, `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek/GameStateStore.cs`, `Source/Parsek/ChainSegmentManager.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek.Tests/GameStateEventConverterTests.cs`, `Source/Parsek.Tests/PendingScienceSubjectsClearTests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.9.0.

---

## ~~529. Stable-terminal landed re-snapshots can still persist a stale orbital `ORBIT` block~~

**Source:** the finalized sidecar `parsek/Recordings/a31472b008f042848e868bf1759e1259_vessel.craft.txt` from `logs/2026-04-21_2335_live-collect-script` has `sit = LANDED` and `skipGroundPositioning = True`, but its `ORBIT` block still contains a stale orbital tuple (`SMA = 300816.6`, `ECC = 0.9948`, `REF = 1`). `KSP.log` shows the recording being finalized via the stable-terminal re-snapshot path immediately beforehand.

**Concern:** closed `#479` normalized unsafe `sit` values during stable-terminal persistence, but the current persistence path only rewrites situation metadata. The surface-orbit repair logic still lived only in spawn-time healing, so landed/splashed stable-terminal sidecars could remain internally contradictory until a later repair path touched them.

**Fix:** landed/splashed `BackupVessel()` snapshots now normalize through the shared live-backup path instead of only the stable-terminal finalize helper, so finalize persistence, limbo pre-capture, split/chain snapshots, and the other live snapshot call sites all rewrite stale `ORBIT` nodes to the canonical surface tuple for the live body. The rewrite is now logged explicitly, and spawn validation still treats same-body landed/splashed orbital tuples as malformed surface data and rewrites them from the recorded or snapshot surface coordinates so older bad sidecars self-heal when they spawn.

**Files:** `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/VesselSpawner.cs`, `Source/Parsek.Tests/Bug278FinalizeLimboTests.cs`, `Source/Parsek.Tests/SpawnSafetyNetTests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.9.0.

---

## ~~530. Timeline `W` can open in a false disabled state until the lazy ghost build finishes~~

**Source:** when Timeline opens in `logs/2026-04-21_2335_live-collect-script`, `KSP.log` records `Timeline watch button "r0" ... disabled (no ghost)` for the same recording that becomes watchable less than a second later once the ghost finishes building/spawning. No user context changed; the row just self-corrected when the ghost materialized.

**Concern:** initial watchability was being computed from a pending ghost shell that existed before the lazy snapshot build finished, but that shell had no seeded playback body metadata yet. The Timeline row could therefore briefly advertise a false "not watchable" state on first open even though the recording became watchable as soon as the build completed.

**Fix:** pending ghost shells now resolve and store playback interpolation metadata from the trajectory at spawn time, so same-body watch eligibility is stable before the split build completes.

**Files:** `Source/Parsek/GhostPlaybackEngine.cs`, `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0.

---

## ~~531. Destroyed recordings are still being diagnosed as `no vessel snapshot` instead of `vessel destroyed`~~

**Source:** the package's playback loop repeatedly logs `Spawn suppressed ... no vessel snapshot` for both committed recordings, even though the same session reports the timeline contains only destroyed recordings. The behavior is stable across many suppression cycles.

**Concern:** `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(...)` currently checks `rec.VesselSnapshot == null` before `rec.VesselDestroyed`, so destroyed recordings get the wrong suppression reason. This does not change the spawn outcome, but it hides the real state during FF/watch investigations and adds misleading playback noise.

**Fix:** `ShouldSpawnAtRecordingEnd(...)` now checks `rec.VesselDestroyed` before the missing-snapshot guard, so destroyed recordings without a preserved snapshot still report `vessel destroyed`. Focused regressions pin both the direct playback/rewind helper and the KSC wrapper with the exact `VesselDestroyed=true` + `VesselSnapshot=null` shape.

**Files:** `Source/Parsek/GhostPlaybackLogic.cs`, `Source/Parsek.Tests/RewindTimelineTests.cs`, `Source/Parsek.Tests/KscSpawnTests.cs`.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0.

---

## ~~532. Same-UT tech unlock bursts can temporarily refund science until the `TechResearched` action lands~~

**Source:** `logs/2026-04-21_2335_live-collect-script/KSP.log` shows `basicRocketry` being allowed, then `PatchScience: 17.1 -> 22.1`, and only later the matching `TechResearched` ledger event. The same pattern repeats for `engineering101`: allow tech, patch science back up, then record the action later.

**Concern:** during bursty R&D activity, KSP's science pool is being patched upward in the gap between stock unlock and the recorder/ledger catching up. That means the player can briefly see refunded science or even try to re-spend it while the action stream is still converging.

**Fix:** `LedgerOrchestrator` now measures the recent unmatched `ScienceChanged(RnDTechResearch)` debit against landed KSC `ScienceSpending` actions in the same 0.1s window, and `KspStatePatcher.PatchScience()` subtracts that live-only gap from its upward patch target. The pool stays at the already-deducted stock value until the matching `TechResearched` action lands, instead of briefly refunding the gap.

**Files:** `Source/Parsek/GameActions/KspStatePatcher.cs`, `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek.Tests/KspStatePatcherTests.cs`, `Source/Parsek.Tests/LedgerOrchestratorTests.cs`.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0.

---

## ~~533. `ContractAccepted -> ContractAccept` conversion drops `contractType` on the ledger path~~ CLOSED 2026-04-22

**Source:** replay in `logs/2026-04-21_2335_live-collect-script/KSP.log` logs both accepted launch-site test contracts as `type=''`, and the committed `ledger.pgld` actions contain title/deadline/advance/penalties but no `contractType`. The raw stored contract snapshots still carry `type = PartTest`, so the type exists at capture time and is being lost during conversion.

**Concern:** `GameStateRecorder.OnContractAccepted()` preserves the full contract snapshot, but `GameStateEventConverter.ConvertContractAccepted()` only reads title/deadline/funds/penalties and never populates `GameAction.ContractType`. `PatchContracts` currently survives because it re-reads the snapshot, but any UI/rule/path that relies on `GameAction.ContractType` already sees empty data.

**Files:** `Source/Parsek/GameStateRecorder.cs`, `Source/Parsek/GameActions/GameStateEventConverter.cs`, `Source/Parsek/GameActions/ContractsModule.cs`, `Source/Parsek/GameActions/GameActionDisplay.cs`.

**Fix:** `GameStateRecorder.OnContractAccepted()` now writes the accepted contract's `type=` into the structured event detail using the same value saved in the contract snapshot, and `GameStateEventConverter.ConvertContractAccepted()` now populates `GameAction.ContractType`, falling back to `GameStateStore.GetContractSnapshot(contractId)` for pre-fix events that still lack the new detail token. Regression coverage pins both the direct-detail path and the snapshot-backfill path.

**Status:** ~~OPEN. Data-loss bug in the ledger conversion path.~~ Closed 2026-04-22. Fixed for v0.9.0.

---

## ~~534. Returning to a spawned chain-tip vessel can miss the vessel-switch restore and strand the next continuation outside the existing mission tree~~

**Source:** `logs/2026-04-22_0012_followup-log-sweep/KSP.log` around 23:56:39-00:00:38. When switching back to the spawned Mun-orbit `Kerbal X`, `ParsekScenario` logs `vesselSwitchPending flag stale ... treating FLIGHT→FLIGHT as quickload, not vessel switch`. The follow-up `OnFlightReady` state is still `mode=none tree=-`, even though scene entry active vessel pid `2641112149` is the already-spawned chain tip. The later Mun landing logs `OnVesselSituationChange: not a launch transition (SUB_ORBITAL -> LANDED)`, and the only new recording that starts afterward is a fresh single-node EVA tree for Bob Kerman.

**Concern:** the FLIGHT→FLIGHT return-to-vessel path can lose the pending tree restore/promotion for a spawned chain tip, so the later continuation never rejoins the existing `Kerbal X` mission tree/group. Once that tree context is lost, landing or engine-burn follow-up on the returned vessel no longer auto-starts a chained continuation; only the separate EVA auto-record path re-arms.

**Files:** `Source/Parsek/ParsekScenario.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek.Tests/VesselSwitchTreeTests.cs`, `Source/Parsek/InGameTests/ExtendedRuntimeTests.cs`.

**Resolution:** fixed 2026-04-22 in the dedicated `bug/534-chain-tip-restore` worktree. The failing path was not a missing `LimboVesselSwitch` dispatch: by the time `OnFlightReady` ran, the pending tree was already gone and only the committed tree copy remained. `ParsekFlight` now detects that scene-entry active vessel PID against committed spawned recordings, pre-transitions the committed tree back into the same live vessel-switch shape the in-session path expects, clears any stale `BackgroundMap` entry still keyed by the recording's historical PID instead of the live spawned PID, detaches the matched tree from committed storage before restoring it live, and restores the tree immediately on `OnFlightReady` (with the existing Update-time recovery loop as a late-active-vessel safety net). Returns to a spawned background member promote cleanly after that stale-entry cleanup; returns to the committed active member resume that same recording directly; and the later recommit still goes through the normal uncommitted-tree path instead of tripping duplicate-tree guards. This keeps `#534` narrowly on the spawned-chain-tip restore path and leaves the broader first-meaningful-modification auto-resume gap to `#546`.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~535. Tracking Station can show a future Mun-orbit chain tip before the current ghost has actually reached the Mun~~

**Source:** `logs/2026-04-22_0012_followup-log-sweep/KSP.log` around 00:01:42-00:02:07. Tracking Station startup immediately creates recording `#3` as a ghost vessel on `body=Mun` from terminal orbit data, then still draws atmospheric markers `#0` and `#1` over Kerbin, and later creates recording `#1` as a Kerbin ghost-map vessel from the current segment.

**Concern:** Tracking Station was mixing the future chain tip's terminal orbit with the earlier active leg, so the vessel list could advertise a second `Kerbal X` as already "in Mun orbit" before the current ghost had actually reached the Mun. `CreateGhostVesselsFromCommittedRecordings()` instantiated tip recordings from `HasOrbitData(rec)` even when current UT was still on an earlier chain segment.

**Fix:** tracking-station ghost creation now resolves a single source of truth per recording: use the currently visible orbit segment when one exists, skip future terminal-orbit tuples before the recording has activated or while it is still in progress, and only fall back to terminal orbit after the recording's own `EndUT`. The follow-up also restores the `KSP.log` trail for `ResolveTrackingStationGhostSource()` and splits `before-activation` / `before-terminal-orbit` out of the startup `noOrbit` summary bucket. Headless regressions now cover future-tip suppression, segment-vs-terminal precedence, post-`EndUT` terminal fallback, the decision logs, and the startup summary buckets.

**Files:** `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek.Tests/GhostMapPresenceTests.cs`. No `RuntimeTests` change landed here because the regression is fully covered at the pure decision/logging layer.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0.

---

## ~~536. Tracking Station can drop the current atmospheric continuation at the Kerbin-exit handoff~~

**Source:** the same package logs `Drawing atmospheric marker #2 "Kerbal X" ... alt=69999` at 00:03:47, then immediately removes recording `#1` with `Removed ghost map vessel for recording #1 ... reason=tracking-station-expired` at 00:03:49. No same-moment replacement current-phase marker/orbit handoff is logged for the in-progress continuation before the much later Mun-leg visibility resumes.

**Concern:** the Kerbin-exit handoff in Tracking Station can drop the current ghost entirely at the boundary between atmospheric-marker playback and orbit-map lifecycle. The package shows the removal, but not a synchronized successor for the current continuation, matching the user report that the icon vanished even though the craft was still on its way back out of atmosphere toward Mun transfer.

**Fix:** Tracking Station suppression is now current-UT-aware instead of hiding every recording that merely has a child. The startup cache and lifecycle tick now compute the same time-aware suppression set, the lifecycle retires already-existing parent ghosts when a child with a resolvable start becomes current, and indeterminate child starts fail open instead of hiding the parent immediately. That keeps the current atmospheric continuation visible at the Kerbin-exit handoff without leaving the old parent ghost hanging around after the real child-start boundary. Added headless regressions for suppression timing, existing-parent retirement, indeterminate child-start handling, current orbit-continuation ghost creation, and atmospheric-marker handoff.

**Files:** `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek/ParsekTrackingStation.cs`, related tests/docs.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0.

---

## ~~537. Tracking Station never runs the real-vessel spawn handoff that flight/map playback does~~

**Source:** user observed the missing Mun-orbit materialization in Tracking Station; the same package shows the contrast directly. In TRACKSTATION, the host only initializes and updates ghost ProtoVessels (`ParsekTrackingStation initialized`, atmospheric markers, ghost create/remove). Later in FLIGHT/map playback, the same recording `#3` does execute the normal handoff: `Spawn #3 (Kerbal X) ... body=Mun` followed by `SpawnAtPosition: vessel spawned (sit=ORBITING, pid=3724180956, body=Mun, alt=63723m)`.

**Concern:** fixed on `2026-04-22`. `GhostMapPresence` now runs a Tracking Station end-of-recording handoff that reuses the shared spawn eligibility rules, dedups against already-live real vessels, and calls `VesselSpawner` directly for eligible recordings instead of waiting for a later FLIGHT/map scene. The same pass now also suppresses/removes already-materialized terminal-orbit ghosts so stale map entries do not linger after the handoff.

**Files:** `Source/Parsek/ParsekTrackingStation.cs`, `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek/ParsekPlaybackPolicy.cs`, `Source/Parsek/VesselSpawner.cs`, `Source/Parsek.Tests/TrackingStationSpawnTests.cs`.

**Status:** CLOSED 2026-04-22 for v0.9.0. Tracking Station now materializes eligible real vessels directly and clears the stale spawned-ghost edge that was keeping terminal orbit ghosts around.

---

## ~~538. Atmospheric reentry fire still looks too sparse; tuning target is roughly 2x the current particle density~~

**Source:** user observed that the atmospheric drag/heating fire is too thin and should emit about twice as many particles. Current code still hardcodes a single fire-particle layer at `ReentryFireEmissionMin=300`, `ReentryFireEmissionMax=2000`, and `ReentryFireMaxParticles=1500`. The package's reentry run confirms the path is active (`Lazy reentry build fired`, later `rebuilt emission mesh ... and 105 fire shell meshes after decouple`), but does not measure visual density.

**Fix:** kept the primary tuning surface in `DriveReentryLayers()` by doubling the fire-particle emission range from `300-2000` to `600-4000` particles/sec, then raised the build-time `ReentryFireMaxParticles` cap only from `1500` to `2000` so the denser stream does not clip at peak intensity. Added deterministic headless coverage pinning the tuned emission range/cap, plus a live runtime regression that builds a real Unity reentry particle system, drives `UpdateReentryFx()` on an atmospheric body, and waits on elapsed realtime rather than a fixed frame count before asserting the emission rate rises past the old `2000` particles/sec ceiling. The live runtime check still skips on non-atmospheric saves.

**Files:** `Source/Parsek/GhostVisualBuilder.cs`, `Source/Parsek/GhostPlaybackEngine.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek.Tests/GhostVisualBuilderTests.cs`.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0.

---

## ~~539. Two `GhostPlaybackEngineTests` cases are `[Fact(Skip = ...)]` stubs that xUnit should not keep shipping~~

**Source:** `Source/Parsek.Tests/GhostPlaybackEngineTests.cs` had two permanently-skipped cases. `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` already had a dedicated in-game replacement, but `UpdateLoopingPlayback_PendingCycleBoundary_DoesNotEmitRestartEvents` still only had indirect coverage via the pure-logic sibling `ReusePrimaryGhostAcrossCycle_NullGhost_AdvancesCycleWithoutEvents` plus broader loop-flow regressions.

**Resolution (2026-04-22):** CLOSED on branch `bug/539-remove-skipped-xunit-stubs`. Deleted both skipped xUnit placeholders from `GhostPlaybackEngineTests.cs` and also removed the now-dead `SpawnPrimingPositioner` helper that only those stubs used. Coverage decision:

- `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT`: removed outright. Its surviving live-Unity coverage remains the pre-existing `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT_InGame` test, which now seeds its own synthetic playback recording from the active-vessel snapshot instead of depending on pre-existing committed save data, so the shipped regression keeps direct runtime evidence for the priming invariants without the dead xUnit stub.
- `UpdateLoopingPlayback_PendingCycleBoundary_DoesNotEmitRestartEvents`: added a dedicated `GhostPlayback` in-game regression, `PendingLoopCycleBoundary_PendingGhostDoesNotEmitRestartEvents_InGame`, which drives `UpdatePlayback -> UpdateLoopingPlayback` on a pending `LoopEnter` shell with `ghost == null`, asserts that `loopCycleIndex` advances, and proves that no `OnLoopRestarted` / `OnLoopCameraAction` events fire while the missing-snapshot debris ghost still cannot materialize. The existing headless `ReusePrimaryGhostAcrossCycle_NullGhost_AdvancesCycleWithoutEvents` test stays in place as the pure-helper counterpart for the null-ghost cycle-advance invariant.

**Files:** `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`.

**Validation:** `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --no-restore --filter "FullyQualifiedName~GhostPlaybackEngineTests"` passed (`109` passed, `0` failed, `0` skipped) and the full restore-backed `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` compile+run reached the xUnit runner, but still hit one unrelated existing environment-state failure in `SyntheticRecordingTests.InjectAllRecordings` (`Expected exactly 283 .prec files ... found 540`, orphan sidecars from a prior inject run). The new runtime regression compiles as part of the referenced `Parsek` project but still needs live KSP execution via `Ctrl+Shift+T` for full runtime evidence.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0.

---

## ~~540. Three xUnit style warnings on `dotnet test` should be cleaned up~~

**Source:** `dotnet test` on current `main` emits three xUnit analyzer warnings: `InGameTestRunnerTests.cs:259` (`xUnit1013` — `FormatCoroutineState_ReportsActiveAndIdleSlots` is public but missing `[Fact]`, so it silently does not run) and `KerbalsWindowUITests.cs:700-701` (two `xUnit2009` — `Assert.True(text.StartsWith(prefix, StringComparison.Ordinal))` should be `Assert.StartsWith(prefix, text, StringComparison.Ordinal)` for better failure messages).

**Concern:** the `xUnit1013` case is a real correctness gap — the orphaned method sits between two `[Fact]`-attributed siblings and looks like a test that lost its attribute during an edit, so a code path currently believed to be tested is not. The two `xUnit2009` cases are presentation-only, but leaving analyzer warnings in the baseline trains reviewers to ignore real signals.

**Files:** `Source/Parsek.Tests/InGameTestRunnerTests.cs` (add `[Fact]` to `FormatCoroutineState_ReportsActiveAndIdleSlots` or reduce visibility), `Source/Parsek.Tests/KerbalsWindowUITests.cs` (swap both `Assert.True(x.StartsWith(...))` for `Assert.StartsWith(...)`).

**Fix / Resolution (2026-04-22):** CLOSED for v0.9.0. `InGameTestRunnerTests.FormatCoroutineState_ReportsActiveAndIdleSlots` is now explicitly marked `[Fact]`, so the assertion runs instead of sitting as public test-shaped dead code, and the Kerbals subitem-indent regressions now use xUnit `Assert.StartsWith(...)` with the original `StringComparison.Ordinal` semantics preserved across the touched prefix checks instead of the old `Assert.True(text.StartsWith(..., StringComparison.Ordinal))` form. A forced `dotnet build Source/Parsek.Tests/Parsek.Tests.csproj -t:Rebuild --no-restore` rerun now reports `0 Warning(s)`.

**Status:** CLOSED 2026-04-22. Cheap cleanup completed for v0.9.0; the test project no longer carries these baseline analyzer warnings.

---

## ~~541. Main Parsek button labels should be shorter and stop showing the dynamic Kerbals count~~

**Source:** user request from 2026-04-22 after a main-page UI wording pass.

**Concern:** the main button column still renders `Kerbals ({total})` and `Career State`. For top-level navigation the count suffix is noise, and `Career State` is longer/more formal than the rest of the button labels. Requested wording: `Kerbals` with no trailing count, and `Career` instead of `Career State`. Any detailed counts can stay inside the destination windows rather than on the launch surface.

**Fix:** `ParsekUI` now renders the launch-surface buttons as `Kerbals` and `Career`, removes the dynamic Kerbals aggregate count entirely from the main column, and leaves the detailed roster counts inside the Kerbals/Career destination windows. `ParsekUITests` now pin the short-label contract so a future refactor cannot quietly reintroduce the count suffix or the longer `Career State` button label.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~542. Ghost watch camera cutoff should stop being user-editable and become a fixed 300 km config default~~

**Source:** user request from 2026-04-22 plus current code read of `SettingsWindowUI` / `ParsekConfig`.

**Fix:** removed the `Ghosts -> Camera cutoff` field from the settings window, dropped `ghostCameraCutoffKm` from `ParsekSettings` and `ParsekSettingsPersistence`, and made watch eligibility / watch exit / watched-full-fidelity callers read the fixed `DistanceThresholds.GhostFlight.DefaultWatchCameraCutoffKm = 300f` helper directly. Old persisted cutoff values are now ignored, so legacy overrides stop leaking into current watch behavior.

**Files:** `Source/Parsek/UI/SettingsWindowUI.cs`, `Source/Parsek/ParsekConfig.cs`, `Source/Parsek/ParsekSettings.cs`, `Source/Parsek/ParsekSettingsPersistence.cs`, `Source/Parsek/WatchModeController.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek.Tests/DistanceThresholdsTests.cs`, `Source/Parsek.Tests/ParsekSettingsPersistenceTests.cs`.

**Status:** DONE. Fixed in `0.8.3`.

---

## ~~543. Auto-loop recordings need a global launch queue instead of per-recording independent cadence~~

**Source:** user request from 2026-04-22. Current code in `GhostPlaybackLogic.ResolveLoopInterval(...)` gives every `LoopTimeUnit.Auto` recording the same global period, but each recording still schedules its own cycles independently from its own start UT.

**Concern:** with multiple recordings on `Auto`, the current behavior can still produce simultaneous or visually clumped relaunches because "Auto" is only a shared interval value, not a shared queue. Requested behavior: treat all looped recordings whose unit is `Auto` as one launch queue ordered by launch sequence, with the gap between launches equal to the global Auto setting. Example: three Auto recordings with Auto = 30s should launch A, then B after 30s, then C after 30s, then continue cycling through that queue instead of each recording relaunching on its own independent cadence.

**Fix:** added a shared Auto-loop schedule resolver in `GhostPlaybackLogic`, cached that queue in `GhostPlaybackEngine`, and propagated the same schedule/playback split into flight watch-mode and KSC loop reconstruction. Auto recordings are now ordered once by effective loop launch UT, the shared Auto interval becomes the gap between successive queue launches, and each recording's own relaunch cadence becomes `queueLength * autoInterval`. Added direct regression coverage for the queue resolver, schedule-aware phase math, KSC loop timing, and engine cache usage.

**Files:** `Source/Parsek/GhostPlaybackLogic.cs`, `Source/Parsek/GhostPlaybackEngine.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/ParsekKSC.cs`, `Source/Parsek/WatchModeController.cs`, `Source/Parsek.Tests/AutoLoopTests.cs`, `Source/Parsek.Tests/LoopPhaseTests.cs`, `Source/Parsek.Tests/KscGhostPlaybackTests.cs`, `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~544. Rewind-to-launch should give 15 seconds of lead time instead of 10~~

**Source:** user request from 2026-04-22 plus current code read in `RecordingStore.InitiateRewind`.

**Concern:** the rewind preprocessing path still winds UT back by only `10.0` seconds (`rewindLeadTime`) before restoring the stripped launch save. That is not enough setup time on many launches. Requested change: increase the lead time to 15 seconds and update the related tests/logging assumptions that currently pin the 10-second value.

**Fix:** `RecordingStore` now routes rewind-to-launch through a shared `RewindToLaunchLeadTimeSeconds = 15.0` constant, so the stripped launch save rewinds far enough for pad setup before the ghost appears. The end-to-end rewind logging / adjusted-UT tests now read that same constant instead of pinning the old 10-second launch assumption in literals.

**Files:** `Source/Parsek/RecordingStore.cs`, `Source/Parsek.Tests/RewindLoggingTests.cs`.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~545. Timeline should squash adjacent near-duplicate milestone rows into one richer entry~~

**Source:** user request from 2026-04-22 plus current code read in `TimelineBuilder` / `TimelineEntryDisplay`.

**Concern:** the Timeline currently formats each milestone action independently, so near-neighbor rows can read like duplicates when they describe the same milestone with slightly different reward payloads (for example one row with funds only and a sibling row with funds + rep). `TimelineBuilder` already filters exact ledger-vs-legacy duplicates, but it does not run a second pass that compares `Prev - Current - Next` for "same milestone, same moment, richer combined message" cases. Requested behavior: add a compaction/squash pass that detects adjacent similar milestone rows and merges them into a single entry with the union of the useful reward details instead of showing multiple nearly-identical lines.

**Fix:** `TimelineBuilder` now runs a post-sort compaction pass over same-moment game-action milestone rows that share both milestone id and a UT within 0.1 seconds. Compatible rows collapse into one entry even if another same-timestamp row sits between them, missing reward legs are filled from the richer sibling, and the merged row keeps the effective/T1 presentation if any source row was effective. `TimelineEntryDisplay` milestone text now uses a shared formatter that includes science rewards as well as funds/rep, and the focused timeline-builder tests pin the compacted, interleaved, near-UT, non-compacted, and conflicting-value cases.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~546. Post-switch follow-up recording still lacks general first-modification triggers once Parsek is idle or tree context is lost~~

**Source:** design/code read on 2026-04-22 after reviewing `docs/parsek-flight-recorder-design.md`, `docs/dev/done/recording-chaining.md`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/FlightRecorder.cs`, and the current auto-record/runtime tests. The design says focus-vessel recording should promote/demote across vessel switches and that physical state changes are what matter; current settings/UI still expose only `Auto-record on launch` and `Auto-record on EVA`.

**Concern:** outside the narrow launch / pad-EVA / live-tree-promotion paths, there was no general "switch to a real vessel, then make the first meaningful state change" arming path. `OnVesselSituationChange` only auto-started from `PRELAUNCH` or settled `LANDED`, and `PromoteRecordingFromBackground(...)` only worked while an active tree already existed and the switched-to PID was still in `BackgroundMap`. That left several follow-up cases uncaptured unless the user manually started recording: orbital/suborbital engine or RCS burns that stay in the same situation, rover/base repositioning that remains `LANDED`, and switched-to vessel part/crew/resource mutations that do not pass through the current launch/EVA gates. `#534` is the spawned-chain-tip restore variant of this broader gap.

**Fix:** idle switches to real non-ghost vessels now arm a dedicated post-switch watcher instead of starting on switch alone. The watcher captures its baseline on the first stable physics frame after the switch, reuses the existing landed settle threshold before comparing landed vessels, and starts exactly once on the first meaningful physical change:

- landed motion / orbital state change
- engine ignition or sustained RCS activity
- crew/resource/inventory delta
- non-cosmetic part-state change (gear and similar authored physical state)

Observation-only / cosmetic-only changes stay ignored. Checks are suppressed while restore is running, while split/dock/boarding transitions are pending, during regular or physics warp, for packed/on-rails vessels, for ghost-map vessels, and when the active vessel no longer matches the armed PID. Manifest-based comparisons are throttled to a short interval while armed, and vessel-modification events invalidate the cached engine / RCS module lists so post-switch checks do not keep snapshotting the full vessel every physics frame. A new setting, `Auto-record on first modification after switch`, ships enabled by default alongside the other auto-record toggles. The outsider/start-fresh path and tracked-background-member promote-on-trigger path are both implemented here. The restore-and-promote tracked seam remains intentionally gated behind open `#534` on this branch.

**Files:** `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/Patches/PhysicsFramePatch.cs`, `Source/Parsek/ParsekSettings.cs`, `Source/Parsek/UI/SettingsWindowUI.cs`, `Source/Parsek.Tests/PostSwitchAutoRecordTests.cs`, `Source/Parsek.Tests/VesselSwitchTreeTests.cs`, `Source/Parsek.Tests/MissedVesselSwitchRecoveryTests.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`, `docs/dev/manual-testing/test-auto-record.md`, `CHANGELOG.md`. Related cluster: open `#534` is still the narrow spawned-chain-tip restore seam; `#547` / `#548` / `#549` remain separate follow-ups.

**Status:** CLOSED 2026-04-22. Fixed for v0.9.0 with the post-switch arming / trigger policy, headless helper coverage, and isolated runtime canaries. Remaining gate: `#534` restore-and-promote for the spawned-chain-tip return seam stays open and separate.

---

## ~~550. KSC merge spawn can duplicate the surviving source vessel at the same landed endpoint, then both unpack and collide~~

**Source:** user repro and fresh package `logs/2026-04-23_1909_recording-resume-spawn-explosion`. `KSP.log` shows `Butterfly Rover` and `Crater Crawler` each being merged in SPACECENTER, then `KSCSpawn` spawning a real vessel for the committed recording while the source vessel still existed in the save. The save snapshot confirms the duplicate: `quicksave.sfs` contains source `persistentId = 22060629` and spawned copy `persistentId = 3693161297` at the same `lat/lon`, with only the altitude clamp separating them. Later FLIGHT loads unpacked two same-name vessels and produced collision/debris/crash-through-terrain logs.

**Concern:** the KSC spawn path only checked intrinsic recording eligibility and the recording's prior `spawnedPid`. It did not check whether the original source vessel was still present after a normal Space Center scene exit. That created a second real vessel at the terminal pose. It also meant the resume mechanism depended on the duplicate's PID instead of the vehicle the user had actually just recorded.

**Fix:** real-vessel materialization now goes through a shared `VesselSpawner` source-vessel adoption guard. Before KSC spawn, Flight tree-leaf spawn, Flight/Tracking Station spawn handoffs, or chain-tip spawns create a vessel from a recording snapshot, Parsek checks loaded vessels and `HighLogic.CurrentGame.flightState.protoVessels` for the recording's original `VesselPersistentId`. If the source vessel still exists, Parsek adopts that PID by setting `VesselSpawned` and `SpawnedVesselPersistentId` instead of spawning a copy. The committed-tree restore path already keys off `SpawnedVesselPersistentId`, so returning to that craft can resume the committed tree from the recorded endpoint. The older #226 replay/revert path remains an explicit duplicate-spawn opt-in instead of an accidental bypass.

**Files:** `Source/Parsek/VesselSpawner.cs`, `Source/Parsek/ParsekKSC.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/VesselGhoster.cs`, `Source/Parsek.Tests/KscSpawnTests.cs`, `Source/Parsek.Tests/VesselSpawnerExtractedTests.cs`, `Source/Parsek.Tests/VesselGhosterTests.cs`, `Source/Parsek.Tests/TimeJumpManagerTests.cs`, `Source/Parsek.Tests/VesselSwitchTreeTests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.9.0 with source-vessel adoption and focused headless restore coverage.

---

## 547. Recording optimizer should surface cross-body exo segments more clearly than the current first-body label

**Source:** `docs/dev/recording-optimizer-review.md` (2026-04-07), especially the traced Kerbin-launch-to-Mun-landing scenario.

**Concern:** the optimizer only splits on environment-class changes, not body changes, so a long exo segment can legitimately span Kerbin orbit, transfer coast, and Mun orbit while still inheriting `SegmentBodyName` from its first trajectory point. The current result is structurally correct and loopable, but the player-facing label can still read like a lie (`Kerbin` even though the recording includes Mun orbit time). We need a deliberate decision here instead of leaving it as an accidental quirk: either keep the single exo segment and surface a multi-body label (`Kerbin -> Mun`), or introduce an optional body-change split criterion if that proves clearer in practice.

**Files:** `Source/Parsek/RecordingOptimizer.cs`, `Source/Parsek/RecordingStore.cs`, timeline/recordings UI that renders `SegmentBodyName`, `docs/dev/recording-optimizer-review.md`.

**Status:** TODO. Likely UX/research follow-up, not a v0.8.3 ship blocker.

---

## 548. Static background continuations and all-boring surface leaf segments should not read like empty ghost recordings

**Source:** `docs/dev/recording-optimizer-review.md` (2026-04-07), issues 1 and 2.

**Concern:** two related outputs are still structurally correct but awkward in the player-facing recordings list:
- stationary landed background continuations can end up as `SurfacePosition`/time-range placeholders with no real ghost trail
- all-boring surface leaf segments can survive optimizer trim because they still carry the final `VesselSnapshot`/spawn responsibility

Both cases are valid data, but they clutter the UI and read like broken/empty ghosts. We should either collapse them visually, mark them explicitly as static/stationary, or trim them to a minimal terminal window while preserving their structural role.

**Files:** `Source/Parsek/BackgroundRecorder.cs`, `Source/Parsek/RecordingOptimizer.cs`, recordings/timeline UI that lists committed segments, `docs/dev/recording-optimizer-review.md`.

**Status:** TODO. UX cleanup / follow-up analysis.

---

## 549. Recording optimizer needs end-to-end branch-point coverage when tree recordings are split post-commit

**Source:** `docs/dev/recording-optimizer-review.md` (2026-04-07), issue 5.

**Concern:** the optimizer has unit coverage for split logic, but we still do not have a full tree-with-branch-points regression that proves post-commit environment splits preserve the intended branch linkage and chain navigation shape. The review did not find a live bug here, but this is exactly the seam most likely to regress silently when optimizer logic or branch-point rewrites change.

**Files:** `Source/Parsek.Tests/RecordingOptimizer*`, `Source/Parsek.Tests/RecordingStore*`, any integration-style optimizer/tree fixture that exercises `RunOptimizationPass` on a multi-stage tree with branch points.

**Status:** TODO. Medium-priority coverage gap.

---

## ~~561. Tracking Station ghost selection can leave a stale stock vessel as the private fly target~~

**Source:** `logs/2026-04-23_1815_logs-package/KSP.log` and `persistent.sfs`. The session created `Learstar A1` correctly as a real `Plane` in Kerbol orbit (`SpawnAtPosition ... pid=3517645340, body=Sun`), but the later Tracking Station fly path loaded stock asteroid `Ast. QME-914` (`persistentId=2902671035`, `type=SpaceObject`, `PotatoRoid`) instead.

**Concern:** `SpaceTracking.BtnOnClick_FlySelectedVessel()` flies KSP's private `selectedVessel`. Parsek blocked ghost `SetVessel` calls and disabled the visible buttons, but did not clear that private field, so a previous stock asteroid/comet selection could survive behind the ghost focus flow and become the eventual fly target. The same log package also showed repeated terminal-orbit ghost creation failures for unseedable records and misleading segment-ghost creation lines that printed the recording terminal SMA rather than the actual segment orbit SMA.

**Fix:** ghost Fly/Delete/Recover/SetVessel blocks now clear the Tracking Station `selectedVessel` field before returning, and the blocked `SetVessel` log records whether a previous selection was cleared. Tracking Station terminal-orbit ghosts now pass through the same endpoint-aligned orbit-seed gate before creation, while terminal-orbit-only recordings can seed from their own terminal orbit when there is no conflicting endpoint evidence. Segment ghost creation logs now print the actual ProtoVessel orbit SMA.

**Files:** `Source/Parsek/Patches/GhostTrackingStationPatch.cs`, `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek/RecordingEndpointResolver.cs`, `Source/Parsek.Tests/GhostTrackingStationPatchTests.cs`, `Source/Parsek.Tests/GhostMapPresenceTests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3.

---

## ~~551. Tracking Station should share Map View's ghost lifecycle policy instead of rebuilding an independent subset~~

**Source:** Tracking Station / Map Mode UI audit from the `#561` investigation, plus `logs/2026-04-23_1815_logs-package`.

**Concern:** Flight Map View has the richer ghost lifecycle path: pending-vessel policy, state-vector and orbit-segment source selection, chain-tip dedupe/update behavior, and handoff checks flow through `ParsekPlaybackPolicy` / `GhostMapPresence`. Tracking Station still has its own periodic rebuild loop in `ParsekTrackingStation`, which re-evaluates committed recordings every couple of seconds and has historically lagged Map View on source selection, handoff suppression, and duplicate cleanup.

**Action plan:**

1. ~~Extract a shared map-presence lifecycle that both Flight Map View and Tracking Station call for "which map/TS objects should exist now".~~
2. ~~Make Tracking Station consume the same source-decision result as Map View: visible segment, terminal orbit, state-vector fallback, endpoint conflict reason, and materialized-real-vessel suppression.~~
3. ~~Preserve scene-specific rendering/adapters, but keep chain dedupe, materialized-PID tracking, and update/remove decisions shared.~~
4. ~~Add regressions for a recording that is correct in Map View and then enters Tracking Station with the same visible object set and suppression reasons.~~

**Fix:** Added `GhostMapPresence.ResolveMapPresenceGhostSource` as the shared source-decision path used by both `ParsekPlaybackPolicy` and the Tracking Station lifecycle. Tracking Station now follows the same visible-segment and state-vector policy as Map View, keeps terminal-orbit fallback behind endpoint-aligned seed checks, skips endpoint conflicts with the same reason, and removes/suppresses ghosts once a real vessel materializes.

**Files:** `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek/ParsekPlaybackPolicy.cs`, `Source/Parsek.Tests/GhostMapPresenceTests.cs`, `Source/Parsek.Tests/TrackingStationSpawnTests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3 with headless parity coverage in `GhostMapPresenceTests` and Tracking Station materialization coverage in `TrackingStationSpawnTests`.

---

## ~~552. Vessel recovery funds can log a false missing-pair warning when stock delivers the funds event after recovery~~

**Source:** latest collected package `logs/2026-04-23_1829_logs-package/`. `KSP.log` shows `OnVesselRecoveryFunds` warning at `18:23:16.943` because no paired `FundsChanged(VesselRecovery)` was found yet, then the actual `FundsChanged` recovery event arrived at `18:23:16.961`, within the intended pairing window.

**Concern:** the old path assumed KSP delivered `FundsChanged(VesselRecovery)` before `onVesselRecovered`. The observed ordering was reversed by about 18 ms, so Parsek skipped adding the recovery earning and logged a false warning even though the data arrived moments later.

**Fix:** `OnVesselRecoveryFunds(...)` now defers unmatched recovery requests and `GameStateRecorder.OnFundsChanged(...)` calls `OnRecoveryFundsEventRecorded(...)` for recovery reasons so the delayed event can complete the pairing. Pending callbacks are preserved until they pair with distinct `FundsChanged(VesselRecovery)` event fingerprints, including same-named recoveries inside the UT epsilon, and are cleared on load/test resets. Pairing prefers requests whose vessel name matches the funds event and warns when multiple candidates share the same UT after name matching, then falls back to nearest UT. Pending requests that never receive a paired funds event are evicted on scene switches, save loads, and rewind boundaries with a WARN listing the unclaimed entries.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek/GameStateRecorder.cs`, `Source/Parsek/ParsekScenario.cs`, `Source/Parsek.Tests/GameStateRecorderLedgerTests.cs`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3 with focused callback-before-event coverage, staleness eviction on lifecycle boundaries, and vessel-name-preferred pairing with WARN on ambiguous ties.

---

## ~~563. Tracking Station needs an in-scene Parsek control surface comparable to Map View~~

**Source:** Tracking Station / Map Mode UI audit from the `#561` investigation.

**Concern:** Map View and Flight/KSC scenes expose the Parsek button/window surface, while Tracking Station mostly exposes only stock map objects plus defensive ghost blocking. A player can inspect ghosts there but cannot conveniently toggle Parsek ghost visibility, open Recordings/Settings, see Parsek status, or understand why a TS object is ghost-only, materialized, suppressed, or blocked.

**Fix:** Tracking Station now installs a Parsek toolbar button and draws a compact IMGUI control surface using the same opaque window styling as the existing scene UI. The panel exposes the sticky `Show ghosts in Tracking Station` toggle, shared Recordings and Settings windows, and status rows for committed recordings, current map ghosts, suppressed entries, and materialized vessels. The ghost toggle updates live settings when available, persists through `ParsekSettingsPersistence`, removes ghost ProtoVessels immediately when disabled, and forces the next lifecycle tick when changed.

**Files:** `Source/Parsek/ParsekTrackingStation.cs`, `Source/Parsek/ParsekUI.cs`, `Source/Parsek/UI/RecordingsTableUI.cs`, `Source/Parsek/UI/SettingsWindowUI.cs`, `Source/Parsek/UI/TestRunnerUI.cs`, `Source/Parsek.Tests/TrackingStationControlSurfaceUITests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3 by `#563`. Runtime collection of Tracking Station scene smoke evidence remains tracked separately by `#554`.

---

## ~~553. Untagged pre-recording FLIGHT contract events can miss the ledger because the direct path was KSC-only~~

**Source:** latest collected package `logs/2026-04-23_1829_logs-package/`. The launch-site contract path emitted untagged `ContractAccepted` / `ContractCompleted` events with `tag=''` and no recording owner, while the ledger needed the accepted contract to preserve the active contract and advance.

**Concern:** direct ledger forwarding only allowed non-FLIGHT scenes. That is correct for tagged FLIGHT teardown events, but untagged pre-recording FLIGHT contract events have no later commit-time `ConvertEvents` owner. If they are not written directly, the ledger can miss contract state or rewards. The same ownership reasoning applies to tech, part-purchase, crew-hire, strategy, facility, and science-subject events that arrive untagged before any recording exists.

**Fix:** contract lifecycle forwarding now keys on the `recordingId` stamped by `Emit(ref evt)` and whether a live recorder can still own an empty-tag event. Non-empty tags suppress direct ledger writes so teardown/discard fate remains intact; empty tags forward directly only when no live recorder exists, covering true pre-recording FLIGHT events without turning tag-resolution drift into null-owner ledger actions. The same `ShouldForwardDirectLedgerEvent` predicate is threaded through `TechResearched`, `PartPurchased`, `CrewHired`, `MilestoneAchieved` (in-flight and standalone paths), `StrategyActivated`, `StrategyDeactivated`, and `FacilityUpgraded` handlers so every direct-ledger path shares one gate.

**Files:** `Source/Parsek/GameStateRecorder.cs`, `Source/Parsek.Tests/DiscardFateTests.cs`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3 with predicate coverage for tagged teardown suppression, untagged KSC forwarding, untagged pre-recording FLIGHT forwarding, and empty-tag live-recorder drift suppression across all direct-ledger handlers.

---

## ~~557. Delayed science/reputation seeding can turn future balances into UT0 after rewind~~

**Source:** latest collected package `logs/2026-04-23_1829_logs-package/`. `KSP.log` shows initial deferred seeding stopped as soon as Funding reported 25000 while Science and Reputation still read zero. Later, after the first committed flight and before the rewind, `ledger.pgld` gained `ScienceInitial = 11.04` and `ReputationInitial = 0.999999464` at UT0 even though the earliest baseline had both resources at zero. The rewind recalculation at adjusted UT 49.4 then included those UT0 seeds and patched science/reputation from future state.

**Concern:** a zero science or reputation balance can be legitimate at career start. Because `LedgerOrchestrator` skipped zero seeds and `Ledger.SeedInitialScience/Reputation` could later upgrade an existing zero-like seed, a future live balance could be mistaken for initial state. That breaks rewind/cutoff budget reconstruction and can make future science or reputation available in the past.

**Fix:** `LedgerOrchestrator` now seeds per-resource initial balances through a baseline-aware path. Existing seed actions mark that resource seeded, captured baselines can create authoritative zero seeds, and the fallback path refuses to treat non-zero live science/reputation as initial when timeline actions for that same resource already exist without a baseline.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek.Tests/RewindUtCutoffTests.cs`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3 with focused headless rewind-cutoff coverage.

---

## ~~559. Research Building stays unlocked to future tech after rewind~~

**Source:** user report from the 2026-04-23 log review, confirmed against `logs/2026-04-23_1829_logs-package/parsek/GameState/ledger.pgld` and saved baselines. The player rewound to adjusted UT 49.4, before the UT 124.43 `ScienceSpending` unlocks for `basicRocketry` and `engineering101`, but the Research Building still showed the future button state.

**Concern:** `KspStatePatcher.PatchAll(...)` restored resources, facilities, milestones, contracts, and crew-derived state but did not restore KSP's R&D tech availability. Once KSP had loaded a future save with later researched nodes, rewinding the ledger cutoff could fix science balance without locking the future tech nodes again.

**Fix:** patching now builds an authoritative target tech set from the latest baseline at or before the cutoff plus affordable `ScienceSpending` actions up to that same cutoff, and `PatchTechTree(...)` (gated to the rewind path where `utCutoff.HasValue`) updates `ResearchAndDevelopment` proto-tech state, mirrors availability onto the static tech-tree proto nodes, removes future nodes from the proto dictionary, unconditionally rehydrates bypass-entry-purchase parts from loaded part metadata with dedup so stale or partial `partsPurchased` lists self-heal, and refreshes the tech-tree UI. Non-rewind `RecalculateAndPatch` calls no longer touch the live tech tree, so post-baseline unlocks are preserved. Reflection lookups now emit a one-shot `ParsekLog.Warn` identifying the failed field, the "missing targets" skip log lists the first ~10 missing tech ids, and the `PatchTechTree: available=...` info log now includes the cutoff UT and selected baseline UT.

**Files:** `Source/Parsek/GameActions/KspStatePatcher.cs`, `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek.Tests/KspStatePatcherTests.cs`, `CHANGELOG.md`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3 with headless target-tech selection coverage plus `PatchTechTree` log-assertion coverage (no-target skip, missing R&D singleton, one-shot reflection-failure warn); live Research Building UI evidence still requires a manual KSP rewind run.

---

## ~~554. Tracking Station runtime coverage is missing from the collected in-game test package~~

**Source:** Tracking Station / Map Mode UI audit from the `#561` investigation. The collected package did not include expected TS scene rows such as `ParsekTrackingStationExists` or `ShowGhostsInTrackingStation_FlipRemovesAndRecreates`.

**Concern:** Several TS regressions are scene-integration problems that headless xUnit can only approximate. The latest package proved the Learstar real-vessel spawn and the stale asteroid switch, but it did not prove the TS UI lifecycle, TS ghost toggle recreation, or post-materialization Fly path on a patched build.

**Action plan:**

1. Restore or add isolated in-game TS canaries for scene entry, show/hide/recreate, ghost object count, and no exception spam.
2. Add a materialization canary for an orbital terminal recording: enter TS, let the real vessel spawn, verify the ghost is removed/suppressed, select/fly the real vessel, and assert the loaded vessel PID/type/name match the materialized vessel, not a stale asteroid/comet.
3. Ensure `collect-logs.py` preserves these TS rows in `parsek-test-results.txt` and the release bundle validation can flag their absence when TS work is under test.
4. Keep manual-only variants for any stock scene transitions that remain too destructive for the regular isolated batch.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek/InGameTests/*TrackingStation*`, `scripts/collect-logs.py`, `scripts/validate-release-bundle.py`, release validation docs.

**Fix:** Added deterministic `TrackingStation` in-game canaries in `RuntimeTests.cs`: scene entry verifies the Parsek/stock TS hosts, the synthetic orbital toggle canary forces show/hide/recreate without depending on save-local recordings, and the object-count canary validates the synthetic TS ghost object stays unique/resolvable without captured TS error spam. Added a manual-only materialized orbital Fly canary for the Learstar stale-selection class: it seeds a stale alternate selection, proves focusing a Parsek ghost clears the stale private stock selection, then focuses/flys a materialized orbital vessel and asserts the loaded FLIGHT vessel PID is the materialized one. `validate-release-bundle.py` now has a `release-tracking-station` profile requiring the batch-safe TS rows and documenting the optional Fly row when it was not captured.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3.

---

## ~~555. Tracking Station orbit-source diagnostics and fallback noise need a cleanup pass after the #561 fix~~

**Source:** `logs/2026-04-23_1815_logs-package/KSP.log` and the Tracking Station / Map Mode UI audit from the `#561` investigation.

**Concern:** `#561` fixed the worst repeated terminal-orbit ghost attempts and corrected the segment-ghost SMA log, but the TS logs still need a cleaner source story. When a ghost is skipped, rebuilt, seeded from a visible segment, seeded from terminal orbit, or suppressed because a real vessel already exists, the log should make the source and reason clear without generating hundreds of repeated fallback lines.

**Action plan:**

1. Carry orbit-source metadata through the TS map object build path.
2. Rate-limit or aggregate recurring skip reasons by recording/source/reason, especially around terminal-orbit and "map-visible orbit window unavailable" fallbacks.
3. Log terminal vs segment vs state-vector source decisions consistently with Map View.
4. Add log-assertion tests covering one successful segment ghost, one terminal-orbit ghost, one endpoint-conflict skip, and one already-materialized suppression.

**Files:** `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek/ParsekTrackingStation.cs`, `Source/Parsek/RecordingEndpointResolver.cs`, `Source/Parsek.Tests/GhostMapPresenceTests.cs`.

**Resolution:** Added first-occurrence plus aggregate Tracking Station orbit-source diagnostics for visible segment, terminal orbit, endpoint conflict, already-materialized suppression, and repeated no-orbit skip buckets. Endpoint-aligned seed resolution now carries diagnostic metadata, ProtoVessel creation logs include orbit-source detail, and visible-window fallback logs share rate-limited keys.

**Validation:** `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter GhostMapPresenceTests`.

**Status:** CLOSED in `#555`.

---

## ~~556. Tracking Station `buildVesselsList` finalizer should not swallow unrelated stock exceptions~~

**Source:** Tracking Station / Map Mode UI audit from the `#561` investigation.

**Concern:** `GhostTrackingBuildVesselsListPatch.Finalizer` protected Tracking Station from ghost-caused stock NREs, but the broad finalizer shape also hid unrelated `SpaceTracking.buildVesselsList` failures. That made TS debugging harder and could mask regressions outside Parsek ghost handling.

**Fix:** the finalizer now suppresses only a `NullReferenceException` when the live `FlightGlobals.Vessels` scan shows the first missing `orbitRenderer` belongs to a Parsek ghost ProtoVessel, no earlier stock-null candidate (null vessel or null `DiscoveryInfo`) would have failed first, and any available stock stack-frame IL offset still points at the `orbitRenderer` load (`0x00b4`) or the `onVesselIconClicked` access (`0x00b9`) in `SpaceTracking.buildVesselsList`. Unrelated exception types, NREs without ghost missing-renderer evidence, different stock offsets, ambiguous stock missing-renderer contexts, scan failures, and prior stock-null candidates emit a `[GhostMap]` WARN with vessel-context counts and return the original exception to Harmony so stock failures remain visible.

**Files:** `Source/Parsek/Patches/GhostTrackingStationPatch.cs`, `Source/Parsek.Tests/GhostTrackingStationPatchTests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3.

---

## ~~560. Default solution build returned exit code 1 even after a clean MSBuild success summary~~

**Source:** `dotnet build Source\Parsek.sln` on SDK `6.0.428` in the `bug/547-latest-log-orbit-anomalies` worktree.

**Concern:** the solution built both `Parsek` and `Parsek.Tests` as default top-level solution projects. On this SDK, the parallel solution-level project dispatch could report `Build succeeded` with `0 Warning(s)` / `0 Error(s)` while still returning process exit code `1`. Direct project builds and `dotnet test Source\Parsek.Tests\Parsek.Tests.csproj` were clean, so this was solution orchestration noise rather than a compiler failure.

**Fix:** the default solution build now builds the deployable `Parsek` plugin project only. `Parsek.Tests` remains listed in the solution and keeps its active Debug/Release configuration, but its `Build.0` entries are removed so tests are built through the explicit test command instead of the default solution build. This keeps `dotnet build Source\Parsek.sln` deterministic while preserving `dotnet test Source\Parsek.Tests\Parsek.Tests.csproj` as the full validation path.

**Files:** `Source/Parsek.sln`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3.

---

## ~~562. Tracking Station ghost-selection clearing leaves the previous vessel's orbit focus and patched conics latched~~

**Source:** Follow-up to the `#561` Tracking Station ghost-selection investigation. `#561` nulled `SpaceTracking.selectedVessel` before blocking Fly/Delete/Recover/SetVessel, but it did not clear the previously selected vessel's `orbitRenderer.isFocused`, `orbitRenderer.drawIcons`, or patched-conics state, so the earlier real-vessel focus ring and conics lines stayed visible after the user clicked a Parsek ghost.

**Concern:** stock `SpaceTracking.SetVessel(...)` deselects the previous vessel by writing `orbitRenderer.isFocused = false`, `orbitRenderer.drawIcons = DrawIcons.OBJ`, and calling `Vessel.DetachPatchedConicsSolver()`. Because the ghost block runs instead of stock `SetVessel`, those deselection side-effects never fired. Calling stock `SetVessel(null, keepFocus:false)` from the block would have re-entered the patched method and could re-trigger Tracking Station tab switches in Mission / Mission Builder modes.

**Fix:** `GhostTrackingStationSelection.TryClearSelectedVessel(...)` now mirrors stock's previous-vessel deselection block (`orbitRenderer.isFocused = false`, `orbitRenderer.drawIcons = DrawIcons.OBJ`, `DetachPatchedConicsSolver()`) directly on the previous selection before nulling the private field, so Fly/Delete/Recover/SetVessel blocks clear the latched focus without re-entering `SetVessel`. Cleanup exceptions are routed back through the existing `error` out-parameter so the caller still logs a targeted `[GhostMap]` WARN instead of corrupting the Tracking Station state.

**Files:** `Source/Parsek/Patches/GhostTrackingStationPatch.cs`, `Source/Parsek.Tests/GhostTrackingStationPatchTests.cs`.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3.

---

## ~~564. Tracking Station ghost objects need safe first-class interactions, not only Fly/Delete/Recover blocking~~

**Source:** Tracking Station / Map Mode UI audit from the `#561` investigation.

**Concern:** Ghost ProtoVessels in the Tracking Station only had the stock Fly/Delete/Recover buttons, all of which Parsek blocks for ghosts. There was no safe positive affordance to focus the camera on the ghost, set it as a target, inspect its owning recording, or materialize the recorded vessel when it was eligible to spawn.

**Action plan:**

1. Add a Parsek-owned selected-ghost action surface in the Tracking Station that exposes Focus, Target, Recording details, and Materialize.
2. Key the selection by stable recording ID so raw index churn in `CommittedRecordings` does not move the panel off the ghost the player clicked.
3. Keep stock Fly/Delete/Recover blocked for ghost-only objects and continue clearing private `SpaceTracking.selectedVessel` on every blocked path.
4. Add tests for action-state decisions and for stale-selection clearing when a player alternates between stock asteroids/comets and Parsek ghosts.

**Files:** `Source/Parsek/Patches/GhostTrackingStationPatch.cs`, `Source/Parsek/ParsekTrackingStation.cs`, `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek.Tests/GhostTrackingStationPatchTests.cs`, `Source/Parsek.Tests/TrackingStationSpawnTests.cs`.

**Fix:** Added a Parsek-owned selected-ghost action surface in Tracking Station. Ghost `SetVessel`/Fly/Delete/Recover blocks now also record the Parsek ghost selection by stable recording ID, clear stock `selectedVessel`, and leave stock Fly/Delete/Recover disabled. The selected ghost panel refreshes action eligibility every GUI frame and exposes safe Focus, Target, owning Recording details, and a selected-recording-only Materialize action when the existing Tracking Station spawn eligibility says that recording is ready; when that ghost resolves, the nav target and map focus hand off to the materialized real vessel. Chain ghosts without a direct committed recording row show disabled recording/materialize states instead of falling through stock actions.

**Status:** CLOSED 2026-04-23. Fixed for v0.8.3.

---

## ~~487. Test Runner transparent background on scene change / Settings-hosted reopen path~~

**Source:** follow-up on the transparent `TestRunner` window after scene transitions. The original fix hardened the global Ctrl+Shift+T shortcut path, but the shared `ParsekUI` cache used by the Settings-hosted Test Runner and other Parsek windows could still cache a transparent or unreadable window style after scene changes / skin-lag frames.

**Fix:** opaque-window rebuilds are now gated on a ready normal background, lagging hover/focus/active states fall back to the ready normal texture instead of freezing a transparent cache, shared `ParsekUI` windows invalidate stale opaque styles across scene changes, and focused/active title-bar text colors are normalized so the opaque title bar stays readable after focus changes. The in-game regressions cover the missing-skin / lagging-state path, and the xUnit coverage now checks the readable title-text states too.

**Files:** `Source/Parsek/InGameTests/TestRunnerShortcut.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek/ParsekUI.cs`, `Source/Parsek/UI/TestRunnerUI.cs`, `Source/Parsek.Tests/ParsekUITests.cs`.

**Resolution:** shortcut path fixed on 2026-04-19 in `bug/487-test-runner-transparent`; the shared `ParsekUI` follow-up landed on 2026-04-20 so the Settings-hosted Test Runner and the rest of the Parsek subwindows now use the same guarded opaque-style rebuild instead of bypassing the original fix. A later live repro confirmed the transparent background was gone, and the final follow-up normalized title-bar text colors when window focus changed.
**Status:** CLOSED. Fixed for v0.8.3.

---

## ~~489. Headless xUnit finalization tests tripped the live incomplete-ballistic extrapolator through `FlightGlobals` before they reached their real fallback assertions~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing examples: `Bug278FinalizeLimboTests.FinalizeIndividualRecording_*` and `EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory`.

**Concern:** `#442` wired `IncompleteBallisticSceneExitFinalizer.TryApply()` into the default scene-exit finalization path. In headless xUnit, Unity's native `FlightGlobals` runtime is unavailable, so the default extrapolator could trip `FlightGlobals` static initialization before the tests ever reached the branches they were actually written to assert. That turned pre-existing fallback tests into engine-startup failures unrelated to their purpose.

**Fix:** `IncompleteBallisticSceneExitFinalizer.TryFinalizeRecording()` now probes `FlightGlobals.fetch` / `FlightGlobals.ready` behind a guarded cache and emits a focused `VERBOSE` line when the Unity runtime is unavailable. Permanent headless probe failures are cached so xUnit does not keep tripping the static initializer, but transient `ready=false` scene states are not cached, so a teardown frame cannot disable later live scene-exit finalization in the same KSP session. The default finalizer now also treats wrapped headless `SecurityException` chains as the same guarded decline path inside `TryFinalizeRecording()`, while the outer `TryApply()` fallback logs the same wrapped failure without permanently poisoning the cached runtime-availability state for later live KSP sessions. The Bug278 fixture explicitly resets the static finalizer seam between tests so cached/headless state cannot leak across unrelated fallback assertions. Hook / override-based tests still bypass the guard exactly as before. Added regression coverage in `Source/Parsek.Tests/SceneExitFinalizationIntegrationTests.cs`.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~497. Legacy exact-boundary recordings stopped backfilling an orbit endpoint after the same-UT stale-orbit guard landed~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `RecordingEndpointPersistenceTests.LoadRecordingFiles_LegacyRecording_BackfillsEndpointDecisionFromTerminalOrbitAlignedSegment`.

**Concern:** the stricter same-UT terminal-orbit guard was correct for live stale-cache overwrite prevention, but `RecordingStore.LoadRecordingFilesFromPathsInternal()` also uses endpoint backfill on recordings that have no persisted endpoint phase yet. That made exact-boundary legacy files fall back to the last point body instead of persisting the recorded terminal-orbit body on load.

**Fix:** `RecordingEndpointResolver.BackfillEndpointDecision()` now has a narrow legacy backfill path that only applies when there is no persisted endpoint decision, the recording already has an orbital terminal state, and the last orbit segment matches the cached terminal-orbit body. It logs the exact-boundary override and persists `EndpointPhase=OrbitSegment` without weakening the stricter live same-UT rejection used by `PopulateTerminalOrbitFromLastSegment()`.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~498. Headless surface-snapshot repair still reached `FlightGlobals.Bodies` when it rewrote a landed ORBIT node~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `SpawnSafetyNetTests.BuildValidatedRespawnSnapshot_SurfaceTerminalWithStaleOrbit_UsesEndpointSurfaceRepair`.

**Concern:** the first headless body-registry seam covered snapshot `REF` decoding and body-name lookup, but `ApplySurfaceOrbitToSnapshot()` still used `FlightGlobals.Bodies.IndexOf(body)` when it rewrote a landed snapshot to `SMA=0/ECC=1`. In headless xUnit that left the stale orbit node untouched even though the endpoint repair path had already resolved the correct body through the test seam.

**Fix:** `VesselSpawner` now resolves body indexes through a dedicated `BodyIndexResolverForTesting` seam, and both `ApplySurfaceOrbitToSnapshot()` and `SaveOrbitToNode()` use that helper instead of reading `FlightGlobals.Bodies` directly. The headless tests inject the same Kerbin/Mun registry for name, body, and index lookups, and the surface-repair path now emits a focused `VERBOSE` line if no body index can be resolved.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~499. Preserving legacy predicted orbit flags regressed two different recording-store paths~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `TrackSectionSerializationTests.SerializeTrackSections_V4Checkpoint_OmitsPredictedFlag` and `RecordingStorageRoundTripTests.CurrentFormatTrajectorySidecar_PredictedTailBeyondTrackSections_FallsBackToFlatBinaryAndRoundTrips`.

**Concern:** the earlier `isPredicted` preservation fix broadened `SerializeOrbitSegment()` itself, so legacy `TRACK_SECTION` checkpoints started writing `isPredicted=True` even in format v4, while the accompanying fallback guard became strict enough to reject perfectly valid flat tails whose appended suffix simply used older/non-monotonic test UTs. Those are different surfaces with different compatibility rules: legacy flat sidecars must preserve predicted orbit flags, but legacy track-section checkpoints must not grow the field retroactively.

**Fix:** `SerializeOrbitSegment()` now takes an explicit `writeLegacyPredictedFlag` switch. Flat trajectory sidecars opt in so legacy round-trips keep `isPredicted`, while `SerializeTrackSections()` leaves the flag omitted before `PredictedOrbitSegmentFormatVersion`. `FlatTrajectoryExtendsTrackSectionPayload()` also went back to its intended job: detect whether the flat lists extend the rebuilt section payload, without rejecting the suffix for unrelated monotonicity that only matters in the later healing paths.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~500. Post-walk partial-tracker integration still asserted the pre-`compared=` summary format~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `PostWalkReconciliationIntegrationTests.Integration_FundsTrackerUnavailable_PostWalkStillReconcilesTrackedLegs`.

**Concern:** the production reconciliation summary now includes `compared=` and `cutoffUT=`, but this fixture was still matching the older shorter string. That made the test fail even though the intended science-mismatch warning still fired and the reconciliation counters were correct.

**Fix:** updated the fixture to assert the current summary shape explicitly, including `compared=1` and `cutoffUT=null`, while still pinning the underlying science-mismatch warning. That keeps the test anchored to the real production log contract instead of the older format.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~501. Orbital-frame continuity started comparing equivalent quaternions with unstable raw signs across SOI and scene-exit frame reconstructions~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing examples: `BallisticExtrapolatorTests.Extrapolate_SoiTransitions_PreserveFrozenPlaybackWorldRotationAcrossSegments` and `SceneExitFinalizationIntegrationTests.SeedPredictedSegmentOrbitalFrameRotations_PreservesBoundaryWorldRotationAcrossSegments`.

**Concern:** the new orbital-frame encode/decode seam correctly preserved attitude in world space, but it did not canonicalize quaternion sign. Once the code started comparing raw quaternion components across different frame reconstructions, two equivalent orientations could show up with different signs and fail exact component assertions even though the underlying rotation was unchanged.

**Fix:** `BallisticExtrapolator` now canonicalizes quaternion sign when it computes or resolves orbital-frame-relative rotations. That keeps SOI handoffs and scene-exit seeded predicted tails on a deterministic raw-component representation without changing the represented world rotation.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~502. Narrowed sea-level impact scans could end exactly on the predicted crossing and miss the sign-change bracket entirely~~

**Source:** `Parsek-fix-xunit-failures` rerun on 2026-04-21. Failing example: `BallisticExtrapolatorTests.Extrapolate_LongHorizonSeaLevelImpact_NarrowsSurfaceScan`.

**Concern:** when `FindLocalCutoff()` found an analytic sea-level crossing, it narrowed the dense scan window to end exactly at that UT. If the analytic estimate landed slightly early, every sampled surface delta in the window could still stay positive, so `FindSurfaceCrossing()` never saw the `>0 -> <=0` bracket it needs and the extrapolator fell through to `Orbiting` instead of `Destroyed`.

**Fix:** the sea-level narrowed window now extends one cutoff sample step past the predicted crossing while staying clamped to the requested `endUT`. That preserves the narrowing optimization but guarantees the sampler still has room to capture the sign-change bracket when the analytic crossing lands a little early.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~504. `QuickloadResumeTests` stopped compiling after merging `main` because the fixture still seeded the old facility-upgrade enum name~~

**Source:** post-merge `dotnet build --no-restore` failure on `Parsek-fix-xunit-failures` (2026-04-21): `CS0117` on `GameStateEventType.FacilityUpgrade`.

**Concern:** `GameStateEventType` now exposes `FacilityUpgraded` / `FacilityDowngraded`, but the quickload-resume fixture was still seeding the removed `FacilityUpgrade` member. That is a pure test-side drift after merging `main`, but it stops the entire `Parsek.Tests` build before any of the real xUnit failures can run.

**Fix:** updated the fixture to seed `GameStateEventType.FacilityUpgraded`, which matches the current production enum contract while preserving the exact milestone/event setup that the baseline-restore test needs.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~490. Headless snapshot-validation tests reached Unity body lookup just to decode `VesselSnapshot.ORBIT.REF`~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing examples: `SpawnSafetyNetTests.BuildValidatedRespawnSnapshot_PersistedEndpointBodyMismatchWithoutCoordinates_Rejects` and `BuildValidatedRespawnSnapshot_SurfaceTerminalWithStaleOrbit_UsesEndpointSurfaceRepair`.

**Concern:** the failing logic under test was spawn-safety provenance repair, but the setup path decoded the snapshot's `ORBIT.REF` by reading `FlightGlobals.Bodies[refIndex]` directly. In headless xUnit that pulls in Unity's body registry before the test can even reach its real assertion, so a body-name lookup detail masks the actual endpoint-body decision logic.

**Fix:** `VesselSpawner` now routes snapshot `REF` decoding through `TryResolveBodyNameByIndex()`, with an internal `BodyNameResolverForTesting` override for headless tests. The later endpoint-body repair path now likewise resolves the loaded `CelestialBody` through `TryResolveBodyByName()`, with a matching `BodyResolverForTesting` override, so the stale-orbit / mismatch tests do not have to touch `FlightGlobals.Bodies` just to reach their real assertions. Production still defaults to the real Unity body registry, but both fallback lookups now decline with a focused `VERBOSE` log even when the headless `FlightGlobals` failure arrives through a deeper wrapped exception chain instead of a direct `TypeInitializationException(SecurityException)`. xUnit injects `{0: Kerbin, 1: Mun}` plus the matching test-body objects and pins both seams with direct regressions.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~491. Endpoint-aligned spawn-orbit selection could fall back across a body mismatch, while spawn-validation logs hid the vessel name behind generic caller context~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing examples: `SpawnSafetyNetTests.TryGetEndpointAlignedRecordedOrbitSeedForSpawn_PrefersTerminalOrbitMatchingEndpointBody`, `...ReturnsFalseWhenNoOrbitSeedMatchesEndpointBody`, and the snapshot-validation log assertions that expected vessel names like `Malformed Snapshot` / `Surface Repair`.

**Concern:** `TryGetEndpointAlignedRecordedOrbitSeedForSpawn()` delegated to the strict endpoint-body resolver first, then silently fell back to the older preferred-seed resolver even when that fallback pointed at a different body. That let a Kerbin terminal orbit leak into a Mun endpoint decision instead of returning false. In the same area, `BuildValidatedRespawnSnapshot()` and friends logged only the external caller label when one was supplied, so a generic context like `spawn-test` hid the actual vessel name the tests and triage needed.

**Fix:** endpoint-aligned orbit selection now stays strict: orbit-segment endpoints still prefer the last matching segment, non-orbit endpoints only accept a matching terminal-orbit tuple when the recording is still in an orbital terminal state, and if nothing matches the resolved endpoint body the helper returns false instead of broadening the search. The terminal-orbit-aligned branch now also refuses a same-UT conflicting last trajectory point on another body unless the final orbit segment truly extends past that point, and it emits a `VERBOSE` line with the conflicting bodies/UTs when that happens. That prevents landed/splashed recordings from reusing stale terminal-orbit tuples during malformed-snapshot repair or same-UT boundary cases. Spawn-validation messages now format context as `caller (VesselName)` when both exist, so repair/refusal logs keep the human vessel identity visible without dropping the caller label.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~492. Spawn-rotation coverage used Unity quaternion APIs directly inside headless xUnit tests~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing examples: all ten `SpawnRotationTests.*`.

**Concern:** the code under test is fine, but the test bodies themselves construct expected rotations with `Quaternion.Euler(...)`. That forces plain `net472` xUnit to execute Unity-native ECalls that only exist in a live KSP runtime, so the tests fail before they reach the actual spawn-rotation assertions.

**Fix:** moved the full spawn-rotation suite into `Source/Parsek/InGameTests/SpawnRotationInGameTests.cs` under `Category = "SpawnRotation"` / `Scene = GameScenes.FLIGHT`, preserving the same helper/log assertions with `InGameAssert` and Unity-backed quaternion semantics, including the explicit null-body warning/no-rotation-mutation regression that was briefly dropped in the first port. The old xUnit file is deleted instead of being left behind as skipped coverage.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~493. `TrimBoringTail` treated identity terminal rotations as authored surface poses and refused to trim stable landed tails~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing example: `RecordingOptimizerTests.TrimBoringTail_LandedStableTerminalState_StillTrims`.

**Concern:** `TryGetTerminalSurfaceReference()` read `SurfacePosition.HasRecordedRotation`, which treats identity as recorded when `rotationRecorded` is absent. The later point matcher only treats non-identity rotations as meaningful, so a perfectly upright landed tail compared "recorded identity" on the terminal pose against "no meaningful rotation" on the tail points and bailed instead of trimming.

**Fix:** `RecordingOptimizer` now normalizes terminal/surface poses the same way it already normalizes tail points: identity rotations are ignored for trim matching even when the serialized `SurfacePosition` reports them as recorded. The optimizer emits a focused `VERBOSE` line when it drops that identity rotation so the behavior stays pinned in tests.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~494. Legacy text trajectory sidecars silently dropped `OrbitSegment.isPredicted` on round-trip~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing example: `RecordingStorageRoundTripTests.TextTrajectorySidecar_PredictedOrbitSegment_RoundTrips`.

**Concern:** `SerializeOrbitSegment()` only wrote `isPredicted` once the recording format version reached the modern predicted-orbit threshold. That made legacy text sidecars serialize the orbital geometry correctly but silently clear the predicted flag during round-trip, which is exactly the kind of state drift the sidecar tests are supposed to catch.

**Fix:** text sidecar serialization now writes `isPredicted` whenever a segment is actually predicted, regardless of the legacy recording-format version. Older readers ignore the extra key, while current deserialization preserves the flag and the old-format round-trip keeps its predicted tail semantics intact.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~495. Flat-binary fallback detection rejected valid predicted tails because it re-validated monotonicity across the already-matched track-section prefix~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing example: `RecordingStorageRoundTripTests.CurrentFormatTrajectorySidecar_PredictedTailBeyondTrackSections_FallsBackToFlatBinaryAndRoundTrips`.

**Concern:** `FlatTrajectoryExtendsTrackSectionPayload()` first proves that the rebuilt track-section payload matches the flat trajectory prefix, then immediately re-checks monotonicity across the whole flat list. That can reject a valid extension case purely because the already-matched prefix has its own duplicated boundary shape, which means the code never reaches the intended flat-binary fallback path for a real predicted tail beyond the checkpoint payload.

**Fix:** the extension detector now applies its monotonicity guard only from the first appended point / orbit segment beyond the rebuilt track-section payload, while still checking the stitched boundary against the last rebuilt element. That keeps the defense against non-monotonic appended tails without misclassifying legitimate fallback cases that only extend a known-good section-authoritative prefix.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~496. Partial-tracker post-walk integration coverage seeded untagged events and created a spurious reputation mismatch~~

**Source:** `Parsek-fix-xunit-failures` clean local `dotnet test` run on 2026-04-21. Failing example: `PostWalkReconciliationIntegrationTests.Integration_FundsTrackerUnavailable_PostWalkStillReconcilesTrackedLegs`.

**Concern:** the test is meant to prove that when funds tracking is disabled, post-walk reconciliation still ignores the funds leg while correctly matching the tracked reputation leg and warning only on the mismatched science leg. But the fixture seeded all three observed events without a `recordingId`, and non-science post-walk reconciliation is recording-scoped. That makes the reputation event miss scope matching for the wrong reason, so the test can fail with a spurious rep warning even when the production code is behaving correctly.

**Fix:** the partial-tracker fixture now tags its seeded `FundsChanged`, `ReputationChanged`, and `ScienceChanged` events with `recordingId = "rec-partial"`, matching the action under test. That restores the intended coverage shape: funds remains ignored because tracking is disabled, reputation matches cleanly, and the test isolates the science mismatch it was written to pin.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~503. `AutoRecordDecisionTests` stopped compiling after merging `main` because a public xUnit theory exposed the internal restore-mode enum directly~~

**Source:** post-merge `dotnet test` compile failure on `Parsek-fix-xunit-failures` (2026-04-21): `CS0051` on `AutoRecordDecisionTests.ShouldIgnoreFlightReadyReset_RestoreOrMissingLiveState_ReturnsFalse(...)`.

**Concern:** `ParsekScenario.ActiveTreeRestoreMode` is intentionally internal production state, but the merged xUnit theory surfaced it directly as a public method parameter. C# rejects that accessibility mismatch before the test run even starts, so the whole branch stops at compile time for a pure test-fixture reason.

**Fix:** the theory now passes the restore mode as `(int)ActiveTreeRestoreMode.X` inline-data casts and casts back to the enum at the production call site. That preserves the exact coverage without widening production visibility or introducing test-only public surfaces, and keeps each case readable by its enum name at the `[InlineData]` call site instead of surfacing raw magic integers.

**Status:** CLOSED 2026-04-21. Fixed for v0.8.3.

---

## ~~488. Incomplete-ballistic scene-exit finalization accepted bad hook outputs and could overwrite hook-authored terminal endpoint data~~

## ~~486. Quicksave/quickload while recording on the runway produces a spurious-looking tree with a ~7s surface segment glued to a post-takeoff atmo segment — user-visible as "two recordings (landed + in air)" that both describe the same takeoff~~

**Source:** `logs/2026-04-19_2126/KSP.log` + `saves/c2/persistent.sfs`. User reported: "There was a problem with the runway R0 recording — I did a save/load while on the runway and it caused problems — created two recordings (one landed, one in air) which looked weird."

Reconstructed timeline for vessel `r0` (chainId `201ad985c4894c20a60cda3c4b878496`):

1. 21:19:15 — launch from Runway; Parsek arms recording.
2. ~21:20:00 — recording `ae5abeb1d4aa4ba8aafd46b68e74782d` opens (chainIndex=0, `segmentPhase=surface`).
3. 21:20:09 — F5 quicksave while still landed; OnSave flushes the in-progress tree.
4. 21:20:09 — F9 quickload; OnLoad detects UT-backwards (333.40 → 322.86) and calls `DiscardPendingTree`, then `RestoreActiveTreeFromPending` reopens the recording.
5. Takeoff rolls, vessel transitions LANDED→FLYING.
6. `FlightRecorder`'s environment-phase hysteresis fires `StopRecordingForChainBoundary` at the surface→atmo transition. New recording `05ddb2c3d11249f08da302012756ae67` opens (chainIndex=1, `segmentPhase=atmo`, pointCount=162, terminalState=4/destroyed).
7. 21:21:12 — merge commits with two `MergeTree` WARNs on the same chain:

```
[WARN][Merger] MergeTree: boundary discontinuity=105.72m at section[1] ut=329.70 vessel='r0' prevRef=Absolute nextRef=Absolute prevSrc=Active nextSrc=Active dt=0.10s expectedFromVel=0.50m cause=sample-skip
[WARN][Merger] MergeTree: boundary discontinuity=220.31m at section[2] ut=333.40 vessel='r0' prevRef=Absolute nextRef=Absolute prevSrc=Active nextSrc=Active dt=0.30s expectedFromVel=21.82m cause=sample-skip
```

The `ut=333.40` discontinuity coincides exactly with the quicksave UT — that's a ~220m position jump over a 0.3s stitch where the recorded velocity implied only ~22m of travel. The `ut=329.70` one is the surface→atmo phase boundary on a near-stationary vessel (expected 0.5m, saw 105m). Both are labelled `cause=sample-skip`, but neither was a legitimate sample gap — both are save/load teleports the merger is papering over.

**Concern:** the user is seeing two effects stacked on one action:

- (a) A zero-length-feel `surface` recording (~7 seconds, 22 points, ends mid-takeoff-roll) that by design is "correct" (every phase change opens a new segment since chain-phase split shipped), but because the user just did a quicksave/quickload, the segment's start and end positions don't agree with the atmo segment that follows — the visible playback glitches ~220m at the save boundary and ~105m at the phase boundary.
- (b) The post-load recorder appears to append to the same recording id from before the save. OnLoad discards the *pending* tree metadata (`DiscardPendingTree`) but the `.prec` file on disk for `ae5abeb1…` is not truncated/replaced, so the 22 points retained may be an inconsistent mix of pre-save and post-load samples — which is what the `cause=sample-skip` masking is hiding at section[2] on the merge.

This is the intended chain-segmentation design interacting badly with the quickload-rewind path: the environment-hysteresis state machine does not know it just crossed a scene reload, so it treats the UT-backwards post-load resume + natural LANDED→FLYING transition as a normal phase boundary when in fact the whole "landed" prefix should have been discarded (or never persisted).

**Fix:** two independent issues stacked — fix both.

- Truncate the pending recording's trajectory on quickload-rewind: when `DiscardPendingTree` (or its quickload-specific branch) fires, also truncate the `.prec` / in-memory sample buffer back to the post-rewind UT so post-load sampling does not produce a jagged prefix. Trace in `ParsekScenario.cs` OnLoad + the quickload-detection path that sets the UT-backwards flag; ensure the active-recording reopen in `RestoreActiveTreeFromPending` clears samples `> currentUT`.
- Reset the environment-phase hysteresis on restore so the first post-load phase transition is recognized as "restored state, not a live crossing" — either by snapshotting `phase` into the scenario OnSave and restoring it in OnLoad, or by forcing a re-sync from the live vessel situation one physics frame after the reopen. `FlightRecorder.TraceEnvironmentTransitions` and its phase-tracking fields are the owner.

Separately, the `MergeTree` "sample-skip" cause label is wrong for the observed shape: dt=0.30s with a 220m jump is not a skipped sample, it's a discontinuous sampling source. Make the merger distinguish `cause=save-load-teleport` from `cause=sample-skip` by cross-referencing `ParsekScenario.lastRestoreUT` (or add one if it doesn't exist) so future triage can tell the two apart at a glance.

**Files:** `Source/Parsek/ParsekScenario.cs` (OnLoad quickload branch + `RestoreActiveTreeFromPending`), `Source/Parsek/FlightRecorder.cs` (`TraceEnvironmentTransitions`, hysteresis state fields, sample-truncation on rewind), `Source/Parsek/RecordingTree.cs` or wherever `MergeTree` lives for the cause-label improvement.

**Scope:** Medium. Two related fixes; the sample-truncation is small, the hysteresis reset needs a reproducing test (in-game, because the UT rewind is what triggers it).

**Dependencies:** none. The `QuickloadResume` in-game test category (currently partially populated — several `(never run)` entries in the test report) is the right home for new regression coverage.

**Update (2026-04-19):** Implementation landed on branch `bug/486-quicksave-runway-restore`.

- The quickload-resume path now arms a restore-specific tree context, truncates every restored tree recording back to the current UT before sampling restarts, prunes future-only branch recordings / empty branch points left behind by that rewind, rebuilds `BackgroundMap`, and marks touched recordings dirty so stale future points / events / sections cannot survive into the resumed merge.
- `FlightRecorder` now derives the restore-environment resync target from the post-trim tail of the recording that actually resumes, so the one-shot relabel still applies when EVA parent-fallback rewrites `ActiveRecordingId` between F5 and F9.
- `SessionMerger` now labels overlap-derived active save/load seams as `cause=save-load-teleport` instead of falling through to the generic `sample-skip` bucket.
- Added headless regression coverage for tree-wide trim, post-trim tail-environment selection, restore-environment resync, and the merger label heuristic.

**Validation (2026-04-21):** the archived live quickload bundles `logs/2026-04-21_2041_live-collect-now/` and `logs/2026-04-21_2042_live-collect-script/` now provide the missing runtime evidence.

- `parsek-test-results.txt` records `FlightIntegrationTests.Quickload_MidRecording_ResumesSameActiveRecordingId` as `FLIGHT PASSED (6447.3ms)`.
- `KSP.log` logs the pre-F9 live recording id (`preRecId=ed1b9329360f488dbfac4b15cb4750a9`), then `Quickload tree trim: ... trimmedRecordings=1/1 prunedFutureRecordings=0 prunedBranchPoints=0`, then `Quickload resume prep: activeRec='ed1b9329360f488dbfac4b15cb4750a9' treeTrimmed=True`, and finally `RestoreActiveTreeFromPending: resumed recording tree ... activeRec='ed1b9329360f488dbfac4b15cb4750a9'`.
- That live path is launch-backed rather than the literal `r0` runway craft, so it only verified the shipped trim/resume seam and active-id reuse; it did not prove that a pre-liftoff runway quicksave would stop committing as separate `surface` + `atmo` phases.

**Reinvestigation (2026-04-22):** current `main` still allows a literal runway quicksave made before liftoff to finish as a short `surface` recording followed by an `atmo` recording, but that shape is not the old save/load seam bug:

- `FlightRecorder.PrepareQuickloadResumeStateIfNeeded()` trims and resumes against the post-cutoff tail environment. When that tail is still `SurfaceStationary`/`SurfaceMobile`, the later takeoff remains a real live surface→atmo boundary rather than a restore-only relabel.
- `SessionMerger.LooksLikeSaveLoadTeleportBoundary(...)` only tags overlap-derived restored seams. A normal runway surface→atmo handoff with no overlapping active section is not classified as `save-load-teleport`.
- `RecordingOptimizer.FindSplitCandidatesForOptimizer(...)` still intentionally splits non-EVA surface→atmo boundaries once both halves exceed 5 seconds, so the final committed runway tree can still look like a short surface segment glued to an atmo segment even though the quickload discontinuity itself is gone.

**Status:** CLOSED 2026-04-22 for v0.9.0. The original seam/discontinuity bug remains fixed; the follow-up diagnosis on 2026-04-22 was stale docs plus missing runway-specific regression coverage, not a new code gap on current `main`.

---

## ~~485. `StrategyLifecycle` readiness probe throws `NullReferenceException` for every stock strategy on every poll — ~1980 `[Parsek][WARN][TestRunner]` lines in a single session~~

**Source:** `logs/2026-04-19_2126/KSP.log` (≥ 1980 lines). Representative pair:

```
[WARN][TestRunner] StrategyLifecycle probe skipped strategy index 7 because readiness access threw: NullReferenceException: Object reference not set to an instance of an object
...
[WARN][TestRunner] FAILED: FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents - StrategyLifecycle readiness never stabilized: no CanBeActivated-true stock strategy available (count=11, null=0, active=0, configless=0, nameless=0, blocked=0, probeThrows=11, lastProbe='NullReferenceException: Object reference not set to an instance of an object')
```

Every readiness poll spins 11 strategy indices (0-10), each throwing the same NRE, emitting one WARN each — at ~11 polls per test invocation × N test runs per session the total dwarfs every other log source. The aggregated "`probeThrows=11`" in the final `FAILED:` line already captures the same information.

**Concern:** two distinct problems:

- (a) **Log volume regression.** PR #409 (fix for #480) added the per-index WARN to make failures legible, but every index in this career save throws, so the per-index lines are pure noise — the FAILED summary already tells you `probeThrows=11`. This violates the project's logging-efficiency principle (`VisualEfficiency`, CLAUDE.md "batch counting convention" — use aggregate summaries, not per-item). The spam also drowns out every other genuinely useful WARN during a test run.
- (b) **The underlying NRE is not resolved.** #480 closed with "fails loudly if readiness never settles" but didn't fix the throw; the four test failures (`ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` × 4, `FailedActivation_DoesNotEmitEvent` × 4) in the report are all caused by this. The probe still touches a null field somewhere in `CanBeActivated` access for every stock strategy on this save. `#480` has this in the fix-plan notes (`strategy.Config.Name` null guard, `StrategyLifecyclePatch` throwing, or stock `Activate()` NREing) — that investigation did not happen before the PR shipped.

**Fix shipped (2026-04-19):**

- Root cause found from local stock decompile + collected logs: `Strategies.Strategy.CanBeActivated` dereferences `Administration.Instance` immediately, and the SPACECENTER strategy probe was running before that singleton finished hydrating. This was a stock-readiness timing fault, not eleven different strategy-specific nulls.
- `Source/Parsek/InGameTests/RuntimeTests.cs` now gates the readiness probe on `Administration.Instance` before calling stock `CanBeActivated`, so the test no longer throws a per-strategy `NullReferenceException` during early KSC hydration.
- Any future unexpected `CanBeActivated` throws now emit one WARN summary per poll with the first failing index/exception, while the per-index detail moves to `VERBOSE`.
- Strategy lifecycle failure logging now carries `ex.ToString()` in the runtime tests and the Harmony postfix catches, so any future regression lands with the full stack trace instead of just `ex.Message`.
- Behavioral coverage now pins a final-state-only caller contract: an unresolved final readiness block or final poll exception still fails, but early hydration waits / probe exceptions that later clear no longer poison a later legitimate skip outcome.
- The bounded retry window now logs one INFO settle line when readiness recovers and one WARN timeout line with `attempt/max` counts when it does not.
- Verified scope for this landing is local code/log analysis plus new helper/state coverage; a live in-game rerun of the SPACECENTER strategy tests is still pending local environment blockers.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek/InGameTests/StrategyLifecycleProbeSupport.cs`, `Source/Parsek/Patches/StrategyLifecyclePatch.cs`, `Source/Parsek.Tests/StrategyLifecycleProbeSupportTests.cs`.

**Dependencies:** closes the open follow-up from `#480`.

---

## ~~484. `FlightIntegrationTests.TerminalOrbitBackfill_AlreadyPopulated_NoOverwrite` fails in FLIGHT — `PopulateTerminalOrbitFromLastSegment` overwrites an already-populated `TerminalOrbitBody` when the last segment's body disagrees~~

**Resolution (2026-04-19):** Closed on `bug/484-terminal-orbit-preserve`. Investigation confirmed the current code contract comes from `#475`, not `#289`: `TerminalOrbit*` is a healable cache, not immutable finalized metadata. `PopulateTerminalOrbitFromLastSegment` now preserves existing data only when the full cached terminal-orbit tuple already matches the endpoint-aligned last `OrbitSegment`; stale same-body tuples and stale different-body tuples both heal from that segment. The FLIGHT and xUnit regressions were tightened to pin all three cases explicitly: preserve-on-full-match, heal-on-stale-same-body, and heal-on-stale-different-body.

**Status:** CLOSED. Fixed for v0.8.3.

---

## ~~489. Manual-only runtime coverage for deferred merge commit and `Keep Vessel` playback existed locally; both now have live KSP validation~~

**Source:** local audit work on `audit-test-coverage-2026-04-19` after `#488` closed. New tests now exist in `Source/Parsek/InGameTests/RuntimeTests.cs`:

- `RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree`
- `FlightIntegrationTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce`

**What landed already:** the first test drives `ParsekScenario.ShowDeferredMergeDialog()` in `FLIGHT`, presses the real `Merge to Timeline` button, and asserts the synthetic pending tree moves into `RecordingStore.CommittedTrees` / `CommittedRecordings`. That path has now passed in a live KSP run. The second test commits a synthetic one-recording tree, calls `ParsekFlight.FastForwardToRecording(...)`, waits for a live ghost, and asserts the end-of-recording vessel spawn happens exactly once before cleanup/recovery.

**Resolution:** the deferred-merge canary passed live earlier, and the later direct `Kerbal Space Program/KSP.log` + `parsek-test-results.txt` rerun at `2026-04-20 00:32` closed the `Keep Vessel` side too. The first attempt hit the expected idle-flight guard and logged `SKIPPED`, but the actual row-play rerun passed once the session was idle. `KSP.log` shows the patched synthetic endpoint outside the KSC exclusion zone (`padDist≈240m`), a landed deferred spawn (`Vessel spawn for #2 ... sit=LANDED`), the runtime assertion log `Keep-vessel runtime: ... spawnedPid=...`, and the final `PASSED: FlightIntegrationTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce (6132.6ms)` line. That means both manual-only audit canaries are now live-validated.

**Historical next-gap note:** after this closure, the audit moved on to the stock `record -> revert -> soft-unstash / no merge` flow (`#490`) and then the real non-revert scene-exit deferred merge path (`#491`). Both are now closed below.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `docs/dev/test-coverage-audit-2026-04-19.md`, `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`.

**Status:** CLOSED. Fixed for the audit branch.

---

## ~~490. Manual-only stock `Revert to Launch` runtime coverage existed locally; it now has live KSP validation~~

**Source:** follow-up audit work after closing `#489`, aligned with shipped #434 behavior and the current user guide text.

**What landed already:** `FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog` now exists in `Source/Parsek/InGameTests/RuntimeTests.cs`. It starts a real recording on a prelaunch vessel, stages the active vessel, drives stock `FlightDriver.RevertToLaunch`, waits for the fresh FLIGHT scene, and asserts that:

- the reverted mission did not commit into `RecordingStore.CommittedRecordings` / `CommittedTrees`
- no Parsek `ParsekMerge` popup appears after the revert
- the pending tree was soft-unstashed rather than committed or hard-discarded
- the log stream contains the expected fresh-pending keep + soft-unstash lines from the #434 path

**Why this matters:** the audit roadmap used to describe the next gap as `record -> revert -> merge`, but that is no longer the shipped product contract. The current documented behavior is: revert soft-unstashes; if the player wants the merge dialog they take a non-revert exit such as `Space Center`. This runtime canary is the missing end-to-end proof for the actual shipped revert path.

**Resolution:** the direct `Kerbal Space Program/KSP.log` + `parsek-test-results.txt` rerun at `2026-04-20 00:57` now closes this. `parsek-test-results.txt` records `FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog` as `FLIGHT PASSED (7318.7ms)`. `KSP.log` contains the full shipped #434 revert sequence in one live pass: `Revert: keeping freshly-stashed pending`, then `Unstashed pending tree 'Kerbal X' on revert ... sidecar files preserved`, then `Revert flow runtime: ... committedBefore=2 committedAfter=2`, and finally the `PASSED:` row. That means the canary now has real evidence for the current product contract: stock revert soft-unstashes, does not open the Parsek merge dialog, and does not commit the reverted mission into the timeline.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek/ParsekScenario.cs`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`, `CHANGELOG.md`.

**Status:** CLOSED. Fixed for the audit branch. The then-open next player-flow gap (`#491`) is now closed below.

---

## ~~491. No live end-to-end runtime canary yet for the real non-revert scene-exit deferred merge path~~

**Source:** audit follow-up after `#489` and `#490` both gained live KSP validation.

**Current state:** the branch now has strong coverage for the synthetic FLIGHT deferred-merge popup (`RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree`), stock revert semantics (`FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog`), and the two manual-only `SceneExitMerge` stock save-and-exit canaries.

**Why this matters:** after #434, revert is no longer the merge entry point. The remaining live confidence gap is not “revert then merge”; it is the non-revert exit path that still owns merge UI in production.

**Validation (2026-04-21):** the archived sibling-workspace bundle `../logs/2026-04-21_1750_validate-batch-ui-terminalorbit-isolated/` closes the gap.

- `parsek-test-results.txt` records `FlightIntegrationTests.ExitToSpaceCenter_DeferredDiscardButton_ClearsPendingTree` as `SPACECENTER PASSED (9423.5ms)` and `FlightIntegrationTests.ExitToSpaceCenter_DeferredMergeButton_CommitsPendingTree` as `SPACECENTER PASSED (9707.1ms)`.
- `KSP.log` shows the real stock save-and-exit path surfacing `Showing deferred tree merge dialog in SPACECENTER`, then the discard branch logging `User chose: Tree Discard` plus `Scene-exit merge discard runtime: ... committedTreesAfter=0`, and later the merge branch logging `User chose: Tree Merge` plus `Scene-exit merge commit runtime: ... committedTreesAfter=1`.
- Those runs exercise the shipped player flow: launch from `PRELAUNCH`, leave the pad, invoke stock `Space Center` exit semantics, and resolve the production `ParsekMerge` popup in `SPACECENTER` instead of a synthetic dialog path.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `CHANGELOG.md`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~492. First timing-sensitive part-event runtime canaries are local-only; they still need live KSP evidence~~

**Source:** audit follow-up after implementing the first `PartEventTiming` tests in the audit worktree.

**Current state:** closed. `Source/Parsek/InGameTests/RuntimeTests.cs` contains `FlightIntegrationTests.PartEventTiming_LightToggle_AppliesAtEventUt` and `FlightIntegrationTests.PartEventTiming_DeployableTransition_AppliesAtEventUt` (the file is `RuntimeTests.cs`, but the owning/exported class name is `FlightIntegrationTests`). These deterministic `FLIGHT` runtime tests build synthetic ghost light / deployable states and assert `GhostPlaybackLogic.ApplyPartEvents(...)` flips them exactly at the authored UT boundaries. Retained April 21, 2026 live bundles now show both tests passing in `FLIGHT`.

**Why this matters:** the audit's remaining part-event gap was no longer "can ghost FX build at all?" Existing `PartEventFX` checks already cover that. The narrower missing confidence was timing: do visible state changes happen at the right moment? These two tests are the first concrete attempt to pin that down.

**Evidence:**

- `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-04-21_2008_finish-line-validation\parsek-test-results.txt` records both `FlightIntegrationTests.PartEventTiming_*` rows as `FLIGHT         PASSED`
- `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-04-21_2042_live-collect-script\parsek-test-results.txt` records the same two rows as `FLIGHT         PASSED`
- `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-04-21_2042_live-collect-script\KSP.log` records `Running`/`PASSED` for both canaries plus the `Applied 1 part events for ghost #902/#901` diagnostics at the event boundary

This closes the audit's first player-visible part-event timing gap for the light/deployable slice without requiring new live interaction in this worktree.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `CHANGELOG.md`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`.

**Status:** CLOSED - LIVE KSP VALIDATED (evidence retained in `C:\Users\vlad3\Documents\Code\Parsek\logs\`).

---

## ~~493. Destructive FLIGHT runtime tests now have a stronger retained isolated batch path, but final live validation is still pending~~

**Source:** follow-up from the test-coverage audit after repeated manual single-run passes for the destructive FLIGHT canaries became the main workflow bottleneck.

**Fix:** the in-game runner now has explicit `Run All + Isolated` / `Run+` entry points that capture a temporary uniquely-named baseline save in `FLIGHT`, then quickload that baseline between restore-backed destructive tests.

**Current state:** closed. The in-game runner now has explicit `Run All + Isolated` / `Run+` entry points that capture a temporary uniquely-named baseline save in `FLIGHT`, then quickload that baseline between restore-backed destructive tests. Retained evidence under sibling-workspace bundle `../logs/2026-04-21_2041_live-collect-now/` showed that path working on historical commit `80176033`: `parsek-test-results.txt` recorded `FLIGHT captured=180 Passed=153 Failed=0 Skipped=27`, including passes for:

- `RuntimeTests.AutoRecordOnLaunch_StartsExactlyOnce`
- `RuntimeTests.AutoRecordOnEvaFromPad_StartsExactlyOnce`
- `RuntimeTests.TreeMergeDialog_DiscardButton_ClearsPendingTree`
- `RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree`
- `FlightIntegrationTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce`
- `FlightIntegrationTests.BridgeSurvivesSceneTransition`
- `FlightIntegrationTests.Quickload_MidRecording_ResumesSameActiveRecordingId`
- `FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog`

`GhostPlaybackTests.RunAllDuringWatch_DoesNotLeakSunLateUpdateNREs` stayed save-dependent in that earlier retained session and skipped with `no same-body ghost available for watch-cleanup regression`, so the final widened cohort remained open until a fresh same-body packet was captured from the quickload-hardened revision.

**Validation (2026-04-22):** sibling-workspace bundle `../logs/2026-04-22_2118_validate-493-watch-cleanup-pass/` closes the final live-validation gap. That packet is tied to commit `24963d87` (`test: isolate explicit finalize-backfill runtime canary`), a test-only follow-up that does not alter production FLIGHT behavior.

- `parsek-test-results.txt` records `FLIGHT captured=190 Passed=154 Failed=0 Skipped=36` and `SPACECENTER captured=91 Passed=70 Failed=0 Skipped=21`.
- `FlightIntegrationTests.Quickload_MidRecording_ResumesSameActiveRecordingId` passed in `FLIGHT (6739.3ms)`.
- `GhostPlaybackTests.RunAllDuringWatch_DoesNotLeakSunLateUpdateNREs` passed in `FLIGHT (531.2ms)` once the retained session had a same-body ghost available.
- `KSP.log` records the intended cleanup order for the watch regression: `PerformBetweenRunCleanup: begin reason=ingame-watch-cleanup-regression`, then `Exiting watch mode before timeline ghost cleanup`, then `DestroyAllGhosts: clearing 1 primary + 0 overlap entries`.
- The same `KSP.log` contains no `Sun.LateUpdate`, `FlightGlobals.UpdateInformation`, or `NullReferenceException` signatures, and `log-validation.txt` passed.

**Caveat:** `SceneExitMerge` intentionally remains manual-only because the exit-to-KSC path is still too state-dirty to trust inside the isolated `FLIGHT` batch. That is a separate caveat from the destructive FLIGHT harness gap this item tracked.

**Status:** CLOSED (2026-04-22). Final live validation retained for v0.9.0.

---

## ~~494. Coverage-reporting scaffold exists, but there is still no validated baseline coverage report~~ CLOSED 2026-04-22

**Source:** audit backlog item 1 in `docs/dev/test-coverage-audit-2026-04-19.md` plus the latest collection bundles `logs/2026-04-21_2041_live-collect-now/` and `logs/2026-04-21_2042_live-collect-script/`.

**Resolution (2026-04-22):** CLOSED for v0.8.3. Running `pwsh -File scripts/test-coverage.ps1` from `C:\Users\vlad3\Documents\Code\Parsek\Parsek-bug-494` at commit `7216893f48b1e2c7b74ddcc3651cc3a31f531ff6` completed end-to-end and produced the first validated baseline coverage packet:

- restore/build succeeded for `Source/Parsek/Parsek.csproj` and `Source/Parsek.Tests/Parsek.Tests.csproj`
- xUnit result: `Passed: 7730, Skipped: 2, Total: 7732`
- Cobertura baseline: line `34220/82454 (41.50%)`, branch `15944/39907 (39.95%)`, method `56.28%`, `325` classes
- generated artifacts: `coverage..cobertura.xml`, `coverage-summary.txt`, and `dotnet-test.log`
- archived packet: `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-04-22_1850_coverage-baseline\`
- remaining non-blocking warnings: `InGameTestRunnerTests.FormatCoroutineState_ReportsActiveAndIdleSlots` (`xUnit1013`) and the two `KerbalsWindowUITests` substring assertions (`xUnit2009`)

**Why this matters:** the repo can now answer "what is the current mechanical coverage baseline?" with a real artifact instead of a doc claim.

**Follow-up:** future work should keep this packet shape repeatable, but the missing-baseline blocker itself is now closed. The next useful iteration is diff/CI retention, not more local runner churn.

**Files:** `Source/Parsek.Tests/Parsek.Tests.csproj`, `scripts/test-coverage.ps1`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`, `CHANGELOG.md`.

**Status:** CLOSED (2026-04-22). Baseline captured and archived.

---

## ~~495. No subsystem coverage matrix yet ties production areas to headless, runtime, log, and manual coverage~~ CLOSED 2026-04-21

**Source:** audit backlog item 2 in `docs/dev/test-coverage-audit-2026-04-19.md`.

**Resolution (2026-04-21):** CLOSED for v0.8.3. The repo now has a living [test-coverage-matrix.md](test-coverage-matrix.md) that maps major current-tree production subsystems to the four safety nets the project actually uses today: headless xUnit, in-game runtime, `KSP.log` contract validation, and manual scenario checklists.

Review follow-up tightened the first version before closeout: the missing group-hierarchy / visibility subsystem row was added, the stale `done/part-coverage-catalog.md` manual reference was removed, and the diagnostics row now anchors to production observability surfaces rather than framing `LogValidation/*` as a production owner.

**Why this matters:** future audit work now has one current-tree place to answer "which safety net covers this subsystem today?" instead of reconstructing that view from prose and grep each time.

**Files:** `docs/dev/test-coverage-matrix.md`, `docs/dev/todo-and-known-bugs.md`, `CHANGELOG.md`.

**Status:** CLOSED (2026-04-21). Fixed for v0.8.3.

---

## ~~496. Thinly covered IMGUI windows still need extracted pure helpers and headless tests~~

**Source:** audit backlog item 4 plus the "Priority 3" follow-up in `docs/dev/test-coverage-audit-2026-04-19.md`.

**Concern:** after the first slice landed, `TestRunnerUI` and the Ctrl+Shift+T shortcut shared `TestRunnerPresentation`, but the other thin IMGUI owners (`SettingsWindowUI`, `SpawnControlUI`, `GroupPickerUI`) still kept meaningful parse/sort/selection/tree rules inside draw code. That left UI regressions disproportionately dependent on live KSP playtests instead of direct headless ownership tests.

**Fix:** the remaining IMGUI owners now delegate their meaningful non-Unity logic to pure helpers: `SettingsWindowPresentation` owns auto-loop/camera-cutoff edit parsing plus the Defaults payload, `SpawnControlPresentation` owns Real Spawn Control sorting and per-row warp/state decisions, and `GroupPickerPresentation` owns normalized selection, common-group intersection, tree building, toggle rules, new-group validation, and membership deltas. Added focused headless coverage in `Source/Parsek.Tests/SettingsWindowPresentationTests.cs`, `Source/Parsek.Tests/SpawnControlPresentationTests.cs`, and `Source/Parsek.Tests/GroupPickerPresentationTests.cs`, completing the audit target list alongside the earlier `TestRunnerPresentation` tests.

**Deferred micro-follow-up:** this branch intentionally still reuses three older shared helpers instead of re-extracting them into the presentation layer: `SettingsWindowPresentation.TryResolveAutoLoopEdit(...)` still delegates to `ParsekUI.TryParseLoopInput(...)` / `ParsekUI.ConvertToSeconds(...)`, and `SpawnControlPresentation.BuildRowPresentation(...)` still uses `SelectiveSpawnUI.FormatCountdown(...)`. That is now a consistency follow-up rather than an audit blocker.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~497. Runtime-heavy builders and codecs still lack explicit ownership-style test bundles~~

**Source:** audit backlog item 5 plus the "Priority 4" follow-up in `docs/dev/test-coverage-audit-2026-04-19.md`.

**Resolution (2026-04-22):** CLOSED for v0.8.3. The earlier sidecar/generator ownership bundles are now matched by dedicated builder suites: `Source/Parsek.Tests/EngineFxBuilderTests.cs` owns headless-safe effect-group filtering, model/prefab config-entry parsing, fallback Euler selection, prefab rotation-mode decisions, and seam-level diagnostic log assertions for guard/fallback branches extracted from the runtime-heavy FX builder, and `Source/Parsek.Tests/GhostVisualBuilderTests.cs` now owns ghost snapshot selection/root parsing, prefab-name normalization, color-changer grouping, and stock explosion guard behavior. Live Unity object construction stays in the in-game runtime tests instead of xUnit, while true visual confirmation still relies on runtime/manual evidence rather than new headless assertions.

**Files:** `Source/Parsek/EngineFxBuilder.cs`, `Source/Parsek/GhostVisualBuilder.cs`, `Source/Parsek.Tests/EngineFxBuilderTests.cs`, `Source/Parsek.Tests/GhostVisualBuilderTests.cs`, `Source/Parsek.Tests/RecordingStoreTests.cs`, `Source/Parsek.Tests/GhostVisualFrameTests.cs`, `docs/dev/todo-and-known-bugs.md`.

**Status:** CLOSED 2026-04-22. Fixed for v0.8.3.

---

## ~~498. `validate-ksp-log.ps1` and exported in-game results are still not standardized release evidence~~ CLOSED 2026-04-21

**Source:** audit backlog item 6 plus the "Priority 5" follow-up in `docs/dev/test-coverage-audit-2026-04-19.md`.

**Resolution (2026-04-21):** CLOSED for v0.8.3. Release-closeout evidence is now an explicit documented process instead of an implied convention. [manual-testing/test-general.md](manual-testing/test-general.md) defines the named release scenario bundle and the required artifacts, and [development-workflow.md](development-workflow.md) points release/RC closeout at the same bundle flow.

The active process now requires:

- collecting the packet with `scripts/collect-logs.py`
- starting from a fresh `parsek-test-results.txt` export (`Reset` before the evidence run)
- requiring the named runtime rows in that file to be present and `PASSED`
- requiring `scripts/validate-ksp-log.ps1` to pass on the bundled `KSP.log`
- requiring `scripts/validate-release-bundle.py` to pass on the collected folder
- verifying the deployed `GameData/Parsek/Plugins/Parsek.dll` against the worktree build via the `.claude/CLAUDE.md` UTF-16 / size+mtime recipe before trusting any in-game evidence

Review follow-up also corrected the release doc wording to the shipped deferred merge-dialog scene-exit semantics, made the reset / fresh-session boundary explicit so stale cumulative PASS rows do not count as valid evidence, and added an explicit bundle-validation artifact (`release-bundle-validation.txt`) on top of `log-validation.txt`.

**Why this matters:** release conversations now have one canonical evidence bundle instead of relying on memory or ad-hoc packet selection.

**Files:** `scripts/collect-logs.py`, `scripts/validate-ksp-log.ps1`, `scripts/validate-release-bundle.py`, `docs/dev/manual-testing/test-general.md`, `docs/dev/development-workflow.md`, `docs/dev/todo-and-known-bugs.md`, `CHANGELOG.md`.

**Status:** CLOSED (2026-04-21). Fixed for v0.8.3.

---

## ~~483. `ScienceTransmission` earnings reconciliation warns fire repeatedly for stock science transmitted from a flight that included a landed-at-launchpad prologue — `window=[100.3,248.8]` with `expected=11.0` / `store=7.7`~~

**Source:** `logs/2026-04-19_2126/KSP.log` — ≥ 15 occurrences spread across 21:18:32..21:26:05. Representative pair:

```
[WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, sci): ScienceEarning ids=[mysteryGoo@KerbinSrfLandedLaunchPad, telemetryReport@KerbinSrfLandedLaunchPad, temperatureScan@KerbinSrfLandedLaunchPad, temperatureScan@KerbinFlyingLowShores, mysteryGoo@KerbinFlyingLow, telemetryReport@KerbinFlyingLow] across 6 action(s) expected=11.0 but no matching ScienceChanged event keyed 'ScienceTransmission' within recording window [100.3,248.8] for action ut=248.8 -- missing earning channel or stale event?
[WARN][LedgerOrchestrator] Earnings reconciliation (sci): store delta=7.7 vs ledger emitted delta=11.0 — missing earning channel? window=[100.3,248.8]
```

The recording window `[100.3,248.8]` contains six separate `ScienceEarning` ids split between `KerbinSrfLandedLaunchPad` (prologue on the pad before launch) and `KerbinFlyingLow` / `KerbinFlyingLowShores` (main flight). The ledger emits an expected earning of 11.0 but the store only captures a delta of 7.7 — a stable 3.3 gap.

**Concern:** post-fix regression of the #468 / #469 earning-channel family. #468 was about the `ScienceEarning` anchor UT being recovery-time while transmission events fire at transmission-time; #469 was about reconcile failing to find same-UT `FundsChanged` events. This new shape is specifically `ScienceTransmission` events firing on a flight that began with LaunchPad-situation experiments captured before takeoff, then transmitted after takeoff. The reconcile attempts to find the matching `ScienceChanged/ScienceTransmission` event for action ut=248.8 (recovery time) in window `[100.3, 248.8]` and fails — either the transmission event UT is outside that window, or the key dedup is dropping it.

The repeated firing (15+ times over 8 minutes of play) suggests the reconcile path is being re-run on every `RecalculateAndPatch` call and keeps logging the same stale mismatch without converging. That matches the #466 symptom (mid-flight `RecalculateAndPatch` patching funds with an incomplete ledger), which was closed — so either #466's fix didn't cover the sci channel, or this is a different code path.

**Resolution (2026-04-19, verified follow-up 2026-04-19):** CLOSED for v0.8.3. The one-shot ERROR dump was added first and showed the real store shape: the launchpad-prologue `ScienceChanged` events at UT `88.7` and `94.6` were untagged because the recording did not start until `100.3`, one real `+0.6` science delta never entered the store because `GameStateRecorder` still treated `ScienceThreshold = 1.0` as noise, and post-walk science reconciliation was still hardcoding every `ScienceEarning` leg to key `'ScienceTransmission'` even when the stock store event was actually `'VesselRecovery'`. Review follow-up then tightened two edges: collapsed large-UT persisted science spans are now reconstructed as bounded per-subject windows instead of silently widening back to the full recording span, and recorder-side science capture reuse is limited to real subject-science reasons so unrelated positive science rewards cannot be reused by `OnScienceReceived`.

The shipped fix does three things:

1. `GameStateRecorder.OnScienceReceived` now captures the most recent positive subject-science `ScienceChanged` UT/reason and persists that onto each `PendingScienceSubject`, while the recorder-side science threshold is hardened down to real noise-only values (`0.001`) so legitimate sub-1.0 science awards are no longer dropped. Follow-up review also tightened that reuse to the same recording, so stale science metadata cannot leak across recording boundaries.
2. `GameStateEventConverter.ConvertScienceSubjects` now carries that capture UT/reason forward into committed `ScienceEarning` actions (`StartUT` + `Method`), preserves same-recording launchpad-prologue capture windows before the official recording start, rejects stale cross-recording captures, and treats collapsed persisted large-UT spans as bounded float-bucket subject windows instead of widening back to the whole recording.
3. `LedgerOrchestrator` now uses those persisted science windows/reason keys for commit/post-walk reconciliation, emits a one-shot ERROR dump of nearby `ScienceChanged` events when a science window still fails, and suppresses repeated identical science WARNs once per window instead of flooding every recalculation.

**Files:** `Source/Parsek/GameStateRecorder.cs`, `Source/Parsek/GameStateEvent.cs`, `Source/Parsek/GameActions/GameStateEventConverter.cs`, `Source/Parsek/GameActions/LedgerOrchestrator.cs`, plus targeted coverage in `Source/Parsek.Tests/EarningsReconciliationTests.cs`, `Source/Parsek.Tests/GameStateEventConverterTests.cs`, and `Source/Parsek.Tests/GameStateRecorderResourceThresholdTests.cs`.

**Scope:** Medium. Landed as a recorder + converter + reconcile follow-up with targeted unit coverage, plus a narrow review follow-up for collapsed large-UT spans and capture-reuse eligibility.

**Dependencies:** #468, #469, #466, #405, #477 (all closed). This was the next link in that chain and is now closed.

**Status:** CLOSED (2026-04-19). Fixed for v0.8.3.

---

## ~~482. `Paths` security negative tests (`../etc/passwd`) log at WARN — 27+ lines per session from a tested-expected error path~~ CLOSED 2026-04-19

**Status:** CLOSED 2026-04-19. `RecordingPaths.ValidateRecordingId` now takes an explicit `RecordingIdValidationLogContext`; the runtime negative test and the existing acceptance-style xUnit invalid-id test pass `Test`, which demotes their expected rejection logs to `VERBOSE`, while production save/load/delete callers keep the default `WARN` behavior. Dedicated xUnit coverage intentionally keeps one production-context invalid-id path on `WARN` so the loud branch stays pinned too.

**Source:** `logs/2026-04-19_2126/KSP.log` — recurring:

```
[WARN][Paths] Recording id validation failed: id is null or empty
[WARN][Paths] Recording id validation failed: id is null or empty
[WARN][Paths] Recording id validation failed for '../etc/passwd': contains invalid path sequence
```

Fires three times per invocation of the `SerializationTests.RecordingPathsValidation` test (once per scene context, five scenes = 15 test runs = 45 WARN lines). Always triggered by the test itself, never by production code.

**Concern:** `RecordingPaths.ValidateRecordingId` is correctly rejecting a path-traversal id fed by the security test, but the rejection is logged at WARN. Production code never calls `ValidateRecordingId` with a bad id (callers filter upstream), so any real-world hit to this WARN is either (a) a test poking the rejection path, or (b) a real security incident worth a loud log. Conflating the two makes the test-path noise drown out the real-incident signal and clutters every test-run KSP.log.

**Fix:** implemented with an explicit `RecordingIdValidationLogContext` parameter on `ValidateRecordingId`. Expected negative-path runtime/acceptance tests opt into `Test`, which emits the existing rejection message at `VERBOSE`; production callers use the default `Production` context and keep the `WARN` signal for genuinely bad recording ids outside test code. Dedicated xUnit log assertions cover both the production `WARN` branch and the test-context `VERBOSE` branch, including the invalid-file-name-char rejection path.

**Files:** `Source/Parsek/RecordingPaths.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek.Tests/RecordingPathsLoggingTests.cs`, `Source/Parsek.Tests/SyntheticRecordingTests.cs`.

**Scope:** Small. One log-level change; any test-side scope probably doesn't need changing.

**Dependencies:** none.

---

## ~~481. `RuntimeTests.TimeScalePositive` intermittently fails in SPACECENTER — `Time.timeScale` observed as 0 during test execution, passes on retry~~

**Source:** `logs/2026-04-19_2126/parsek-test-results.txt:22` + `KSP.log` (three failure timestamps: 21:14:08.662, 21:15:00.972, 21:15:08.916). Passes in EDITOR / FLIGHT / MAINMENU / TRACKSTATION consistently, failed in SPACECENTER in three of eight observed runs (~37% flake rate).

**Resolution (2026-04-19):** fixed in `0.8.3`. The investigation showed this was not a Parsek `Time.timeScale` regression and not a one-frame scene-load race. All three failures happened entirely inside real stock pause windows:

- `Game Paused!` at `21:14:03.736`, then `RuntimeTests.TimeScalePositive` failed at `21:14:08.662`, then `Game Unpaused!` at `21:14:12.622`.
- `Game Paused!` at `21:14:57.191`, then the test failed at `21:15:00.972`, then `Game Unpaused!` at `21:15:04.782`.
- `Game Paused!` at `21:15:05.935`, then the test failed at `21:15:08.916`, then `Game Unpaused!` at `21:15:14.563`.

The runtime test now instruments the probe instead of asserting on a single frame: it samples up to 8 frames, logs `Time.timeScale`, `FlightDriver.Pause`, `KSPLoader.lastUpdate`, and the test-runner coroutine state on each poll, and only treats recovery as a pass when every earlier zero-timescale sample was observed under explicit stock pause. If any zero-timescale sample is explicitly stock-paused and none show an explicit `FlightDriver.Pause == false`, the result stays in the stock-pause bucket; only no-confirmation probes with at least one unavailable pause read skip with the distinct `FlightDriver.Pause unavailable` result. Any zero-timescale sample observed with an explicit `FlightDriver.Pause == false` still fails even if a later frame recovers.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek/InGameTests/InGameTestRunner.cs`, `Source/Parsek.Tests/TimeScalePositiveTests.cs`, `Source/Parsek.Tests/InGameTestRunnerTests.cs`.

**Status:** CLOSED. Fixed for v0.8.3.

---

## ~~480. `FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` / `FailedActivation_DoesNotEmitEvent` NRE ~2ms into SPACECENTER run on a career save with an activatable stock strategy~~

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt` + `KSP.log:9471-9474`.

```
[01:20:32.161] [VERBOSE][TestRunner] Running: FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents
[01:20:32.163] [WARN][TestRunner]    FAILED: ... - Object reference not set to an instance of an object
[01:20:32.169] [VERBOSE][TestRunner] Running: FlightIntegrationTests.FailedActivation_DoesNotEmitEvent
[01:20:32.170] [WARN][TestRunner]    FAILED: ... - Object reference not set to an instance of an object
```

~2ms from `Running:` → `FAILED:` on both, so the NRE fires very early in the test body. Save is career (`saves/c1/persistent.sfs` shows `Mode = CAREER`), so the career-mode and `StrategySystem.Instance != null` guards at `RuntimeTests.cs:3915-3932` both pass. The NRE happens further in — likely around `FindActivatableStockStrategy()` (`:3891-3907`) reading `strategy.Config.Name` when `Config` is momentarily null, `SnapshotFinancials()` (`:3847-3855`, defensive — probably not it), or `strategy.Activate()` call path (`:3975`) throwing in stock code for some reason.

**Concern:** these are both `#439` Phase A regression tests for `StrategyLifecyclePatch`. A failure here means one of: (a) the patch is throwing and bypassing the expected StrategyActivated emission, (b) the test helpers are fragile against the particular career-save state, (c) stock's `Strategy.Activate()` itself NREs on this save shape. Without a stack trace the three can't be distinguished from the log alone — the test runner reports `ex.Message` only.

**Fix:** first widen the test runner's failure capture so we get the stack trace. Add one line to whatever catches the test exception (grep for `FAILED:` emit site in `InGameTestRunner.cs`) to log `ex.ToString()` at WARN instead of just `ex.Message` — a stack trace turns this into a 5-minute fix instead of a week of guessing. Once the stack lands, root-cause the NRE:

- If it's `strategy.Config.Name` → tighten the null guard in `FindActivatableStockStrategy` to also require `s.Config.Name != null`.
- If it's inside `StrategyLifecyclePatch` postfix → the patch is throwing in a stock code path it didn't previously handle; fix the patch.
- If it's inside stock's `Activate()` → log a skip with the offending strategy's configName so future investigation has the signal, and move on.

Separately: the same save-state shape may make #439 Phase A behaviour unreliable in production, not just in the test harness. If the post-fix investigation reveals `StrategyLifecyclePatch` is the thrower, that's a shipped bug, not just a test fail.

**Files:** `Source/Parsek/InGameTests/InGameTestRunner.cs` (add `ex.ToString()` to the FAIL log), `Source/Parsek/InGameTests/RuntimeTests.cs:3891-3907` (possibly harden `FindActivatableStockStrategy`), `Source/Parsek/Patches/StrategyLifecyclePatch.cs` (if the postfix is implicated).

**Scope:** Small after the stack trace lands. Investigate first — don't patch blindly.

**Dependencies:** none (the other StrategyLifecycle work is on main already).

**Resolution:** fixed in PR #409 (`issue-480-stock-strategy-lifecycle`). Root cause landed in the test harness: probing stock strategies on the first SPACECENTER frames could catch the strategy system mid-hydration, and the tests also lacked targeted diagnostics around stock `Activate()` itself. The fix adds a bounded readiness/stability probe, rejects nameless configs, and fails loudly if readiness never settles or activation still throws after stabilization.

**Status:** CLOSED. Fixed for v0.8.3.

---

## ~~479. `FlightIntegrationTests.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty` fails in FLIGHT — `sit` field not refreshed from the live vessel after stable-terminal re-snapshot~~

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt:18, 41`.

```
FAIL  FlightIntegrationTests.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty (1.0ms)
      Snapshot sit field must be refreshed from the live vessel, not preserved from the stale source
```

**Concern:** the #289 re-snapshot invariant is that when `FinalizeIndividualRecording` runs on a stable-terminal recording (`TerminalStateValue` set) with a live active vessel, the recording's `VesselSnapshot` gets replaced by a fresh snapshot from that vessel, and `sit` should reflect the vessel's actual situation (LANDED/SPLASHED/etc.), not the stale "FLYING" from the original snapshot. The test at `RuntimeTests.cs:3219-3286` builds a recording with `TerminalStateValue = Landed` and a stale `sit=FLYING` snapshot, invokes `FinalizeIndividualRecording(rec, ..., isSceneExit: true)`, then asserts `sit != "FLYING"` — and that assertion fails (`:3276-3277`). So the current code either (a) doesn't replace the snapshot at all, (b) replaces it with a fresh snapshot whose `sit` was also written as FLYING (bug in `BackupVessel()` or equivalent), or (c) replaces it but doesn't persist the new `sit` value.

Corresponding post-#289 re-snapshot path in `ParsekFlight.cs` is around `:6917-6928` (the `backfilled TerminalOrbitBody=` logs visible in earlier collected logs confirm this path fires). Check whether the path calls `vessel.BackupVessel()` and writes the returned ConfigNode to `rec.VesselSnapshot`, or whether it only updates specific fields and skips `sit`.

**Concern (downstream):** if the re-snapshot keeps the stale FLYING sit, the spawn path at `VesselSpawner` (`ShouldUseRecordedTerminalOrbitSpawnState`, `:707`) or `SpawnAtPosition`'s situation override (`:317-320`) will receive a recording whose snapshot sit contradicts the terminal state — the spawner already has defensive overrides for this shape (`#176 / #264` per code comments), but the re-snapshot path fighting them is a separate source of drift and may silently persist the wrong sit to the sidecar (next load sees FLYING).

**Fix:** trace the re-snapshot invocation site and confirm it calls `vessel.BackupVessel()` fully, then writes the result to `rec.VesselSnapshot` (the full ConfigNode, not field-by-field). If it already does that, check whether `BackupVessel()` for a LANDED-situation vessel actually emits `sit = LANDED` (some stock KSP snapshot paths capture from a cached state that may still read FLYING for one frame after situation transition — a `yield return null` / physics-frame wait before the re-snapshot closes that). Add an explicit `sit` override on the fresh snapshot derived from `rec.TerminalStateValue` so the stored value always matches the declared terminal, regardless of when the snapshot capture fires relative to KSP's situation-update tick.

Test should keep passing once the path writes a consistent `sit`; no other assertion in the test needs changes.

**Files:** `Source/Parsek/ParsekFlight.cs` (re-snapshot path near `:6917`), possibly `Source/Parsek/VesselSpawner.cs` (`BackupVessel` usage), `Source/Parsek/InGameTests/RuntimeTests.cs:3219-3286` (no changes — the test is correct as-is).

**Scope:** Small. Likely a 5-line fix to force-set `sit` from the terminal state after `BackupVessel()`.

**Dependencies:** #289 original fix (shipped). This is the regression test catching a hole the original fix left.

**Status:** CLOSED 2026-04-19. Fixed in PR #407 — stable-terminal re-snapshots now normalize unsafe `BackupVessel()` `sit` values before persisting the fresh snapshot, so the FLIGHT regression and stale-sidecar drift are resolved.

---

## ~~478. `RuntimeTests.MapMarkerIconsMatchStockAtlas` runs in EDITOR / MAINMENU / SPACECENTER where `MapView.fetch` doesn't exist — should be scene-gated to FLIGHT + TRACKSTATION only~~

**Closed:** 2026-04-19 in PR #406.

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt:15, 21, 24, 434-438`.

```
[MapView]
  RuntimeTests.MapMarkerIconsMatchStockAtlas
    EDITOR         FAILED  (0.1ms) — MapView.fetch should exist — test requires flight or tracking station scene
    FLIGHT         PASSED  (0.5ms)
    MAINMENU       FAILED  (1.5ms) — MapView.fetch should exist — test requires flight or tracking station scene
    SPACECENTER    FAILED  (0.2ms) — MapView.fetch should exist — test requires flight or tracking station scene
    TRACKSTATION   PASSED  (3.4ms)
```

**Concern:** the `[InGameTest(Category = "MapView", ...)]` attribute at `RuntimeTests.cs:511-512` has no `Scene =` property, which defaults to `InGameTestAttribute.AnyScene = (GameScenes)(-1)` (`InGameTestAttribute.cs:18,21`). The test body requires `MapView.fetch` (available only in FLIGHT and TRACKSTATION per KSP's scene model) and correctly asserts its existence, but surfaces that assertion as a FAIL rather than a skip. Net effect: 3 of 5 scenes report FAIL for a test that is *expected* to only run in 2 scenes.

The `InGameTestAttribute` only supports a single `GameScenes` value; it can't express "FLIGHT OR TRACKSTATION" directly. Two valid fixes:

1. **Extend the attribute to accept a scene set.** Add a `GameScenes[] Scenes` property (or convert `Scene` to a `[Flags]`-like mask) and update `InGameTestRunner` scene-filter logic to match if any listed scene equals the current scene. More invasive; future-proofs other tests.
2. **Skip at the top of the test body** (`RuntimeTests.cs:513-…`) when `HighLogic.LoadedScene` is not FLIGHT or TRACKSTATION: `if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.TRACKSTATION) { InGameAssert.Skip("requires MapView scene"); return; }`. One-method change, keeps other callers of the attribute unaffected.

Option 2 is the cheapest and matches what several other tests already do internally (see `StrategyLifecycle` tests at `:3915-3932` for the skip pattern). Option 1 is worth doing only if a batch of other tests would benefit.

**Fix:** implemented option 2 — added the scene skip at the top of `MapMarkerIconsMatchStockAtlas`. Audited other `Category = "MapView"` / `Category = "TrackingStation"` tests; no other exposed `AnyScene` cases found.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs:513` (add skip), optionally `Source/Parsek/InGameTests/InGameTestAttribute.cs` if option 1 is chosen.

**Scope:** Trivial. 3-line skip + audit of adjacent tests.

**Dependencies:** none.

**Status:** CLOSED. Priority: low — fixed in PR #406. Unsupported scenes now skip instead of failing, so the per-scene report no longer shows three false FAILs for this test.

---

## 477. ~~Ledger walk over-counts milestone rewards — post-walk reconciliation `expected` sum is a 2× / 3× multiple of the actual stock enrichment~~

**Resolution (2026-04-19):** CLOSED. Re-investigation showed the ledger walk was not duplicate-crediting milestone rewards into career state. The real bug sat in `LedgerOrchestrator.ReconcilePostWalk`: it intentionally aggregated same-window `Progression` legs so one coalesced stock delta could match multiple milestone actions at the same UT, but it logged that aggregate under each individual `MilestoneAchievement id=...`. That made `expected=` look 2× / 3× too large whenever two milestones shared the window (for example `Mun/Flyby` + `Kerbin/Escape`, or the `RecordsAltitude` / `RecordsSpeed` bursts), and the missing-event path emitted one WARN per action instead of one WARN per coalesced window. The fix now compares/logs once per coalesced window, reports grouped ids/counts for shared funds/rep legs, and keeps single-contributor legs (such as `Kerbin/Escape` science) attributed to the single action. Regression tests cover both the aggregate-match path and the missing-event path.

**Source:** `logs/2026-04-19_0117_thorough-check/KSP.log`. Worked example for one Mun/Flyby milestone:

```
19799: [INFO][GameStateRecorder] Milestone enriched: 'Mun/Flyby' funds=13000 rep=1.0 sci=0.0
22085: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, funds): MilestoneAchievement id=Mun/Flyby expected=26200.0  (← 2× actual)
22086: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, rep):   MilestoneAchievement id=Mun/Flyby expected=3.0      (← 3× actual)
22087: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, sci):   MilestoneAchievement id=Mun/Flyby expected=1.0      (← actual is 0.0)
```

Same shape across `RecordsSpeed`, `RecordsAltitude`, `RecordsDistance` (hundreds of WARNs with expected 14400 / 9600 while stock gives 4800 per trigger — the Post-walk reconcile summary reports `actions=24, matches=0, mismatches(funds/rep/sci)=24/14/6` meaning ZERO of 24 actions matched; every single one had funds over-counted, 14 had rep over-counted, and 6 had sci incorrectly expected when the enrichment shows `sci=0.0`).

**Concern:** the reconciliation WARN phrasing ("no matching event") was misleading me earlier — on re-read, `CompareLeg` (`LedgerOrchestrator.cs:4248-4310`) sums `summedExpected` from `SumExpectedPostWalkWindow` which collects *every matching leg* across actions within the coalesce window. If the ledger holds multiple actions for the same milestone-at-same-UT (e.g. one per recording finalize pass, or one per recalc that re-replayed the same enrichment), `summedExpected` becomes N × the actual stock reward even though stock only fired it once. The reconciliation then correctly flags the mismatch — but the real bug is upstream: **the ledger is emitting a `MilestoneAchievement` action more than once per stock milestone fire**.

The duplicate emissions correlate with `actionsTotal` growing across recalcs even when no new stock events happened (`RecalculateAndPatch: actionsTotal=32 → 32 → 32 → 39` over a ~20ms span near `:01:07:54`). Each bump is a recording's commit path replaying its enrichment into the ledger without dedup.

This supersedes / refines #462 (prior observation was "double-count for a single milestone"; #477 is the general case across every milestone). #469's old "zero-match" shape turned out to be a separate stale-history live-store-coverage bug and is now closed independently; it did not resolve the duplicate-action emission tracked here.

**Fix:** trace the emission path. Two hypotheses, in priority order:

1. **Duplicate action insertion.** Every `LedgerOrchestrator.NotifyLedgerTreeCommitted` (or wherever `MilestoneAchievement` actions are created from recording state) re-inserts the action without checking whether an equivalent action already sits in the ledger. Expected fix: dedup by `(MilestoneId, UT, RecordingId)` — if the same triple is already present, skip. Watch out for the repeatable-record semantics (`RecordsSpeed` can legitimately fire multiple times per session at different UTs; dedup must be per-UT, not per-id).
2. **Recalc replay re-walks committed recordings without clearing prior action copies.** Between `Post-walk reconcile: actions=32` and `actions=39`, seven actions were added without corresponding new stock events. If `RecalculateAndPatch` clears the ledger and re-walks, it should land on the same 32 actions; if it doesn't clear first, each recalc adds another copy. Verify the clear path in `RecalcEngine.cs` / `LedgerOrchestrator.RecalculateAndPatch` entry.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`NotifyLedgerTreeCommitted`, `RecalculateAndPatch`), `Source/Parsek/GameActions/MilestonesModule.cs` (or whichever module emits `MilestoneAchievement` actions). Test: xUnit seeding a recording with one `MilestoneAchieved` event, calling `NotifyLedgerTreeCommitted` twice (simulating double-commit), asserts the ledger action count does not double and post-walk reconcile reports `matches=1, mismatches=0`.

**Scope:** Medium. Finding the exact emit site is the work; once identified, the dedup or clear-first fix is ~5 lines.

**Dependencies:** read `#307 / #439 / #440 / #448` notes in `done/todo-and-known-bugs-v3.md` first — those touched the earnings-reconciliation path and clarify which side of the dedup is the correct place to land the fix.

**Status:** CLOSED. Was high priority because the false-positive WARN volume obscured real reconciliation signals.

---

## ~~476. Post-walk reconciliation runs in sandbox mode (where KSP does not track funds/science/rep) and floods the log with "store delta=0.0" and "no matching event" false positives~~

**Source:** `logs/2026-04-19_0117_thorough-check/KSP.log`. The session was sandbox mode (`Funding.Instance is null (sandbox mode) — skipping`, `ResearchAndDevelopment.Instance is null (sandbox mode) — skipping`, `Reputation.Instance is null (sandbox mode) — skipping`, all repeating throughout) yet:

```
[WARN][LedgerOrchestrator] Earnings reconciliation (funds): store delta=0.0 vs ledger emitted delta=72800.0 — missing earning channel? window=[15.1,79.9]
[INFO][LedgerOrchestrator] Post-walk reconcile: actions=24, matches=0, mismatches(funds/rep/sci)=24/14/6, cutoffUT=null
```

`actions=24, matches=0` every single reconcile sweep — because stock KSP doesn't fire the Funds/Science/Reputation changed events in sandbox, so the store has nothing to compare against. The reconciliation is doing work and producing noise that has no actionable meaning on this save.

**Concern:** `LedgerOrchestrator.RecalculateAndPatch` unconditionally runs `ReconcilePostWalkActions` (and the window-level variant that emits the `store delta=0.0 vs ledger emitted delta=N — missing earning channel?` lines) regardless of whether KSP's tracked state is available. In sandbox every reconcile fires the full set of "mismatch" WARNs because the comparison baseline is zero. This compounds with #477 (duplicate emissions) to produce 700+ WARNs per session on a sandbox save that should have no WARNs at all.

Same concern applies to any save where the relevant `*.Instance` accessor is null for a legitimate game-mode reason (sandbox, tutorial, scenario that disables the currency).

**Fix:** at the entry of `ReconcilePostWalkActions` and `ReconcileEarningsWindow` (`LedgerOrchestrator.cs` around `:430-451` and `:4230` onward), gate the reconciliation per-resource on the KSP singleton availability. Pseudocode:

```csharp
bool fundsTracked = Funding.Instance != null;
bool sciTracked   = ResearchAndDevelopment.Instance != null;
bool repTracked   = Reputation.Instance != null;
// ... skip fund/sci/rep legs individually when their tracker is null
if (!fundsTracked && !sciTracked && !repTracked) return;
```

Log a single one-shot VERBOSE `[LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds={f} sci={s} rep={r})` so the skip is observable without being repeated every recalc. Existing `PatchFunds: Funding.Instance is null (sandbox mode) — skipping` pattern at `KspStatePatcher.cs` is the template.

Per-leg gating (not whole-sweep) is the correct granularity — a save that disables only one currency should still reconcile the other two.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`ReconcilePostWalkActions`, `ReconcileEarningsWindow`, `CompareLeg` entry). Test: xUnit seeding a `Funding.Instance = null` state (if the test rig can stub it) or verifying via the log sink that no WARN fires when `RecalculateAndPatch` is called with the trackers disabled.

**Scope:** Small. ~15 lines of gate + one log line.

**Dependencies:** none. Independent of #477 — fixing this one also reduces the reproducibility noise around #477 and #469 since a sandbox session after this fix would emit zero reconciliation WARNs.

**Resolution (2026-04-19):** Fixed in `fix-476-sandbox-postwalk`. `LedgerOrchestrator.ReconcileEarningsWindow` and `ReconcilePostWalk` now gate funds / science / reputation legs on live tracker availability, skip the whole sweep when all three trackers are unavailable, and emit a one-shot VERBOSE skip line instead of repeating WARNs every recalc. Added unit + integration coverage for both the all-trackers-missing sandbox shape and partial per-resource gating.

**Status:** CLOSED. Priority was medium — pure log-hygiene, but the hygiene payoff was large (a sandbox session goes from ~700 WARNs to 0).

---

## ~~475. Ghost whose recording terminates in Mun orbit spawns on a Kerbin-SOI-eject trajectory instead of in Mun orbit (post-rewind, map-view watch)~~

**Source:** user playtest report — "when recording a trip that ends in Mun orbit, after rewind when watching the ghost in map view, the ghost gets to the Mun encounter but then instead of spawning in Mun orbit, it spawns in a Kerbin SOI eject trajectory."

**Fix shipped:** finalize now persists an authoritative endpoint decision for each recording, so exact-boundary point-vs-orbit outcomes survive save/load without being re-inferred from the old epsilon-only winner check. Resolver/spawn code consumes that persisted decision when present; legacy recordings without the new fields self-heal on load by backfilling from terminal position, endpoint-aligned terminal-orbit data, the last trajectory point, or surface position. Terminal-orbit capture also no longer falls back to `"Kerbin"` when `orbit.referenceBody` is null, and orbit backfill only trusts a last segment when it agrees with the resolved endpoint body. This branch also closes the remaining spawn-correctness holes around that same bug shape: malformed snapshots are now repaired or refused instead of silently defaulting to Kerbin, chain-tip and ghost-map builders resolve orbit/body from the same endpoint-aligned contract as real-vessel spawn, and unsafe snapshot situation rewrites clear stale site labels along with the corrected `sit`.

**Tests:** added xUnit coverage for persisted exact-boundary capture/escape endpoint decisions, legacy endpoint-decision backfill across Kerbin-to-Mun end-state shapes, malformed-snapshot refusal, remaining ghost-builder endpoint alignment, and stale site-label clearing after unsafe snapshot rewrites; #484 follow-up adds xUnit and runtime coverage for endpoint-aligned terminal-orbit backfill, preserve-vs-heal observability, invariant-culture tuple-heal logs, and endpoint-aligned orbital spawn-seed selection.

**Status:** done/closed on this branch. Priority was high because the bad cached body could throw the spawned vessel onto a solar-escape path after rewind and effectively destroy the mission outcome.

---

## ~~474. Ghost audio sometimes plays in a single stereo channel instead of centered when the Watch button snaps the camera to the ghost~~

**Status:** DONE / closed 2026-04-19.

**Fix shipped:** fresh ghost builds now recalculate `cameraPivot` immediately instead of leaving the initial watch target at the raw root origin; ghost loop + one-shot `AudioSource`s are then re-anchored to that watch pivot, forced back to `panStereo = 0`, and run with `spatialBlend = 0.75f` instead of fully-3D `1.0f`. `HideAllGhostParts()` also mutes those detached ghost audio sources when the ghost is hidden. That keeps Watch-mode framing and the dominant ghost audio source aligned, so engine/explosion playback no longer hard-pans into one ear when the camera snaps to the ghost.

**Verification added:** runtime coverage now checks both invariants directly: `cameraPivot` recenters to the active-part midpoint on a fresh ghost, and re-anchored ghost audio sources land on `cameraPivot` with centered stereo defaults.

---

## ~~473. Gloops group in the Recordings window should be treated as a permanent root group — no `X` disband button, and pinned to the top of the list~~

**Source:** user playtest request.

**Concern:** the Gloops group is created by `RecordingStore.CommitGloopsRecording` at `Source/Parsek/RecordingStore.cs:394-409` and uses the constant `GloopsGroupName = "Gloops - Ghosts Only"` (`:63`). In `UI/RecordingsTableUI.cs:1725`, the disband-eligibility gate reads `bool canDisbandGroup = !RecordingStore.IsAutoGeneratedTreeGroup(groupName);` — so auto-generated tree groups get a single `G` button, but the Gloops group falls through to `DrawBodyCenteredTwoButtons("G", "X", …)` at `:1734`, exposing an `X` that invokes `ShowDisbandGroupConfirmation`. Disbanding the Gloops group would leave new Gloops commits either re-creating it on the next commit (`:408-409` re-adds the name when missing) or reverting to standalone, and there is no user story for disbanding a system-owned group.

Separately, root-group ordering is decided by `GetGroupSortKey` + the column's sort predicate in `RecordingsTableUI.cs:1077-1079`. The Gloops group ends up wherever its sort key lands among the user's trees/chains, which is inconsistent frame-to-frame as the user sorts by different columns.

**Fix:** DONE. Added `RecordingStore.IsPermanentGroup` / `IsPermanentRootGroup`, switched the Recordings-table disband gate to the permanent-group predicate, and moved root-item sorting behind a dedicated comparator that pins the Gloops group above every other root item regardless of sort column or ascending/descending state. Also hardened `GroupHierarchyStore.SetGroupParent` so `Gloops - Ghosts Only` cannot be nested under another group, and `BuildGroupTreeData` now self-heals any stale permanent-root hierarchy mapping back to root on first draw rather than only papering over one saved-parent case.

**Edge case checked:** the legacy rename path still runs in `RecordingTree.LoadRecordingFrom` before UI grouping/sorting sees the loaded recording groups, so pre-rename saves normalize to the modern `Gloops - Ghosts Only` name before the permanent-group rules apply.

**Files:** `Source/Parsek/UI/RecordingsTableUI.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek/GroupHierarchyStore.cs`, plus xUnit coverage in `Source/Parsek.Tests/GroupManagementTests.cs`, `Source/Parsek.Tests/GroupTreeDataTests.cs`, and `Source/Parsek.Tests/RecordingsTableUITests.cs`.

**Scope:** Small. Slightly larger than the original estimate because the final fix also closes the parent-assignment side door and auto-heals old hierarchy state.

**Dependencies:** none.

**Status:** ~~DONE~~. Priority was low-medium — UI polish, but now shipped independently of #471 because the root-group semantics were self-contained and low-risk.

---

## ~~472. Watch-mode camera pitch/heading jumps when playback hands off to the next segment within a recording tree (e.g. flying → landed)~~

**Source:** user playtest report — "when watching a recording, maintain the camera watch angle exactly the same when transitioning to another recording segment (right now it moves when vessel is going from flying to landed for example)."

**Concern:** inside a single tree (chain/branch) the active playback ghost changes at each segment boundary (flying recording ends, landed recording's ghost becomes the new camera target). Investigation on `origin/main` confirmed the explicit tree-segment transfer path (`TransferWatchToNextSegment`) already captured and replayed `WatchCameraTransitionState`; the remaining snap lived in adjacent watch retarget paths that still did raw `FlightCamera.SetTargetTransform(...)` calls. The exposed offenders were the loop/overlap `RetargetToNewGhost` handlers plus the quiet-expiry / primary-cycle fallback, overlap-hold rebind, and stock vessel-switch re-target path. Those branches swapped the target transform without replaying the current pitch/heading, so the camera yanked to whatever framing the new target basis implied.

The existing loop-cycle-boundary code path (`CameraActionType.RetargetToNewGhost` inside `HandleLoopCameraAction`) has the same shape — if the bug reproduces at loop boundaries too, the fix covers both. Confirm during fix.

**Fix landed (2026-04-19):** centralized the retarget angle replay around `TryResolveRetargetedWatchAngles` inside `WatchModeController`'s watch-camera rebind path. Every watch-mode ghost rebind that should preserve framing now captures the current watch camera state, primes the replacement ghost's `horizonProxy` before target selection when `HorizonLocked` is active, then re-targets and replays compensated pitch/heading in the new target basis instead of doing a raw `SetTargetTransform(...)`. Applied to:

- loop `RetargetToNewGhost`
- overlap `RetargetToNewGhost`
- watched-cycle fallback to primary
- overlap-hold completion rebind
- quiet-expiry bridge retarget
- stock `OnVesselSwitchComplete` re-target

Edge cases to cover in the test matrix:
- `HorizonLocked` mode (default on entry) — pitch/heading are relative to the horizon and must survive the target swap
- `Free` mode — same requirement, but the relative frame is the ghost's local frame
- Overlap retarget vs non-overlap — both code paths at `:711` and `:731`
- Loop cycle boundary — verify same issue/fix

**Files:** `Source/Parsek/WatchModeController.cs` (retarget sites + helper plumbing), `Source/Parsek.Tests/WatchModeControllerTests.cs` (pure retarget-angle regression coverage). Verification in this environment used `dotnet build --no-restore` plus a direct reflection harness over the compiled `WatchModeControllerTests` methods because the standard `dotnet test` runner aborts here on local socket initialization (`SocketException 10106` before test execution starts).

**Scope:** Closed in a small controller-only patch. The explicit tree-transfer path needed no change; the missing coverage was the remaining raw re-target sites around loop/overlap and stock camera rebinds.

**Dependencies:** none.

**Status:** Closed — shipped for v0.8.3 as a targeted watch-camera retarget preservation pass. Priority was medium.

---

## ~~471. Gloops recordings should not loop by default; commit path should set `LoopPlayback=false` and `LoopIntervalSeconds=0` (auto)~~

**Source:** user request — "gloops recordings should no longer be looped by default and their loop period should be set to auto when they are created."

**Status:** ~~Fixed~~ in this PR. `ParsekFlight.CommitGloopsRecorderData` now writes `LoopPlayback=false`, `LoopIntervalSeconds=0`, and `LoopTimeUnit=LoopTimeUnit.Auto` before `RecordingStore.CommitGloopsRecording`, so fresh Gloops captures no longer start looping immediately and the stored "auto" period behaves correctly if the player later turns looping back on.

Comments/docs updated in `ParsekFlight`, `GloopsRecorderUI`, and `docs/user-guide.md` so the user-facing text matches the shipped behavior.

Regression coverage now invokes `CommitGloopsRecorderData` with a minimal `ParsekFlight` + `FlightRecorder` harness and asserts the committed recording is ghost-only, non-looping, and stored with the auto period settings. The commit path now resolves the active-vessel name through a small defensive helper so the unit test can run without KSP's Unity-backed `FlightGlobals` static initializer.

No schema change: existing recordings that already have `LoopPlayback=true` are preserved as-is.

---

## 470. ~~`Funds` subsystem logs `FundsSpending: -0, source=Other` hundreds of times per session (134 lines in one 15-minute career run)~~ CLOSED 2026-04-19

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log`. Top-of-list pattern in the deduplicated WARN/VERBOSE counts:

```
134 [Parsek][VERBOSE][Funds] FundsSpending: -0, source=Other, affordable=true, runningBalance=N, recordingId=(none)
```

**Concern:** every `RecalculateAndPatch` sweep (33 of them in this session) fans out to the per-module replay, and each module emits a `FundsSpending: -0` line for zero-delta entries inside the "Other" source bucket. Zero-delta spendings convey nothing a reader would ever act on, and at 4 per recalc × 33 recalcs = 132 lines, they bury the real entries. Adjacent modules already early-return on zero-delta (see the verbose threshold filters in `GameStateRecorder.cs`), so this one is the odd one out.

**Fix shipped (2026-04-19):** `Source/Parsek/GameActions/FundsModule.cs` now suppresses the success VERBOSE log when `FundsSpent == 0`, which is enough to eliminate the `FundsSpending: -0` replay spam without hiding any real low-value spendings. The action still flows through affordability and running-balance updates unchanged.

**Files:** `Source/Parsek/GameActions/FundsModule.cs`, `Source/Parsek.Tests/FundsModuleTests.cs`. Added `FundsSpending_ZeroCost_DoesNotLogVerboseSpend`, which submits a zero-cost `FundsSpending(Other)` action, asserts the action remains affordable with no balance change, and confirms the log sink stays silent.

**Scope:** Trivial. One-line guard + one test.

**Dependencies:** none.

**Status:** CLOSED 2026-04-19. Priority: low — pure log-hygiene. Ready to ship with the targeted regression coverage above.

---

## ~~469. Post-walk reconciliation fails to find same-UT FundsChanged events that are demonstrably in the store — "no matching event keyed 'Progression'" warns fire on events that exist~~

**Source:** `logs/2026-04-19_0014_investigate/KSP.log` and `logs/2026-04-19_0123_test-report/Player.log`.

**Root cause (confirmed):** `CompareLeg` itself was not losing a same-UT same-key live event. The false WARNs happened later, after `GameStateStore.PruneProcessedEvents()` had already removed the relevant `FundsChanged(Progression)` rows from the live store. `ReconcilePostWalk` walks the full ledger history on every recalc, but the store only retains the current live tail: resource events at or below the latest committed milestone `EndUT` are pruned, and after a rewind/load the current epoch may also have no live-event coverage for older-epoch ledger actions. That is why the logs could show an earlier `AddEvent: FundsChanged key='Progression' ... ut=57.2` line and then later emit `no matching event` WARNs for the same milestone: the event existed when recorded, but no longer existed in the live store by the time the later recalc pass ran.

**Fix (2026-04-19):** gate post-walk reconciliation to the portion of ledger history the live store can still represent. `ReconcilePostWalk` now skips:

1. actions at or below the current epoch's prune threshold (`MilestoneStore.GetLatestCommittedEndUT()`), because their paired resource events have already been consumed and removed; and
2. pre-live-tail history after an epoch bump, where the current epoch has neither a live source anchor nor any live observed reward leg for the historical action being revisited.

If a live observed reward still exists but there is no live source anchor, the reconcile only continues when the same-UT window is unambiguous; otherwise it emits a one-shot VERBOSE coverage-skip and leaves the ambiguous stale-history action alone.

Live-tail mismatches still WARN normally, so this is not a blanket suppression of `Progression` reconciliation.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs`; targeted regressions in `Source/Parsek.Tests/EarningsReconciliationTests.cs`.

**Verification:** build succeeded in the worktree. Targeted regression methods executed directly from the built test assembly:

- `PostWalk_MilestoneAchievement_EffectiveTrue_AllLegsMatch_NoWarn`
- `PostWalk_MilestoneAchievement_EffectiveFalseDuplicate_Skipped_NoWarn`
- `PostWalk_MilestoneAchievement_CoalescedWindow_MatchesOnce_NoWarn`
- `PostWalk_MilestoneAchievement_CoalescedWindow_MissingEvent_WarnsOncePerLeg`
- `PostWalk_MilestoneAchievement_CoalescedTinyScienceLegs_AggregateWarnsOnce`
- `PostWalk_MilestoneAchievement_PrunedByCommittedThreshold_Skipped_NoWarn`
- `PostWalk_MilestoneAchievement_WithoutLiveSourceAnchorInNewEpoch_Skipped_NoWarn`
- `PostWalk_MilestoneAchievement_WithLiveSourceAnchorInNewEpoch_MissingFundsEvent_Warns`
- `PostWalk_MilestoneAchievement_WithLiveFundsButNoSourceAnchorInNewEpoch_DoesNotSkip`
- `PostWalk_MilestoneAchievement_StaleNeighborInsideCoalesceWindow_DoesNotInflateLiveExpected`
- `PostWalk_MilestoneAchievement_StaleObservedEventIgnored_InLiveWindow`
- `PostWalk_MilestoneAchievement_ThresholdStraddlingStaleNeighbor_DoesNotSuppressLiveFallback`
- `PostWalk_MilestoneAchievement_LiveNoSourceOverlap_SkipsAmbiguousFallback`

All passed.

**Dependencies / follow-up:** independent of #462 / #477. Those entries are about duplicate milestone-action emission and over-counted expected deltas; this fix only closes the stale-history zero-match WARN path.

**Status:** ~~TODO~~ CLOSED for v0.8.3.

---

## ~~468. `ScienceEarning` reconcile anchor UT is vessel-recovery-time, but `ScienceChanged 'ScienceTransmission'` events are emitted at transmission-time earlier in the flight — the 0.1s window can never match~~

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log:10410-10415`.

```
[WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, sci): ScienceEarning id=mysteryGoo@KerbinSrfLandedLaunchPad expected=11.0 but no matching ScienceChanged event keyed 'ScienceTransmission' within 0.1s of ut=204.4
```

Paired with the actual capture sequence earlier in the same session:

```
9272: [GameStateRecorder] Emit: ScienceChanged key='ScienceTransmission' at ut=39.8
9273: [GameStateStore] AddEvent: ScienceChanged key='ScienceTransmission' ut=39.8
9488: [GameStateStore] AddEvent: ScienceChanged key='ScienceTransmission' ut=66.3
```

**Concern:** the `ScienceEarning` ledger actions created from a committed recording are timestamped to the vessel-recovery UT (here 204.4 — the recovery event), but the KSP `ScienceChanged` events fire whenever stock transmits/completes a science subject, which for an in-flight launch is typically 20-100 seconds into the flight, long before recovery. `CompareLeg`'s `Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds (0.1s)` gate then rejects the only events that could possibly match, and every recovered science subject produces a post-walk WARN.

Independent of #469 (where the event IS at the right UT and the reconcile still fails): this is the case where the event is at the wrong UT *for this particular leg*. Both show up in the same session; fixing one does not fix the other.

**Fix:** two options, pick based on the semantic of `ScienceEarning`:

1. **Anchor the action to transmission UT**, not recovery UT. If the ledger action is meant to reconcile with the per-subject transmission event, the action's UT should track the event's UT. This may require `ScienceModule.cs` (the emit site) to carry the per-subject transmission timestamp forward into the action instead of collapsing to recovery UT.
2. **Broaden the reconcile window for `ScienceEarning`** to cover the entire recording's UT span (e.g. accept any matching ScienceChanged event between recording start and recovery). Keep the 0.1s window for `MilestoneAchievement` where the instantaneous match is correct.

Option 1 is cleaner but touches the emit path; option 2 is localised to `CompareLeg` / `ReconcilePostWalkActions` in `LedgerOrchestrator.cs`.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (+ possibly `ScienceModule.cs`). Test: xUnit seeding a ScienceEarning at recovery UT and a ScienceChanged at an earlier UT within the same recording span, asserts no post-walk WARN.

**Scope:** Small-to-medium depending on option. Option 2 is ~20 lines; option 1 requires an action-schema nudge.

**Dependencies:** surface with #469 during the same investigation — root-cause signal will tell which option is right.

**Status:** DONE/CLOSED (2026-04-19). `CompareLeg` now widens the observed-side `ScienceTransmission` match window to the owning recording span only for end-anchored `ScienceEarning` actions, and new science actions persist `StartUT`/`EndUT` so reloads keep the same reconcile context. `#469` remains separate: it is the same-UT false-negative path, not this earlier-transmission window mismatch.

---

## ~~467. `ReputationChanged` threshold filter rejects stock +1 rep awards — `Math.Abs(delta) < 1.0f` drops `0.9999995` rewards, breaking all records-milestone rep reconciliation~~

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log`.

```
9473: Added 0.9999995 (1) reputation: 'Progression'.
9476: [Parsek][VERBOSE][GameStateRecorder] Ignored ReputationChanged delta=+1.0 below threshold=1.0
```

**Concern:** stock KSP awards `0.9999995` reputation for Records* milestones (the `(1)` in the log is the rounded display value; the actual delta is `~1 − 5e-7`). `GameStateRecorder.cs:910` drops the event with `if (Math.Abs(delta) < ReputationThreshold)` where `ReputationThreshold = 1.0f` (`:222`). `0.9999995 < 1.0` is true, so the event never makes it into the store. The post-walk reconcile for the paired `MilestoneAchievement` rep leg then reports "no matching ReputationChanged event keyed 'Progression' within 0.1s" — in this session that produced all 44 rep-mismatch WARNs (`RecordsSpeed`, `RecordsAltitude`, `RecordsDistance` each firing two per recalc pass).

**Update (2026-04-19):** Fixed in the `#467` worktree. `OnReputationChanged` now keeps a small `0.001f` epsilon under the `1.0f` threshold so stock-rounded `0.9999995` awards still survive after cumulative-float subtraction (`old + reward - old` can land slightly below `0.9999995`, e.g. `0.99999x`). Added regression coverage in `Source/Parsek.Tests/GameStateRecorderResourceThresholdTests.cs` for both raw `+/-0.9999995`, the cumulative-float subtraction shape, and clear sub-threshold control cases.

**Original fix sketch:** one-line change in `Source/Parsek/GameStateRecorder.cs:910`:

```csharp
// Before:  if (Math.Abs(delta) < ReputationThreshold)
// After:   if (Math.Abs(delta) < ReputationThreshold - 0.001f)
```

Or lower `ReputationThreshold` to `0.5f` (any value strictly below stock's 0.9999995 — `0.5f` leaves headroom for other rounding cases while still filtering sub-integer noise). Pick the second form if you want one named constant doing the semantic work; the epsilon form is narrower but preserves the visible `1.0` threshold.

Similar care needed for `FundsThreshold = 100.0` and `ScienceThreshold = 1.0` — confirm stock never rewards *exactly* threshold values; if it does, apply the same epsilon trim.

**Files:** `Source/Parsek/GameStateRecorder.cs:910` (rep), possibly `:821` (funds) and the ScienceChanged analogue. Test: xUnit calling the onReputationChanged handler with delta `0.9999995f`, asserts the event is captured in the store (not dropped).

**Scope:** Trivial. One-line fix + one test + verify the twin thresholds.

**Dependencies:** none. Fixes the rep-mismatch tail of #469 specifically, though the underlying #469 investigation may also surface non-rep mismatches unrelated to this threshold.

**Status:** ~~TODO~~ Fixed for v0.8.3. Priority was high — shipped as a small recorder-side threshold hardening plus targeted unit coverage.

---

## ~~489. Ghosts freeze in mid-air when an incomplete ballistic flight ends on scene exit~~

**Source:** implementation plan + follow-through from `docs/dev/done/plans/incomplete-ballistic-extrapolation.md`.

**Concern:** if the player leaves flight while a vessel is still on an incomplete ballistic path, the saved recording ends at the last sampled frame and the ghost later freezes in place. Suborbital arcs, atmospheric descents, escape trajectories, and post-flyby coasts all stop at scene-exit UT instead of continuing to a natural endpoint.

**Fix / Resolution (2026-04-20):** shipped. Scene-exit finalization now snapshots the vessel's patched-conic coast chain, stores predicted segments in the existing `OrbitSegments` list with persisted `isPredicted` metadata, extrapolates incomplete ballistic tails through SOI handoffs until atmosphere / terrain / horizon termination, and commits the resulting terminal lifetime through `ExplicitEndUT` so existing playback, spawn-timing, watch-protection, and timeline consumers naturally honor the extended end. Runtime/map handling stays on the existing single-orbit renderer path in v1, while focused unit/integration coverage was added for persistence, snapshotting, extrapolation, and scene-exit finalization seams.

**Files:** `Source/Parsek/PatchedConicSnapshot.cs`, `Source/Parsek/BallisticExtrapolator.cs`, `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek/TrajectorySidecarBinary.cs`, plus the new focused tests in `Source/Parsek.Tests`.

**Status:** DONE/CLOSED (2026-04-20). Fixed for `0.8.3`.

---

## ~~466. `RecalculateAndPatch` runs mid-flight with an incomplete ledger, patches funds DOWN to the pre-milestone target and destroys in-progress earnings~~

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log:9993`.

```
9993: [WARN][KspStatePatcher] PatchFunds: suspicious drawdown delta=-36800.0 from current=57795.0 (>10% of pool, target=20995.0) — earning channel may be missing. HasSeed=True
9995: [INFO][KspStatePatcher] PatchFunds: 57795.0 -> 20995.0 (delta=-36800.0, target=20995.0)
```

Two more occurrences at `:12839` (-9300) and `:13581` (-41546.7) within the same 10-minute session, all with identical shape: the live KSP funds are higher than the ledger's computed target because stock KSP has credited milestones the Parsek ledger does not yet know about.

**Concern:** `KspStatePatcher.PatchFunds` logs the `suspicious drawdown` WARN (`KspStatePatcher.cs:160-167`) but deliberately still applies the drawdown — the comment at `:156-159` says "log-only (never aborts the patch) — but a >10% drop alongside a small pool (>1000F) is the shape of missing-earnings bugs". In this session that design is **destructive**: the recalc was triggered mid-launch by an OnLoad at 00:35:27 (just after revert subscribe on `:9957`), at which point the `r0` recording's tree had not yet committed (`Committed tree 'r0'` is at `:10083`, ~4s later). `actionsTotal=4` at that recalc — rollout + initial seed only, no milestones. So the ledger's target of `25000 - 4005 = 20995` ignores the `+800` `+4800` `+4800` `+4800` `+4800` milestone credits stock had already awarded, and `Funding.Instance.AddFunds(delta=-36800, TransactionReasons.None)` silently deletes 36,800F of the player's money.

A subsequent recalc at `:10134` with `actionsTotal=12` (post-commit) computes the full target, but by then the funds have been re-patched several times and the reconcile is in the broken state described in #469. The three drawdowns are not three separate events — they are three recalc passes, each landing before a different tree's commit.

**User-visible:** player earns milestones in flight (visible in game UI), then on scene transition / quickload the funds snap back to a lower value. This is the "ledger/resource recalculation did not really work correctly" the reporter is describing.

**Fix:** shipped. `LedgerOrchestrator.RecalculateAndPatch` now still walks the committed ledger but defers the KSP write-back step whenever there is a live recorder or pending tree and the walk is a normal non-cutoff pass. That keeps in-flight / pending-tree earnings live in KSP until the tree is either committed or discarded, while rewind-style cutoff walks remain authoritative. `MergeDialog.MergeDiscard` and the deferred merge-dialog idle-on-pad auto-discard path now trigger an immediate recalculation after the pending tree is removed so a discard still cleanly tears those live effects back out.

Add a cross-reference: `#439`, `#440`, `#448` and the already-archived post-#307 reconciliation work all touched adjacent logic. Re-read `done/todo-and-known-bugs-v3.md` entries for those before writing the fix — the reason several drawdown WARNs were *kept* log-only was a prior bug where aborting the patch masked a different class of problem. Don't regress that.

**Files:** `Source/Parsek/GameActions/KspStatePatcher.cs` (patch gate), `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`RecalculateAndPatch` entry check), `Source/Parsek/FlightRecorder.cs` or `ParsekFlight.cs` (uncommitted-tree predicate). Tests: xUnit for the gate (seed a mid-flight state, trigger recalc, assert no `Funding.AddFunds` call); integration-style log-assertion test covering the revert-mid-flight path.

**Scope:** Medium. Touches patch gating + recalc entry + a new predicate. Several test cases to cover revert/rewind/OnLoad/quickload interactions.

**Dependencies:** read the #307/#439/#440 history first. Fix should land before / alongside #469 since the reconcile warnings mostly disappear once the patch gate prevents the stale-target state from ever being written.

**Status:** Fixed in `0.8.3`. Priority was **critical** — now closed. The suspicious-drawdown WARN stays log-only for genuine rewind/reset paths, but the destructive "uncommitted tree patched back to committed-ledger funds" case is blocked at the orchestrator layer before `PatchFunds` runs.

---

## 465. ~~Ghost engine/RCS audio keeps playing while the KSP pause menu is open outside the flight scene~~

**Source:** user playtest report. "When paused (game menu open) in KSC view and probably other views, the sound from the rocket ghost is still audible."

**Concern:** ghost `AudioSource` components (engine loops, RCS, ambient clips) don't respond to KSP's global pause like stock vessels' audio does. In the flight scene this is already handled: `ParsekFlight.cs:657` subscribes to `GameEvents.onGamePause`/`onGameUnpause` and the handlers at `ParsekFlight.cs:4302-4315` delegate to `engine.PauseAllGhostAudio()` / `engine.UnpauseAllGhostAudio()`, which loop over active ghost states and call `AudioSource.Pause()`/`UnPause()` (see `GhostPlaybackLogic.cs:2358` for the helpers). KSC playback had no equivalent pause subscription, so ESC at KSC muted stock audio while ghost engine loops kept playing.

**Fix / Resolution (2026-04-19):** `ParsekKSC.cs` now subscribes to `GameEvents.onGamePause` / `onGameUnpause` in `Start`, unsubscribes in `OnDestroy`, and applies pause/unpause across its own `kscGhosts` + `kscOverlapGhosts` dictionaries via a shared helper that reuses `GhostPlaybackLogic.PauseAllAudio()` / `UnpauseAllAudio()`. The earlier "forward to the flight engine" idea was incorrect because KSC playback does not own a `GhostPlaybackEngine`. Tracking Station was checked separately: it publishes map/ProtoVessel ghosts only and does not instantiate `AudioSource`s, so no extra scene-side fix was needed there. Added xUnit coverage for the KSC audio-action helper and its logging/counting seam.

**Files:** `Source/Parsek/ParsekKSC.cs` (pause subscriptions + KSC-local audio-action helper), `Source/Parsek.Tests/KscGhostPlaybackTests.cs` (helper coverage). No Tracking Station code change required after verification.

**Scope:** Small. KSC-only fix plus unit coverage.

**Dependencies:** none.

**Status:** DONE/CLOSED (2026-04-19). Priority: medium — fixed in `fix-465-pause-menu-audio`.

---

## 464. ~~Timeline Details tab duplicates milestone / strategy entries — gray `GameStateEvent` line shadows the green `GameAction` reward line~~

**Source:** user playtest report. "From the Timeline Details tab list, remove the 'Milestone … achieved' messages and leave only the green ones, they're kind of duplicates; same for Strategy: activate / deactivate, duplicates."

**Concern:** for each milestone or strategy lifecycle event, the Timeline Details list renders two rows:

- the green `GameAction` row — rendered by `TimelineEntryDisplay.cs:296-308` for `GameActionType.MilestoneAchievement` and carries the user-meaningful data (milestone name + `+960 funds` / `+0.5 rep`). The strategy-activation variant is rendered in the same file for `GameActionType.StrategyActivate` / `StrategyDeactivate` (setup cost legs).
- the gray `GameStateEvent` row — rendered by `GameStateEvent.GetDisplayDescription` at `GameStateEvent.cs:398-399` (`"{key}" achieved`) and `:405-413` (`"{title}" activated` / `"{title}" deactivated`). These are emitted by the `GameStateRecorder` path for audit completeness but add no information beyond what the green GameAction row already shows.

Net effect: every milestone / strategy event shows up twice in the Timeline Details tab — first as the green reward summary, then as the plain gray confirmation. Players read this as redundant.

**Fix:** filter the duplicate `GameStateEventType.MilestoneAchieved`, `StrategyActivated`, and `StrategyDeactivated` rows out of the Timeline Details rendering when a matching green `GameActionType.MilestoneAchievement` / `StrategyActivate` / `StrategyDeactivate` already exists for the same UT + key. Two equally valid places to apply the filter:

1. In the timeline-details collator (wherever `GameStateEvent`s are merged into the per-recording display list — likely `ParsekUI` / `RecordingsTableUI` or a shared `TimelineBuilder` helper). Preferred — drops them at assembly time so the display path stays simple.
2. In `TimelineEntryDisplay` via a post-hoc "if a preceding entry for this UT already carries the milestone id / strategy title, skip this one" dedup. Works but leaks the dedup logic into the display layer.

Keep the gray rows emitted at the data layer — they're still useful for the raw event log / debugging. Only filter at the Timeline Details renderer level. Add a setting/toggle only if users actually want the duplicates back (unlikely given the report).

**Files:** `Source/Parsek/Timeline/TimelineEntryDisplay.cs` (or upstream of it — grep for whatever builds the Details list); `Source/Parsek/GameStateEvent.cs` only if the "achieved"/"activated" format strings themselves need to change (they don't for this bug — the fix is filtering, not rewording). Test: xUnit building a timeline with both a MilestoneAchievement GameAction and a matching MilestoneAchieved GameStateEvent at the same UT, asserts the rendered list contains exactly one row for that milestone (the green one).

**Scope:** Small. Single collator/filter site + one test. No schema or recording-format change.

**Dependencies:** none.

**Status:** ~~TODO~~ Fixed. `TimelineBuilder` now drops only duplicate legacy `MilestoneAchieved` / `StrategyActivated` / `StrategyDeactivated` rows when the timeline already contains the matching `GameAction` at the same UT + key, so the Details tab keeps the richer action row while leaving raw event capture untouched. Targeted verification: `Source/Parsek` + `Source/Parsek.Tests` build clean with `dotnet build --no-restore`; focused `TimelineBuilderTests` coverage was re-run in-process for `LegacyEvents_AppearAtT2`, `LegacyEvents_ResourceEventsFiltered`, and `LegacyMilestoneAndStrategyDuplicates_AreFilteredWhenMatchingGameActionsExist`.

---

## ~~463. Deferred-spawn flush skips FlagEvents — flags planted mid-recording never materialise when warp carries the active vessel past a non-watched recording's end~~

**Source:** user playtest `logs/2026-04-19_0014_investigate/KSP.log`. Reproducer:

1. Record an "Untitled Space Craft" flight; EVA Bob Kerman and plant a flag (`[Flight] Flag planted: 'a' by 'Bob Kerman'` at UT 17126).
2. Watch an unrelated recording (Learstar A1) and time-warp through the flag's UT.
3. At warp-end the capsule (#290) and kerbal (#291) materialise as real vessels via the deferred-spawn queue — but the flag 'a' does NOT spawn.
4. Stop watching Learstar; watch the actual Bob Kerman recording (#291) instead. Its ghost runs through UT 17126 normally, `[GhostVisual] Spawned flag vessel: 'a' by 'Bob Kerman'` fires, and the flag appears.

Specific log lines in the snapshot (all from the same session):

- `00:10:40.581 [Policy] Deferred spawn during warp: #291 "Bob Kerman"` — warp active, spawn queued.
- `00:10:57.316 [Policy] Deferred spawn executing: #291 "Bob Kerman" id=d631f348fde24b6f8fbeb00228d8e057` — warp ended, queue flushed; `host.SpawnVesselOrChainTipFromPolicy(rec, i)` runs and spawns the EVA vessel. Nothing touches `rec.FlagEvents`.
- `00:12:56.614 [GhostVisual] Spawned flag vessel: 'a' by 'Bob Kerman'` — only emitted when the actual Bob Kerman recording is watched (session 2), via the normal `GhostPlaybackLogic.ApplyFlagEvents` cursor path.

**Root cause:** flag vessel spawns are driven by `GhostPlaybackLogic.ApplyFlagEvents` (`Source/Parsek/GhostPlaybackLogic.cs:1892`), which walks `state.flagEventIndex` forward over `rec.FlagEvents` every frame a ghost is in range. Callers are `GhostPlaybackEngine.UpdateNonLoopingPlayback:744`, `ParsekKSC.cs:341/476/528`, and the preview path in `ParsekFlight.cs:8177`. The deferred-spawn-at-warp-end path in `ParsekPlaybackPolicy.ExecuteDeferredSpawns` (≈ `ParsekPlaybackPolicy.cs:143-179`) goes straight from `host.SpawnVesselOrChainTipFromPolicy(rec, i)` to `continue` without ever stepping the flag-event cursor, because the ghost for that recording never entered range (you were watching Learstar). Flags in the recording interval — which are "in the past" by the time deferred spawn runs — are silently dropped.

User-visible symptom: a flag planted during an EVA disappears from the world whenever the player time-warps past its recording while watching anything else. "Capsule and kerbal spawned but the flag didn't" is the exact report shape.

**Fix:** in `ParsekPlaybackPolicy.ExecuteDeferredSpawns`, after a successful `SpawnVesselOrChainTipFromPolicy` call, walk `rec.FlagEvents` and invoke `GhostVisualBuilder.SpawnFlagVessel(evt)` for every event with `evt.ut <= currentUT`, guarded by the existing `GhostPlaybackLogic.FlagExistsAtPosition` dedup. This mirrors the state-less fallback branch inside `ApplyFlagEvents` (`GhostPlaybackLogic.cs:1918-1924`) so no new invariant is added — the dedup helper already handles idempotent replays. Consider extracting a small `GhostPlaybackLogic.SpawnFlagVesselsForRecording(rec, currentUT)` helper so both paths share one implementation. Log a `[Verbose][Policy] Deferred flag flush: #N "rec" spawned K/N flags` summary so the fix is observable in playtest logs.

**Also verify during fix:** earlier in the same session, `00:09:08.031 [Scenario] Stripping future vessel 'a' (pid=1009931614, sit=LANDED) — not in quicksave whitelist` fires from `ParsekScenario.StripFuturePrelaunchVessels`. This is the rewind/quickload strip path (`Source/Parsek/ParsekScenario.cs:1490`). Confirm that flags planted during a committed recording are NOT treated as future-prelaunch vessels on quicksave round-trip — the whitelist-based strip predates flag support, so a fresh look at whether the planted-flag PID should be added to the whitelist (or filtered by type) would close a related observation. If a quickload can strip the flag before the deferred-spawn replay even runs, the main fix above does not cover that path.

**Files:** `Source/Parsek/ParsekPlaybackPolicy.cs` (deferred spawn flush + policy log), `Source/Parsek/GhostPlaybackLogic.cs` (shared flag replay helper), `Source/Parsek.Tests/DeferredSpawnTests.cs` (helper + policy-path regressions). Verification in this environment used `dotnet test --no-restore` for compile/build plus a direct reflection harness over the compiled `DeferredSpawnTests` methods because the standard `dotnet test` runner aborts here on local socket initialization (`SocketException 10106` before test execution starts).

**Scope:** Small-to-medium. Core fix is a 5-10 line loop in one method + one helper + one unit test. Strip-path verification is separate and may be a no-op if flags are already on the whitelist.

**Dependencies:** none (flag event capture + `SpawnFlagVessel` both already work).

**Resolution:** fixed in branch `fix-463-flagevents-deferred-spawn` on 2026-04-19. Deferred warp-end spawn flushes now call a shared `GhostPlaybackLogic.SpawnFlagVesselsUpToUT(...)` helper immediately after the real vessel/chain-tip spawn, reusing the existing dedup check and emitting a `[Verbose][Policy] Deferred flag flush ... spawned K/N flag(s)` line. The quickload-strip observation was reviewed separately: `StripFuturePrelaunchVessels` already strips any vessel PID that was not present in the rewind quicksave, so this patch intentionally leaves `ParsekScenario` unchanged; the main missing behaviour was the post-spawn replay of already-due flag events.

**Status:** CLOSED 2026-04-19. Priority was medium-high — fixed for v0.8.3.

---

## 462. ~~LedgerOrchestrator earnings reconciliation: MilestoneAchievement double-count vs FundsChanged~~

**Source:** `logs/2026-04-19_0014_investigate/KSP.log` (48 WARN lines across one session). Representative pair:

```
[WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, funds): MilestoneAchievement id=Kerbin/SurfaceEVA expected=960.0, observed=1440.0 across 2 event(s) keyed 'Progression' at ut=17110.6 -- post-walk delta mismatch
[WARN][LedgerOrchestrator] Earnings reconciliation (funds): store delta=13920.0 vs ledger emitted delta=13440.0 — missing earning channel? window=[17076.5,17110.6]
```

**Concern:** post-walk funds reconciliation detects a systematic mismatch between the expected milestone award and the observed FundsChanged events for several stock milestones — `MilestoneAchievement id=` values hitting 1.5× the expected payout across 2 events (so every recalc is double-writing one of them), plus store-vs-ledger window mismatches where the full-window delta diverges by a stable offset. Seen for: `RecordsSpeed` (12×), `RecordsDistance` (12×), `Kerbin/SurfaceEVA` (6+3), `Kerbin/Landing` (6×), `Kerbin/FlagPlant` (6×), `FirstLaunch` (6×). All on the same test-career session at UT≈17110. Because the observed delta is higher than expected, funds accounting for these milestones is likely over-paying — the kind of bug that silently inflates funds over long play sessions and is very hard to spot without the reconciliation WARNs.

**Fix:** investigate `LedgerOrchestrator.RecalculateAndPatch` + `GameActions/KerbalsModule`-style earnings paths for milestone events. Two plausible causes: (1) milestone event being emitted twice into the ledger (once from the live progress event, once during recalc replay); (2) `Progression` channel key matching two distinct events in the reconciliation window (ambient FundsChanged from another source collapsed in). Add a test generator that reproduces the double-count for `RecordsSpeed` in `Source/Parsek.Tests/` (the milestone most obviously reproducible — it fires on every takeoff/landing in the test save). Cross-reference with the existing PR #307 follow-ups in `done/todo-and-known-bugs-v3.md` — that bundle already touched the `Progression` dedup key.

**Files:** likely `Source/Parsek/GameActions/LedgerOrchestrator.cs`, the earnings emit path for MilestoneAchievement, and whichever module owns MilestoneStore→FundsChanged conversion. Log snapshot saved under `logs/2026-04-19_0014_investigate/` for reproduction context.

**Scope:** Medium. Funds reconciliation is safety-critical (double-counted earnings invalidate career economies), but the WARN mechanism is already catching it — so the fix is localised to one emit path, not a schema redesign.

**Dependencies:** none.

**Status:** CLOSED. The apparent `MilestoneAchievement` "double-count" shape was the same post-walk attribution bug family as #477, not duplicate milestone credit landing in the ledger. The main fix shipped in #477; a follow-up hardening pass now also pins the mixed null-tagged/tagged ordering edge so legacy siblings cannot reclaim ownership of a tagged `Progression` burst just by appearing earlier in the ledger.

**Update (superseded by #477):** re-investigation in `logs/2026-04-19_0117_thorough-check/` showed the 2× / 3× / spurious-sci pattern is general across every milestone, not specific to `Kerbin/SurfaceEVA`. Final fix: not duplicate `MilestoneAchievement` action emission, but per-action attribution of a coalesced post-walk reward window. `ReconcilePostWalk` now compares/logs once per window and reports grouped ids, which closes the `Kerbin/SurfaceEVA` / `Records*` false-positive shape as well.

**Update (PR #405):** partial fix shipped — cross-recording `Progression` (and other keyed) events are now filtered out of both `ReconcileEarningsWindow` (commit path) and `CompareLeg` / `AggregatePostWalkWindow` (post-walk) by `recordingId`. That closed the original "2 events keyed 'Progression'" sibling-recording shape, but not the broader same-window attribution bug that #477 later fixed.

**Update (2026-04-19 follow-up):** `AggregatePostWalkWindow` now prefers tagged recording-scoped actions over null-tagged legacy siblings when choosing the primary owner of a mixed-scope window. That makes the `#405` partial fix order-independent: a null-tagged legacy row can still match tagged store events when it is alone, but it can no longer re-aggregate a tagged sibling's `Progression` delta if the ledger happens to enumerate the legacy row first. Added xUnit coverage for the `Kerbin/SurfaceEVA`-style mixed-scope repro.

---

## ~~461. Pin the #406 reuse post-frame visibility invariant with an in-game test~~

**Source:** clean-context Opus review of PR #394 (#406 ghost GameObject reuse across loop-cycle boundaries), finding #4.

**Concern:** the reuse orchestrator (`GhostPlaybackEngine.ReusePrimaryGhostAcrossCycle`) exits with `state.deferVisibilityUntilPlaybackSync == true` and `state.ghost.activeSelf == false` (set by `PrimeLoadedGhostForPlaybackUT.SetActive(false)`). Control then falls through to `UpdateLoopingPlayback:1161-1166`, where `ActivateGhostVisualsIfNeeded` clears both on the same frame before any render pass. A post-investigation trace confirmed this is invariant-equivalent to the pre-#406 destroy+spawn path, so no visual regression exists today — but NO test pins this control-flow ordering. A future refactor that adds an early `return` between `:1068` (the reuse call) and `:1166` (the activation) would silently hide the ghost for a frame on every cycle boundary.

**Fix:** shipped in `Source/Parsek/InGameTests/RuntimeTests.cs` as `Bug406_ReuseClearsDeferVisOnSameFrame` plus `Bug406_ReuseHiddenByZone_DoesNotActivateGhostOnSameFrame`. The coverage now drives the real `UpdatePlayback -> UpdateLoopingPlayback` cycle-boundary path through the live FLIGHT positioner, asserts that the same ghost GameObject instance survives the full frame, and pins the two post-frame outcomes that matter: visible branch clears `deferVisibilityUntilPlaybackSync` and re-activates the reused ghost on the same frame; hidden-by-zone branch keeps the reused ghost deferred/inactive until a later visible frame. It also asserts that zone rendering does NOT emit the `re-shown: entered visible distance tier` path while the ghost is still deferred, so the same-frame activation remains owned by `ActivateGhostVisualsIfNeeded`. xUnit still cannot observe `GameObject.activeSelf`, so this remains in-game-only coverage.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`.

**Scope:** Small. Regression-test coverage only; no production behaviour change was required after the invariant re-check.

**Dependencies:** #394 (#406 follow-up) merged.

**Status:** CLOSED. Priority: low. Landed on 2026-04-19 with the two runtime regressions above. User-visible impact remains none today; this is a guard against future refactors that would insert an early return between reuse and same-frame activation.

---

## ~~450. Per-spawn time budgeting / coroutine split — #414 follow-up for bimodal single-spawn cost~~

**Resolution (2026-04-19):** CLOSED. Phase B2 shipped for v0.8.3. `GhostVisualBuilder.BuildTimelineGhostFromSnapshot` now advances the expensive snapshot-part instantiation loop across multiple `UpdatePlayback` ticks via persisted `PendingGhostVisualBuild` state instead of monopolizing one frame. `GhostPlaybackEngine` gives each pending ghost a bounded per-frame timeline budget, preserves the correct first-spawn / loop / overlap lifecycle event until the build actually completes, and still forces immediate completion for explicit watch-mode loads. Unload / destroy paths now clear pending split-build state, and the overlap-primary path no longer allows hidden-tier prewarm to consume a second advance in the same frame. Coverage added: xUnit guard for pending-state cleanup plus an in-game regression that the incremental builder yields mid-build, resumes, and completes cleanly.

**Source:** smoke-test bundle `logs/2026-04-18_0221_v0.8.2-smoke/KSP.log:11489`. One-shot #414 breakdown line (first exceeded frame in the session):

```
Playback budget breakdown (one-shot, first exceeded frame):
total=40.1ms mainLoop=11.34ms
spawn=28.11ms (built=1 throttled=0 max=28.11ms)
destroy=0.00ms explosionCleanup=0.00ms deferredCreated=0.24ms (1 evts)
deferredCompleted=0.00ms observabilityCapture=0.43ms
trajectories=1 ghosts=0 warp=1x
```

**Concern:** #414's fix caps ghost spawns per frame at 2 via `GhostPlaybackEngine.MaxSpawnsPerFrame`, but this frame built exactly 1 ghost and that single spawn cost 28.11 ms — throttled=0, max=28.11 ms. This is the **bimodal cost distribution** #414 explicitly flagged as requiring a follow-up: "if max > ~10 ms we have a bimodal cost distribution that a count cap alone cannot cover, in which case the follow-up is per-spawn time budgeting or a coroutine split" (see #414 **Fix** section). The smoke test confirms the bimodal case is real on this save.

Breakdown of the exceeded frame: 70% of the budget (28.11 / 40.1 ms) lived inside a single `SpawnGhost` invocation. Candidates for the dominant per-spawn cost:
- `GhostVisualBuilder.BuildGhostVisuals` — part instantiation + engine FX size-boost pass (PR #316) + reentry material pre-warm.
- `PartLoader.getPartInfoByName` resolution for every unique part name in the ghost snapshot (cold PartLoader cache on first spawn of a given vessel type).
- Ghost rigidbody freeze + collider disable walk (`GhostVisualBuilder.ConfigureGhostPart`).

`mainLoop=11.34 ms` with `trajectories=1 ghosts=0` is also on the high side (expected ≤1 ms per trajectory on an established session), worth subtracting from the spawn cost attribution when a follow-up breakdown lands.

**Phase A (shipped):** diagnostic first. `PlaybackBudgetPhases` now carries an aggregate-and-heaviest-spawn breakdown of every `BuildGhostVisualsWithMetrics` call across four sub-phases (snapshot resolve, timeline-from-snapshot, dictionaries, reentry FX) plus a residual "other" bucket so `sum + other = spawnMax` reconciles. See `docs/dev/done/plan-450-build-breakdown.md`.

**Phase B branch decision — data from the 2026-04-18 playtest:**

```
heaviestSpawn[type=recording-start-snapshot
              snapshot=0.00ms timeline=15.90ms dicts=1.28ms reentry=6.94ms
              other=0.08ms total=24.20ms]
```

Timeline dominates (65.7 %) and reentry is a significant secondary contributor (28.7 %). Both B2 and B3 apply; B3 ships first (smaller blast radius), then B2 takes on the remaining `timeline` cost.

**Phase B3 (shipped):** lazy reentry FX pre-warm. Defers `GhostVisualBuilder.TryBuildReentryFx` from spawn time to the first frame the ghost is actually inside a body's atmosphere. `MaxLazyReentryBuildsPerFrame = 2` per-frame cap mirrors `MaxSpawnsPerFrame`. See `docs/dev/done/plan-450-b3-lazy-reentry.md`.

**Phase B2 (shipped):** coroutine split of `BuildTimelineGhostFromSnapshot`. Targets the dominant 15.90 ms timeline bucket that remained after B3. The snapshot-part loop now resumes from a persisted `PendingGhostVisualBuild` on later frames, with the dominant timeline work budgeted per advance instead of landing as one 15-18 ms single-spawn spike. Direct watch-mode loads still bypass the budget and complete synchronously on demand.

**Phase B1 (not planned):** the 15 ms latch threshold means #450's diagnostic only fires on bimodal cases, so the "spread across many spawns" case B1 targets is structurally out of scope of the evidence. #414's count cap already covers that pattern.

**Scope:** Phase B2 shipped as Medium (coroutine split, new invariants).

**Dependencies:** #414 shipped, Phase A shipped, Phase B3 shipped.

**Status:** CLOSED. Phase A, Phase B3, and Phase B2 shipped for v0.8.3. Priority was medium. The remaining bimodal single-spawn timeline cost is now spread across multiple frames instead of monopolizing one `UpdatePlayback` tick.

**Follow-up note:** B2 intentionally changes the diagnostics shape: the old `spawnMax >= 15 ms` one-shot WARN now sees several smaller per-advance samples instead of one large single-spawn spike. Re-validate that gate on the next post-B2 playtest so future heavy snapshot builds do not disappear from the WARN signal purely because the work is now chunked.

---

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
