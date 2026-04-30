# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## Done — v0.9.1 Phase 9 structural-event snapshots

- ~~Phase 9: `[Flags] enum TrajectoryPointFlags : byte` with `None = 0`, `StructuralEventSnapshot = 1 << 0`, bits 1-7 reserved.~~ New file `Source/Parsek/TrajectoryPointFlags.cs` documents the bit assignments and the additive-bit contract. `TrajectoryPoint.flags` byte added at the END of the struct so the struct's value-type default initialization keeps every legacy point at `flags = 0` (the §15.17 fall-through sentinel).
- ~~Phase 9: format constants — `RecordingStore.StructuralEventFlagFormatVersion = 10` and `CurrentRecordingFormatVersion` bumped from 9 to 10.~~ Public RecordingStore constant + version comment block updated; `TrajectorySidecarBinary.StructuralEventFlagBinaryVersion` mirrors the public constant; the binary version-selection ladder now picks v10 for new recordings and v9 for `RecordingFormatVersion < 10` (legacy save support). Regression tests `CurrentRecordingFormatVersion_Is10` and `StructuralEventFlagFormatVersion_Is10` plus the cross-codec sync guard `StructuralEventFlag_FormatVersion_MatchesBinaryVersion` pin the constants.
- ~~Phase 9: `TrajectorySidecarBinary` write/read path gates the per-point `flags` byte on `binaryVersion >= StructuralEventFlagBinaryVersion`.~~ `WritePoint` / `ReadPoint` and the sparse-list paths (`WriteSparsePointList` / `ReadSparsePointList`) thread `binaryVersion` through and append the byte at the END of every per-point record (after the v9 `recordedGroundClearance` double) so legacy v9 readers (which stop after that double) keep their stream alignment. `IsSupportedBinaryVersion` extended to accept v9 alongside v10. Legacy reads default `flags = 0` (HR-9 silent fall-through to interpolated event ε per §15.17). No parallel text-codec changes — `.prec.txt` is debug-only; the text codec's value-type `new TrajectoryPoint` default leaves `flags = 0`.
- ~~Phase 9: `FlightRecorder.AppendStructuralEventSnapshot(double eventUT, IEnumerable<Vessel> involved, string eventType)` helper.~~ Uses the caller-provided structural-event UT as the shared physics-clock timestamp, iterates `involved`, samples live position/velocity per matching vessel, and dedups by `RecordingVesselId` so only the tracked vessel's snapshot is committed. Uses `BuildStructuralEventSnapshot(v, velocity, eventUT)` (which routes through the existing `BuildTrajectoryPoint` + the new pure-static `ApplyStructuralEventFlag` seam so xUnit can pin the bit-set semantics without a live Vessel) and lands the point through the standard `CommitRecordedPoint` path so SurfaceMobile clearance + RELATIVE-frame offset + dual-write to flat Points + section frames all behave identically to a regular tick sample. No-op when `IsRecording == false` or when the active recording's format version is below v10 (legacy recordings keep their interpolation path per §15.17). Per-snapshot `Pipeline-Smoothing` Verbose log line includes UT / vesselId / eventType / flags / lat-lon-alt / relativeApplied.
- ~~Phase 9: event-handler wiring — `FlightRecorder.OnPartJointBreak`, `ParsekFlight.OnPartCouple`, `ParsekFlight.OnPartUndock`, `ParsekFlight.OnCrewOnEva`.~~ All four call `AppendStructuralEventSnapshot` BEFORE `StopRecordingForChainBoundary` (so the snapshot lands in the active section) with the event's involved vessels and a string label (`"Dock"` / `"Undock"` / `"EVA"` / `"JointBreak"`). `OnPartUndock` walks `FlightGlobals.Vessels` to add the pre-undock parent vessel symmetrically; `OnPartJointBreak` passes both `joint.Child.vessel` and `joint.Host.vessel` (typically the same Vessel pre-split — the dedup-by-`RecordingVesselId` filter keeps the snapshot unambiguous).
- ~~Phase 9: `AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(IList<TrajectoryPoint> frames, double ut, double tolerance)` helper.~~ Pure static O(N) scan that returns the index of the closest StructuralEventSnapshot-flagged sample within tolerance, or `-1` on miss. Sorted-list early-out (`d > tolerance` short-circuits the scan). Negative tolerance clamps to 0. Future-bit composition: tests pin that bit-1+ alone does NOT match, but bit-0 alongside any future bit DOES match.
- ~~Phase 9: `RenderSessionState.TryEvaluatePerSegmentWorldPositions` and `ProductionAnchorWorldFrameResolver.TryFindBoundaryFrameSample` consume the helper.~~ Both call `TryFindFlaggedSampleAtUT` first with a 1.0s tolerance and only fall through to today's nearest-neighbour / first-at-or-after path when no flagged sample exists. `RenderSessionState` scopes the flagged lookup to the current `TrackSection.frames` so a same-UT flagged sample from an adjacent RELATIVE section cannot be interpreted as ABSOLUTE lat/lon/alt; the legacy fallback still uses the flat list to preserve pre-v10 behaviour. Per-call `Pipeline-Smoothing` Verbose line carries `flagged=true|false`, `sampleUT`, and the `delta = sampleUT - candidateUT` so anchor-side ε regressions surface in telemetry without hooking into the propagator.
- ~~Phase 9: `RecordingBuilder.WithStructuralEventSnapshot` test generator.~~ Mirrors `AddPoint`'s ConfigNode shape plus a `flags` value the binary codec round-trips at v10. Test fixtures using this method default to `RecordingStore.CurrentRecordingFormatVersion` (now v10) so the binary writer emits the byte.
- ~~Phase 9: xUnit coverage in `Source/Parsek.Tests/`.~~ `Rendering/TrajectoryPointFlagsTests.cs` (6 tests) covers the bit assignments + idempotence + future-bit composition + value-type default `flags = 0`. `Rendering/TrajectorySidecarBinaryStructuralEventTests.cs` (4 tests) covers v10 round-trip with flag set, v10 round-trip with flag clear, v9-legacy-load defaults `flags = 0` AND preserves every other field including the v9 clearance double (positional sanity guard), v10 mixed flagged + non-flagged round-trip via the section-authoritative path. `Rendering/AnchorCandidateBuilderStructuralEventTests.cs` (11 tests) covers exact match, within tolerance, no flagged samples → `-1`, outside tolerance → `-1`, multiple flagged samples → closest wins, unflagged-closest-vs-flagged-farther → flagged wins (the critical Phase 9 contract), null/empty/negative-tolerance defensive cases, and the future-bit composition contract. `FlightRecorderStructuralEventTests.cs` (5 tests) covers `ApplyStructuralEventFlag`'s bit-set, idempotence, future-bit preservation, all-other-fields preservation, end-to-end binary round-trip via the helper-built point, and the §12 same-physics-clock contract via twin point construction. Plus 1 new format-version constant test (`StructuralEventFlagFormatVersion_Is10`) and 1 cross-codec sync guard (`StructuralEventFlag_FormatVersion_MatchesBinaryVersion`).
- ~~Phase 9: in-game test `Pipeline_Smoothing_StructuralEvent_FlagSampleAlignedToBranchPointUT`.~~ `[InGameTest(Category = "Pipeline-Smoothing", Scene = GameScenes.FLIGHT)]` in `RuntimeTests.cs`; builds a synthetic 3-sample section with a flagged point at a known dock UT bracketed by unflagged ticks, writes it via `TrajectorySidecarBinary` to a temp `.prec`, reads it back via `TryProbe` + `Read`, and asserts the round-tripped section preserves the flag at exactly the dock UT — then feeds the restored frames to `AnchorCandidateBuilder.TryFindFlaggedSampleAtUT` to pin the anchor-side consumer behaviour end-to-end.

### Done — Phase 9 review pass (P2 + P3-1 + P3-2)

- ~~P2-1: Live KSP event-handler wiring had no automated coverage.~~ The four Phase 9 wired call sites (`FlightRecorder.OnPartJointBreak`, `ParsekFlight.OnPartCouple` / `OnPartUndock` / `OnCrewOnEva`) only ran in-engine and could regress silently if a future refactor dropped a `GameEvents.X.Add(...)` call. New in-game test `Pipeline_Smoothing_StructuralEvent_HandlersRegistered` walks each `EventData<T>.events` list via reflection, unwraps each `EvtDelegate.originalDelegate`, and asserts at least one delegate per event resolves to `FlightRecorder` or `ParsekFlight`. Because `FlightRecorder.OnPartJointBreak` is subscribed only while recorder part events are active, the test installs and removes one transient recorder subscription around the assertion so idle FLIGHT scenes remain deterministic. The reflection target (`EventData<T>.events` field name) is resolved per closed `EventData<T>` type so `onPartCouple`, `onPartUndock`, `onCrewOnEva`, and `onPartJointBreak` do not reuse an incompatible `FieldInfo`; the test gracefully skips with a self-documenting message if KSP renames the field across versions.
- ~~P2-2: Schema-gate fall-through branch in `AppendStructuralEventSnapshot` was untested.~~ Extracted the "format >= v10" gate into a pure-static `internal static bool ShouldEmitStructuralEventSnapshot(int activeFormatVersion)` that xUnit can pin without a live runtime. Three new xUnit cases plus one log-capture integration test cover the contract: `_LegacyFormatV9_ReturnsFalse`, `_V10AndAbove_ReturnsTrue`, `_BelowV9_StillReturnsFalse` (loops every legacy version), and `AppendStructuralEventSnapshot_LegacyV9Recording_NoAppendAndEmitsSkipLog` (constructs a real `FlightRecorder` with a v9 ActiveTree, asserts the recording stays untouched AND the "skipped: recording format v9 < v10" Verbose log line is emitted for HR-9 visibility).
- ~~P2-3: `ParsekFlight.OnPartCouple` snapshot fired before the `pendingSplitInProgress` early-return.~~ A couple event arriving while a split is mid-pipeline was emitting a flagged sample into a recording whose section was about to be torn down by the split-finishing path — phantom flagged points in the merged result. Fix: gate the snapshot append on `!pendingSplitInProgress` so it mirrors the existing tree-mode early-return guard. Inline comment justifies the ordering.
- ~~P2-4: `RenderSessionState.TryEvaluatePerSegmentWorldPositions` used the flat `Recording.Points` list for the flagged-sample fast path.~~ A same-UT flagged sample from an adjacent RELATIVE section could outrank the current ABSOLUTE section's exact unflagged boundary sample, causing anchor-local metre offsets to be read as body-fixed degrees. Fix: search only `section.frames` for the Phase 9 flagged fast path, then fall through to the legacy flat-list first-at-or-after lookup on miss. New xUnit regression `TryEvaluatePerSegmentWorldPositions_FlaggedLookupIsScopedToSectionFrames` pins this boundary case.
- ~~P3-1: Docstring acknowledgment for the "every involved vessel" reduction.~~ Added a paragraph to `AppendStructuralEventSnapshot`'s XML doc explaining that §12's "snapshot for every involved vessel" reduces to "this recorder's vessel" by construction in Parsek's architecture (each `FlightRecorder` tracks one focused vessel; peer coverage is a per-recorder concern). Points at the BackgroundRecorder follow-up below for the deferred peer integration.

### Phase 9 follow-ups

- BackgroundRecorder integration for proximity-tracked peers in dock / undock / EVA / joint-break events. Today `AppendStructuralEventSnapshot` lives on `FlightRecorder` and dedups by `RecordingVesselId`, so a peer vessel that's being proximity-recorded (its own `BackgroundRecorder` instance) gets no flagged snapshot at the structural event UT. The §12 invariant for the focused-vessel side is satisfied; the peer side falls through to the legacy interpolated event ε. Fix shape: mirror the helper into `BackgroundRecorder` and wire each event handler to call both recorders' helpers with the same `eventUT` so the structural-event timestamp stays aligned across recorders while each recorder samples its own vessel state. Defer until v0.9.2 — the peer-side gap surfaces only when both halves of a dock/undock pair are independently recorded, which is uncommon in practice.
- `TrajectoryTextSidecarCodec.cs` mirror parsing of the new `flags` byte. The text codec is the debug-only mirror (`.prec.txt`); today its deserializer uses a value-type `new TrajectoryPoint` initializer that leaves `flags = 0`, which is the legacy fall-through sentinel. A debug-only round-trip through the text codec silently strips the StructuralEventSnapshot bit. Fix shape: serialize `pt.flags` as a byte value in `SerializePointValues` and parse it back in `DeserializePoint` (parallel to the existing `recordedGroundClearance` text-codec gap, which is also unparsed today). Defer until someone actually exercises the text codec round-trip — it's debug-only and the binary codec is canonical.

---

## Done — v0.9.1 Phase 7 continuous terrain correction

- ~~Phase 7: per-point `recordedGroundClearance` (double, NaN sentinel for legacy / non-SurfaceMobile points) added to `TrajectoryPoint` (`Source/Parsek/TrajectoryPoint.cs`).~~ Default-NaN initializers wired through every production-side `new TrajectoryPoint` site (`BuildTrajectoryPoint`, `BackgroundRecorder.CreateTrajectoryPointFromVessel*`, `ChainSegmentManager` continuation seed + EVA continuation, `OrbitalCheckpointDensifier`, `ParsekFlight.PositionAtSurface` synthetic), through every interpolation site that lerps adjacent points (`SessionMerger.TryAppendBoundarySeam`, `SpawnCollisionDetector.InterpolateTrajectoryPoint`, `RecordingOptimizer` boundary point + general lerp helper, `SmoothingPipeline.LiftToInertial`), and the text codec deserializer.
- ~~Phase 7: format constants — `RecordingStore.TerrainGroundClearanceFormatVersion = 9` and `CurrentRecordingFormatVersion` bumped from 8 to 9.~~ Public RecordingStore constant + version comment block updated; `TrajectorySidecarBinary.TerrainGroundClearanceBinaryVersion` mirrors the public constant; the binary version-selection ladder now picks v9 for new recordings and v8 for `RecordingFormatVersion < 9` (legacy save support). New regression tests `CurrentRecordingFormatVersion_Is9` and `TerrainGroundClearanceFormatVersion_Is9` pin the constants; the existing `TrackSection_BoundarySeamFlag_RoundTripsThroughBinaryCodec` test is now pinned to `BoundarySeamFlagFormatVersion` explicitly so it survives future bumps.
- ~~Phase 7: `TrajectorySidecarBinary` write/read path gates the per-point `recordedGroundClearance` double on `binaryVersion >= TerrainGroundClearanceBinaryVersion`.~~ `WritePoint` / `ReadPoint` and the sparse-list paths (`WriteSparsePointList` / `ReadSparsePointList`) now thread `binaryVersion` through and append the double at the END of every per-point record so legacy v8 readers (which stop after the reputation field) keep their stream alignment. `IsSupportedBinaryVersion` extended to accept v8 alongside v9 for explicit downgrade-path support. Legacy reads default `recordedGroundClearance = NaN` (HR-9 silent fall-through to legacy altitude path).
- ~~Phase 7: `FlightRecorder.CommitRecordedPoint` populates `recordedGroundClearance = altitude - terrainHeight` for every sample whose section has `environment == SurfaceMobile` and `referenceFrame == Absolute`.~~ Computed via `body.TerrainAltitude(lat, lon, true)` (the same PQS-only call used by `VesselSpawner.ClampAltitudeForLanded`). Per-section accumulator fields (`surfaceMobileSamplesThisSection`, min/max/sum) reset in `StartNewTrackSection` and emit one `Pipeline-Terrain` Verbose line at `CloseCurrentTrackSection` with min/max/avg/N. Non-SurfaceMobile sections / Relative frame / missing PQS keep clearance NaN, so playback falls through to the legacy altitude path.
- ~~Phase 7: `Source/Parsek/Rendering/TerrainCacheBuckets.cs` — lat/lon-bucketed render-time cache.~~ 0.001° × 0.001° buckets (~111 m on Kerbin), per-`(body, latBucket, lonBucket)` keyed, no LRU within a scene. Cap at 100k entries — past the cap the cache stops growing and emits a one-shot Warn (the diagnostic IS the diagnostic). Production resolver routes through `body.TerrainAltitude(lat, lon, true)`; test seam `TerrainResolverForTesting` lets unit tests exercise the cache without a live PQS controller. A second test seam `FrameCountResolverForTesting` is required because vanilla CLR (xUnit) cannot bind `UnityEngine.Time.frameCount` (it's an ECall and the SecurityException fires at JIT bind time, not at call time — the production native path is isolated in a separate `NativeUnityFrame` method). Per-frame summary emits via `VerboseRateLimited("Pipeline-Terrain", "terrain-cache-frame-summary", ..., 2.0)`.
- ~~Phase 7: scene-transition cache clear hook in `ParsekFlight.OnSceneChangeRequested`.~~ Calls `TerrainCacheBuckets.Clear()` at the top of the scene-change handler; ensures stale bucket values from the previous scene's body don't leak into the next scene's terrain queries. Diagnostic Verbose line on every clear lists the pre-clear bucket count.
- ~~Phase 7: render-time effective-altitude helper `ParsekFlight.ResolvePhase7EffectiveAltitude(body, lat, lon, recordedAltitude, recordedGroundClearance)`.~~ Pure static (testable without KSP): returns `recordedAltitude` when clearance is NaN (legacy fall-through), null body, or terrain cache returns NaN (PQS unspun); returns `cachedTerrain + clearance` otherwise. Wired into both `PositionGhostAt` (used by `IGhostPositioner.PositionAtPoint`) and `InterpolateAndPosition` (which now resolves both before/after altitudes through the helper before the world-space lerp). Cache miss + PQS-NaN path emits a rate-limited Verbose under the `effective-altitude-pqs-miss` key.
- ~~Phase 7: xUnit coverage in `Source/Parsek.Tests/Rendering/`.~~ `TerrainCacheBucketsTests.cs` (17 tests) covers hit/miss correctness, bucket-key contract (positive/negative lat handling, adjacency), scene-transition clear, body-name distinguishes buckets, NaN propagation, cap-warn bounds. `TrajectorySidecarBinaryTerrainTests.cs` (4 tests) covers v9 round-trip with finite clearance, v9 round-trip with NaN clearance, v8-legacy-load defaults clearance to NaN AND preserves every other field (positional sanity guard), v9 multi-section fixture preserves per-frame clearance through TrackSection round-trip. `FlightRecorderTerrainTests.cs` (7 tests) covers the render-time helper formula (NaN ⇒ recorded altitude, finite ⇒ terrain + clearance, cross-session terrain shift preserves visual clearance, resolver-NaN fall-through with log assertion, null body short-circuit, theory-driven clearance computation symmetry, equator + pole resolution).
- ~~Phase 7: in-game test `Pipeline_Terrain_RoverClearance_StaysConstant`.~~ `[InGameTest(Category = "Pipeline-Terrain", Scene = GameScenes.FLIGHT)]` in `RuntimeTests.cs`; builds an 8-sample synthetic SurfaceMobile path crossing the KSC area, sets `altitude = current_pqs_terrain + 1.5m` and `recordedGroundClearance = 1.5m` for each sample, then asks the render-time helper for effective altitude at each sample and asserts `(rendered - terrain_at_lat_lon) == 1.5m ± 0.01m`. Skips with `InGameAssert.Skip` if Kerbin's PQS isn't spun up yet (transient scene-transition state). Also exercises the NaN fall-through path with a synthetic legacy point.

### Done — Phase 7 review pass (P2 + P3-3 + P3-4)

- ~~P2-1: `ResolvePhase7EffectiveAltitude` now takes a `ReferenceFrame` parameter and short-circuits to `recordedAltitude` whenever the section is non-Absolute.~~ Today's recorder never writes a finite clearance on a Relative-frame point, but the contract was fragile: a future codec / optimizer / merge that surfaced a finite clearance on a `ReferenceFrame.Relative` point would interpret metre-scale anchor-local lat/lon as degrees and project deep inside the planet (CLAUDE.md "Rotation / world frame" notes). Defensive gate at the renderer pinned by two new xUnit regressions (Relative + OrbitalCheckpoint frames with finite clearance ⇒ recorded altitude unchanged, resolver not called). Both production callers (`PositionGhostAt`, `InterpolateAndPosition`) hard-code `ReferenceFrame.Absolute` since RELATIVE goes through `InterpolateAndPositionRelative` and OrbitalCheckpoint goes through `TryInterpolateAndPositionCheckpointSection`.
- ~~P2-2: end-to-end legacy v8 → renderer fall-through coverage.~~ New `V8LegacyRead_EveryRestoredPoint_RoutesThroughRendererToRecordedAltitude` xUnit test in `TrajectorySidecarBinaryTerrainTests.cs` writes a v8 binary file, reads it back, and routes every restored point through `ParsekFlight.ResolvePhase7EffectiveAltitude` — asserts the helper returns the recorded altitude unchanged for every point AND that the test resolver was never called (proves the NaN fall-through never spuriously hits the resolver). Catches a future refactor that wires the helper to the wrong altitude (e.g. stores `effectiveAltitude` back into `point.altitude`).
- ~~P2-3: cache body-boundary test now verifies the round-trip.~~ Replaced `GetCachedSurfaceHeight_BodyNameDistinguishesBuckets` with `_BodyA_BodyB_BodyA_BodyACachedValueUnchanged_NoSecondResolverCallForA`: populates Kerbin at lat/lon X, queries Mun at same lat/lon, re-queries Kerbin at lat/lon X, asserts Kerbin's cached value is unchanged AND the resolver was NOT called a second time for Kerbin. Catches a hash-key collision regression where dictionary overwrite semantics would silently replace one body's terrain with another's.
- ~~P2-4: cap-warn test actually exercises the cap.~~ New `CapForTesting` test seam (mirrors `TerrainResolverForTesting`) lets unit tests override `MaxCachedBuckets` to a small value (3 in the new test). The test inserts 5 distinct buckets, asserts `CachedBucketCount == 3`, asserts ONE Warn line emitted (dedup guard works), and asserts a subsequent over-cap miss still resolves via the resolver (correctness preserved past cap). Cap log line upgraded from `Verbose` to `Warn` so it surfaces in default log levels — past-cap is a real diagnostic, not a routine event. `ResetForTesting_RestoresDefaultCap` pins the cap reset behaviour.
- ~~P2-5: `EmitFrameSummaryIfDue` removed; per-frame summary now uses `ParsekLog.VerboseRateLimited` directly.~~ The old path called `UnityEngine.Time.frameCount` (an ECall) on every `GetCachedSurfaceHeight` invocation, which defeated the "amortise PQS query" advertisement when N concurrent surface ghosts each hit the cache per frame. The hot path now has zero Unity API calls. Dropped the `FrameCountResolverForTesting` and `NativeUnityFrame` test seams that the JIT-bind workaround required; the new summary line key is `"frame-summary"` (was `"terrain-cache-frame-summary"`) and reports cumulative `cacheHits / cacheMisses / cached / body` — counters reset only on `Clear()` / scene transition (since-scene-began monotonics, more useful than per-frame deltas anyway).
- ~~P3-3: defensive accumulator reset in `CloseCurrentTrackSection`.~~ After emitting the SurfaceMobile clearance summary, `CloseCurrentTrackSection` now resets `surfaceMobileSamplesThisSection`, `surfaceMobileMinClearanceThisSection`, `surfaceMobileMaxClearanceThisSection`, and `surfaceMobileClearanceSumThisSection` to NaN/0. `StartNewTrackSection` already resets, but closing without immediately opening (Stop Recording, scene exit) left stale state.
- ~~P3-4: design-doc note on `body.TerrainAltitude` substitution.~~ One paragraph added to `docs/parsek-ghost-trajectory-rendering-design.md` Phase 7 section explaining the implementation calls `body.TerrainAltitude(lat, lon, true)` instead of `body.pqsController.GetSurfaceHeight(...)` directly — the wrapper provides ocean clamping and matches `VesselSpawner.ClampAltitudeForLanded` prior art. Future implementers should not "fix" this back.

## Done — v0.9.1 Phase 8 outlier rejection

- ~~Phase 8: environment-aware outlier classifier with per-environment acceleration ceilings, bubble-radius single-tick position-delta cap, and altitude bounds against `body.sphereOfInfluence`.~~ `Source/Parsek/Rendering/OutlierClassifier.cs` (pure static, HR-1 / HR-3 audited) dispatches by `TrackSection.environment`; thresholds live in the new `OutlierThresholds` struct (defaults: Atmospheric 500 / ExoPropulsive 200 / ExoBallistic 50 / SurfaceMobile 30 / SurfaceStationary 10 / Approach 50 m/s², bubble 2500 m, altitude floor -100 m, altitude ceiling margin 1000 m, cluster rate 0.20). RELATIVE-frame and OrbitalCheckpoint sections short-circuit to an empty result (HR-7) since their `latitude/longitude/altitude` fields are anchor-local metres rather than body-fixed degrees.
- ~~Phase 8: `OutlierFlags` packed-bitmap POCO + `SectionAnnotationStore.PutOutlierFlags / TryGetOutlierFlags / GetOutlierFlagsCountForRecording`.~~ `Source/Parsek/Rendering/OutlierFlags.cs`. LSB-first bit packing; in-memory `SampleCount` is NOT persisted — the loader backfills it from `section.frames.Count` so `IsRejected` bounds-checks correctly.
- ~~Phase 8: spline `Fit` consumes `OutlierFlags` to skip rejected samples.~~ `TrajectoryMath.CatmullRomFit.Fit` gained an optional `OutlierFlags rejected = null` parameter (back-compat default null). When supplied, samples whose `IsRejected(i)` is true contribute neither knot nor control. After filtering, fewer than 4 kept samples returns `IsValid=false` with `failureReason="after-rejection sample count {kept} < min 4 (rejected {n} of {N})"` so the orchestrator surfaces a Pipeline-Smoothing Warn and falls through to the legacy bracket lerp (HR-9).
- ~~Phase 8: `SmoothingPipeline.FitAndStorePerSection` runs the classifier before the spline fit.~~ Gated on a new `useOutlierRejection` rollout flag. Section-wide cluster trips emit a `Pipeline-Outlier` Warn deduped per `(recordingId, sectionIndex)` per session via the same `s_clusterWarnLogged` HashSet pattern used by Phase 4's frame-decision dedup.
- ~~Phase 8: `.pann OutlierFlagsList` block read/write.~~ Per-entry schema: sectionIndex (int32) + classifierMask (byte) + packedBitmap (length-prefixed byte[]) + rejectedCount (int32). Empty / all-kept entries are dropped at write time so the block is compact in the steady-state "no krakens detected" case (mirrors the AnchorCandidates pattern). Reader caps the entries at `MaxOutlierFlagsEntries = 10_000` and per-entry bitmap byte length at `(MaxKnotsPerSpline + 7) / 8`. `AlgorithmStampVersion` bumped 8 → 9 (post-rebase onto Phase 5 review-pass-5 tip; Phase 5 took the stamp through 5 → 6 → 7 → 8 across its review passes) so existing v8 `.pann` files are discarded via `alg-stamp-drift` and recomputed on first load (HR-10).
- ~~Phase 8: `useOutlierRejection` rollout flag (default on) + ConfigurationHash extension.~~ Property + persistence wired alongside `useSmoothingSplines` / `useAnchorCorrection` / `useAnchorTaxonomy` / `useCoBubbleBlend` in `ParsekSettings` and `ParsekSettingsPersistence`. `PannotationsSidecarBinary.ComputeConfigurationHash` canonical encoding length grew 53 → 86: the previously-reserved outlier accel bytes at [21..28] became `OutlierThresholds.Default`'s Atmospheric / ExoPropulsive ceilings, bytes [53..84] added the remaining four environment ceilings + bubble radius + altitude floor + ceiling margin + cluster rate floats, and byte [85] holds `useOutlierRejection`. Any threshold tweak or flag flip invalidates cached `.pann` files via `config-hash-drift` (HR-10 freshness).
- ~~Phase 8: `Pipeline-Outlier` logging contract per §19.2.~~ Per-rejected-sample Verbose ("Sample rejected: …", capped at 50 lines per section followed by a single "log capped" line for overflow), per-section Info summary ("Per-section rejection summary: … sampleCount / rejectedCount / accel / bubble / altitude / cluster") emitted unconditionally so HR-9 visibility holds even when the section is clean, and per-section Warn ("Cluster threshold exceeded → low-fidelity tag: …") deduped per session.
- ~~Phase 8: in-game test `Pipeline_Outlier_Kraken`.~~ `[InGameTest(Category = "Pipeline-Outlier", Scene = GameScenes.FLIGHT)]` in `Source/Parsek/InGameTests/RuntimeTests.cs`; builds a synthetic 12-sample ExoBallistic section with a single kraken velocity + lat-jump spike at sample 5, runs the orchestrator end-to-end, and asserts (a) the OutlierFlags entry stores with the kraken sample marked rejected, (b) the spline still fits (11 kept ≥ 4), (c) evaluating the spline at the kraken UT places the rendered world position 5x closer to the linear midpoint of neighbours 4/6 than to the kraken's deflected lat/lon — proving the spline did not deflect through the kraken sample.

### Phase 8 follow-ups

- §9.1 propagation helper outlier-aware boundary search (deferred from Phase 8 per plan §5.4). `RenderSessionState.TryEvaluatePerSegmentWorldPositions` reads `recordedWorld` from the first sample at-or-after the boundary UT via `TryFindFirstPointAtOrAfter`. If that sample is itself flagged outlier, the propagator computes `(recordedWorld - smoothedWorld)` from a kraken sample and the resulting ε is distorted by up to the bubble-radius cap (~2500 m). The fix requires mapping `Recording.Points` index back to `(sectionIndex, sampleIndex)` for outlier-bitmap lookup; out-of-scope for the kraken-spike done-condition. The in-source breadcrumb lives next to `TryFindFirstPointAtOrAfter` in `RenderSessionState.cs`; smoothed component is already clean (the spline was fit through cleaned samples per Phase 8 wiring) so the residual error from a kraken `recordedWorld` is bounded.
- Plan §1 acceleration thresholds and §22.1 design-doc thresholds are intentionally still "deferred" for empirical tuning. Phase 8 ships baseline values that catch krakens (1000+ m/s² ticks) without false-positives on 30g chemistry-rocket bursts. Tests assert the contract (acceleration cap rejects krakens, accepts real burns) NOT the constants — so re-tuning won't break the suite.
- Acceleration-from-position fallback intentionally disabled (deviation from plan §1.2 / §12 risk note). Plan accepted "4× sensitivity to position noise"; in practice with `velocity = Vector3.zero` on synthetic test fixtures, the position 2nd-derivative produces 500+ m/s² estimates for normal 1-second orbital sampling that trip every environment ceiling. Phase 8 falls back to "skip the test when velocity is unavailable" instead — production recordings populate velocity from KSP's frame, so this only affects test fixtures and any future code path that constructs TrajectoryPoints without setting velocity. Bubble-radius classifier still catches single-tick teleports independently. Documented inline in `OutlierClassifier.ComputeAccelerationMagnitude`.
- Synthetic `pipeline-outlier-kraken.prec` fixture (plan §10.4) — defer to a follow-up. The xUnit suite + `Pipeline_Outlier_Kraken` in-game test cover the contract; integrating the fixture into `SyntheticRecordingTests.InjectAllRecordings` is a separate carefully-balanced injection.
- Design doc §17.3.1 ConfigurationHash table row for `useOutlierRejection` and the Phase 8 threshold bytes — small doc nit; bundle when convenient.

### Done — Phase 8 review pass 3 (cross-tree global sweep)

- ~~Both post-hydration sweeps were called PER-TREE inside `ParsekScenario.OnLoad`'s tree-loading loop, dropping cross-tree deferred entries.~~ Phase 8 review-pass-2 P2 wired `RecomputeDeferredCoBubbleTraces` and Phase 5 review-pass-4 wired `RevalidateDeferredCoBubbleTraces` into `ParsekScenario.OnLoad`, but both sweeps fired with `tree.Recordings` (the just-finished tree) only. Cross-tree co-bubble traces are real because commit-time `DetectAndStore` scans `RecordingStore.CommittedRecordings` (which spans every tree); a deferred entry in tree T1 may reference a peer in tree T2. With per-tree sweep timing, T1's sweep ran before T2 was visible, the recompute saw only T1's recordings, no overlap pair was emittable, the deferred entry was drained without producing traces, and the later T2 hydration never re-triggered the sweep — the missing trace stayed missing for the session. Symmetric for the validation sweep dropping a stale T1 trace whose peer was a still-unhydrated T2 recording. Fix: move BOTH sweeps from per-tree to a single post-ALL-COMMITTED-TREES pass. The committed-tree loop builds the union of every tree's `Recordings` into a `Dictionary<string, Recording>`, then calls `RecomputeDeferredCoBubbleTraces` (first — freshly-built signatures match the live peer by construction, so the validation sweep below won't drop them) followed by `RevalidateDeferredCoBubbleTraces`. The active-tree restore branch builds a similar union from `RecordingStore.CommittedRecordings` (already populated by the prior committed-tree loop) plus the active tree's recordings, then runs the same sweep pair. `RevalidateDeferredCoBubbleTraces` gained a per-entry `peer-not-in-load-set` Verbose log emitted before the committed-recordings fallback fires, plus a `peerNotInLoadSet` counter in the summary line — HR-9 visibility for the diagnostics-only case (peer deleted from the save file between sessions; peer-tree never loaded). Two new xUnit tests in `CoBubbleSidecarRoundTripTests`: `RecomputeDeferredCoBubbleTraces_CrossTreePeer_ResolvesAfterAllTreesHydrated` exercises the full flow including a pre-fix demonstration step (per-tree sweep produces empty `.pann`) followed by the post-fix global sweep (writes both sides); `RevalidateDeferredCoBubbleTraces_PeerNotInLoadSet_LogsVerboseAndDrops` pins the new Verbose log + summary counter. AlgorithmStampVersion stays at 9 — this is OnLoad lifecycle correctness, not changes to persisted content semantics.

### Done — Phase 8 review pass 2 (P2 deferred recompute + P3 PrimaryDesignation)

- ~~P2: lazy recompute on the mid-tree-load path emitted no co-bubble traces because peers iterated AFTER the current recording had empty Points.~~ Phase 5 review-pass-3 P3-1 added `DetectAndStoreCoBubbleTracesForRecording(rec, treeLocalLoadSet)` to the `LoadOrCompute` recompute path (file-missing / version-drift / alg-stamp-drift / config-hash-drift / epoch-drift / format-drift / payload-corrupt). On save load, `RecordingStore.LoadRecordingFiles` iterates `tree.Recordings.Values` sequentially per-recording — when this fired for the FIRST recording in a tree, later same-tree peers in `treeLocalLoadSet` still had empty `Points`. `DetectAndStore` scanned them, found no overlap-eligible samples, emitted no co-bubble trace, and `TryWritePann` persisted an empty `CoBubbleOffsetTraces` block. If the later peer's own `.pann` was already fresh (cache-hit, no recompute triggered for them), the missing owner-side trace stayed missing for the session — same root cause as the deferred-signature P1 the post-hydration sweep was built for, just in a different code path. Fix: when `treeLocalLoadSet != null`, `LoadOrCompute` enqueues `rec` into a new `s_deferredCoBubbleRecomputes` Dictionary keyed on recording id and skips the inline `DetectAndStoreCoBubbleTracesForRecording` + `PersistPeerPannFiles`. `FitAndStorePerSection` + `TryWritePann` still run (the spline + outlier work isn't peer-dependent), and the `.pann` is written with whatever co-bubble traces were already in the in-memory store (typically none). New `SmoothingPipeline.RecomputeDeferredCoBubbleTraces(IReadOnlyDictionary<string, Recording>)` drains the deferred set and runs `DetectAndStore` + `PersistPeerPannFiles` per entry against the now-fully-hydrated tree, plus rewrites owner `.pann` with the regenerated traces. `ParsekScenario.OnLoad` invokes the recompute sweep at both call sites (committed-tree loop + active-tree restore loop), BEFORE the existing `RevalidateDeferredCoBubbleTraces` signature sweep — the freshly-detected traces have signatures that match the live peer by construction, and any pre-existing deferred-validation entries refer to disjoint (owner, peer, UT) tuples. Inline path (`treeLocalLoadSet == null`) keeps the immediate detect + peer-write behaviour: non-tree-load lazy compute (manual cache-bust outside OnLoad) has no later peers waiting to hydrate. New `SmoothingPipeline.DeferredCoBubbleRecomputesCountForTesting` test seam mirrors `DeferredCoBubbleValidationsCountForTesting`. `LoadOrCompute_DriftedAlgStamp_RecomputesCoBubbleTraces` extended to exercise both halves end-to-end: first call with `treeLocalLoadSet={A,B-empty}` produces an empty-traces `.pann` and enqueues recA; second call to `RecomputeDeferredCoBubbleTraces({A,B-full})` populates BOTH sides' `.pann` files. `LoadOrCompute_LazyRecompute_PeerPannSymmetricallyUpdated` updated to exercise the inline non-tree-load path explicitly (the deferred-path coverage moved to the alg-stamp test). AlgorithmStampVersion stays at 9 — this is validation-flow correctness, not changes to persisted content semantics.
- ~~P3: `primaryDesignation` byte inverted on both stored sides.~~ `CoBubbleOverlapDetector.DetectAndStore` wrote `aIsPrimaryHint ? 1 : 0` for the trace stored under `recA` and `aIsPrimaryHint ? 0 : 1` for the trace stored under `recB`. Schema §17.3.1 contract: `primaryDesignation = 0` means self-is-primary, `1` means self-is-peer, RELATIVE to the recording the trace is stored under. With `aIsPrimaryHint=true` (lower-ordinal-id wins, recA is the hinted primary), the trace under recA records self=primary → designation=0 (was 1); the trace under recB records self=peer → designation=1 (was 0). Symmetric flip at `!aIsPrimaryHint`. The selector doesn't read the field today (`CoBubblePrimarySelector` ignores it), so there's no in-game regression — but the persisted bytes contradicted the schema and would break the moment a future consumer started trusting the field. Fix: swap the two byte literals to match the schema contract. AlgorithmStampVersion stays at 9 — accepting that v9 files written before this commit have the wrong designation byte (still functionally correct because the selector ignores the field) and the next AlgStamp bump for any other reason will sweep them up. Documented in the inline alg-stamp comment block in `PannotationsSidecarBinary.cs`. New xUnit test `DetectAndStore_PrimaryDesignation_MatchesSchemaContract` exercises both `aIsPrimaryHint=true` and `aIsPrimaryHint=false` cases by reading the persisted bytes back via `SectionAnnotationStore.TryGetCoBubbleTraces`.

### Done — Phase 8 review pass (P1 / P2 / P3-2 / P3-4)

- ~~P1-1: Plan §5.4 §9.1 TODO breadcrumb missing in source.~~ Added a 5-line `TODO(Phase 8 follow-up)` comment near `TryFindFirstPointAtOrAfter` inside `RenderSessionState.TryEvaluatePerSegmentWorldPositions` documenting the deferred outlier-aware boundary search and pointing back to this doc.
- ~~P2-1: Body-resolver exception silently swallowed in `OutlierClassifier.Classify`.~~ The `try { body = bodyResolver(...) } catch { body = null; }` block now surfaces the swallowed exception via `ParsekLog.VerboseRateLimited("Pipeline-Outlier", "classifier-body-resolve-exception", ..., 5.0)`, matching `SmoothingPipeline.ResolveBody`'s HR-9 contract. A degenerate FlightGlobals state mid-load is now diagnosable from KSP.log instead of silently no-op'ing the altitude classifier for the entire section.
- ~~P2-2: Bubble-radius math fails at the poles.~~ `OutlierClassifier.ComputePositionDeltaMagnitude` now uses haversine great-circle distance (singularity-free across the full sphere) instead of the flat-earth `Δlon × R × cos(meanLat)` approximation, which collapsed to ≈ 0 at the poles. New regression test `OutlierClassifier_BubbleRadius_PolarKraken_DetectsLongitudeFlip` places samples at lat 89° with a 180° longitude flip and asserts the bubble-radius classifier fires. Test fixture body-radius fallback also tightened to a Kerbin-OOM 600 km value (was a flat 60 km/deg → ~3.43e6 m equivalent).
- ~~P2-3: RELATIVE-frame Classify emitted section-summary for ineligible sections.~~ The early-return path for RELATIVE / OrbitalCheckpoint / null-frames sections no longer calls `EmitPerSectionSummary`; production never reaches this path because `SmoothingPipeline.ShouldFitSection` gates RELATIVE / OrbitalCheckpoint out before Classify is called, but the misleading `rejectedCount=0` Info line was confusing. Comment on the early-return now documents the contract. New test `OutlierClassifier_RelativeFrame_EmitsNoSectionSummary` pins the contract.
- ~~P2-4: CHANGELOG entry violated project style (≤ 2 sentences, user-facing only).~~ The Phase 8 bullet was 6 sentences with embedded technical detail (per-environment thresholds, byte offsets, AlgorithmStampVersion bump). Trimmed to two sentences focused on user-visible behaviour; tech detail stays in the corresponding entries here.
- ~~P3-2: Cluster-Warn dedup regression test missing (plan §10.2 line item).~~ New `Outlier_ClusterWarn_DedupedAcrossRecompute` test in `SmoothingPipelineLoggingTests.cs` calls `FitAndStorePerSection` twice on the same kraken-clustered fixture and asserts exactly one Cluster Warn line emits across both calls. Pins the `s_clusterWarnLogged` HashSet behaviour. Companion `Outlier_PerSectionInfo_AlwaysEmitted_OnCleanSection` test pins the HR-9-visibility contract for clean sections.
- ~~P3-4: `Pipeline_Outlier_Kraken` in-game test residual ratio too coarse + leaked overrides.~~ Added an absolute residual cap (10 km) alongside the existing 5x ratio so the test fails if the spline blows up to some other large offset. Removed the redundant `flags.SampleCount = sampleCount` line (Classify already sets it). The test already calls `SmoothingPipeline.ResetForTesting` before `yield break`, which clears `BodyResolverForTesting` and `UseOutlierRejectionResolverForTesting` (and `s_clusterWarnLogged`); a comment now documents that explicitly.
## 637. Track remaining refactor opportunities after Refactor-4 archive

**Status:** TODO - active reference in
`docs/dev/plans/refactor-remaining-opportunities.md`.

Refactor-4's completed planning docs are archived under
`docs/dev/done/refactor/`. The remaining opportunities are not approved
implementation work; use the linked document to choose the next focused
proposal and avoid overlap with active unfinished-flight, ghost trajectory, or
rendering branches.

## 636. Destroyed staged flights could miss Unfinished Flights when finalization stamped stale SubOrbital ~~done~~

**Status:** ~~done~~ — fixed in the destroyed-stages Unfinished Flights
worktree.

The 2026-04-29 Kerbal X repro staged into an upper/root vessel and a
probe-controlled booster, then let both crash. Runtime logs showed the
upper/root recording had a fresh `Destroyed` finalizer cache, but
`FinalizeIndividualRecording` skipped cache application because an earlier
`SubOrbital` terminal was already present. Unfinished Flights then rejected the
focus slot as `stableTerminalFocusSlot`. The probe/booster recording had
`Destroyed`, but also carried a later child BranchPoint for the crash/debris
event; the classifier rejected it as `downstreamBp` before reaching the
`Destroyed => crashed` rule.

Fix: `FinalizeIndividualRecording` now allows a `Destroyed` finalizer cache to
repair an existing `SubOrbital` terminal on an effective leaf, using the
cache-applier's already-finalized repair mode so stale predicted tails are
replaced only when the cached destruction UT is not in the future. Stable
endpoints such as `Landed`, `Splashed`, and `Orbiting` are not overwritten.
`UnfinishedFlightClassifier` now treats a `Destroyed` chain tip as conclusive
when its downstream BranchPoint has no resolved Rewind Point route of its own,
so crash/debris bookkeeping BPs do not hide playable Re-Fly slots while real
downstream RPs still take precedence.

## 635. Stable-leaf Unfinished Flights were forward-only after Rewind Point splits ~~done~~

**Status:** ~~done~~ — fixed in the stable-leaves worktree stacked on the
invocation-linearization prerequisite.

The stable-leaves design extended Unfinished Flights beyond crash-only leaves:
after a multi-controllable split, a non-focused controllable child that later
ends `Orbiting` or `SubOrbital` can still be an unfinished mission because the
player never actively flew it to a final outcome. Stranded EVA kerbals whose
terminal state is not `Boarded` are also unfinished, including legacy rows that
do not have a focused-slot signal. Stable terminal outcomes (`Landed`,
`Splashed`, `Recovered`, `Docked`, `Boarded`) remain forward-only, and debris or
non-controllable slots are excluded.

Fix: `RewindPoint` now persists `FocusSlotIndex`; `RewindPointAuthor` captures
it from the active vessel PID at split time; the Unfinished Flights predicate is
centralized in `UnfinishedFlightClassifier`; original tree commit and fresh
re-fly supersede commit both use the same slot-aware classifier to promote
qualifying leaves to `CommittedProvisional`. Legacy vessel rows with
`FocusSlotIndex=-1` stay excluded for orbiting/suborbital terminal states so old
saves do not accidentally surface stable focused flights as unfinished.
If multiple split slots unexpectedly match the active vessel PID, capture keeps
the first match and logs the aliasing. Fresh-provisional merge classification now
preflights before supersede relations, tombstones, or journal writes; a missing
Site B-1 slot lookup aborts without durable merge mutations when the terminal
outcome needs slot-aware stable-leaf/EVA handling, rather than silently falling
back to the v0.9 terminal-kind rule.

UI fix: Unfinished Flight rows now show `Fly` plus an explicit `Seal` action.
Seal sets `ChildSlot.Sealed`/`SealedRealTime`, leaves the recording and
`MergeState` unchanged, removes that slot from Unfinished Flights, and lets
`RewindPointReaper` count a sealed `CommittedProvisional` slot as closed once
every sibling slot is also closed. Sealed `NotCommitted` slots still block reap.
The in-game test suite has Seal popup coverage for replacing an already-open
confirmation dialog plus both confirm/cancel button lock cleanup paths. It also
has a synthetic runtime fixture that validates stable-leaf group membership,
route resolution, Seal's non-mutating slot close, and last-seal RP reap without
requiring a hand-authored save.

v2 follow-up: stable terminal leaves that the default predicate excluded can now
be manually Stashed from the Recordings table while their Rewind Point still
exists. Stash sets `ChildSlot.Stashed`/`StashedRealTime`, leaves the recording and
`MergeState` unchanged, makes the row appear under Unfinished Flights with the
same `Fly` and `Seal` actions, and makes `RewindPointReaper` treat an unsealed
stashed `Immutable` slot as still open. Stash does not resurrect already-reaped
RP quicksaves; all-closed stable splits can still disappear before the player
has a slot to stash. Under the existing B2-A in-place re-fly policy, the merge
confirmation clears `ChildSlot.Stashed` before forcing the recording `Immutable`
so an in-place Stashed re-fly closes and reaps like other in-place stable-leaf
commits.

Diagnostics follow-up: Settings -> Diagnostics now shows live RP breakdown
counts next to total rewind-point quicksave disk usage: crashed-open,
stable-open, and sealed-pending. The split is monitoring only; TTL cleanup or a
manual bulk-wipe action remains deferred because it needs a separate destructive
cleanup policy.

Site B-2 limitation: this PR preserves B2-A for in-place continuations. The
in-place merge path may classify an unfinished outcome as
`CommittedProvisional`, but immediately forces the same recording back to
`Immutable` and reaps the RP because the merge confirmation is treated as the
player's final commitment to that slot. It does not auto-seal the slot and does
not create a fresh provisional; B2-B still requires a deeper recorder/merge
rearchitecture.

## 634. Re-fly chain extension wrote origin-rooted star supersede rows instead of linear prior-tip rows ~~done~~

**Status:** ~~done~~ — fixed in the invocation-linearization worktree.

The §11.2 stable-leaves prerequisite investigation found that
`MergeCrashedReFlyCreatesCPSupersedeTest` is an in-game single-merge smoke
test, not a headless chain-extension regression. Running
`dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter
MergeCrashedReFlyCreatesCPSupersedeTest` on 2026-04-28 built successfully
but found no matching xUnit test, and source inspection showed the in-game
test only asserts the first crashed provisional remains visible after merge.
It never constructs a second re-fly and never asserts the second relation is
`{priorTip -> newRecording}`.

The actual bug was the prerequisite design's star graph: invocation stamped
the slot origin as the supersede target, and `SupersedeCommit.AppendRelations`
rooted closure at `marker.OriginChildRecordingId`. Repeated re-flies therefore
wrote `{origin -> attempt1}`, `{origin -> attempt2}`, ...; the forward walker
uses the first matching relation, so the oldest re-fly won and later attempts
were not the dominant effective slot recording.

Fix: `ReFlySessionMarker.SupersedeTargetId` now persists the prior effective
tip, `RewindInvoker.AtomicMarkerWrite` computes that prior tip once before the
in-place/fresh-provisional branch, `SupersedeCommit.AppendRelations` roots the
closure at `SupersedeTargetId ?? OriginChildRecordingId`, and
`EffectiveState.ComputeSubtreeClosureInternal` includes the root override in
its cache key. Headless tests now cover marker round-trip and weak validation,
pending-tree marker targets, fresh-provisional and in-place marker stamping,
root-override closure equivalence/caching, linear append relations, and the
hybrid legacy-star plus new-linear graph. When a restored origin still has the
matching vessel PID but an existing supersede relation makes a different
recording the slot's prior tip, invocation rejects the in-place path and creates
a fresh provisional so commit cannot write a two-node supersede cycle.

§11.3 Site B-2 result: in-place re-fly merges cannot naturally extend the
slot chain in this PR; only the fresh-provisional path supports chain
extension. Preserve the existing B2-A force-Immutable in-place handling.
`MergeDialog.TryCommitReFlySupersede`'s in-place path is already architected
around one recording acting as both slot effective state and supersede target,
with optimizer-split tip resolution, self-link skips, session-owned chain
cleanup, RP reap, and durable save in that branch.
Switching it to B2-B fresh-provisional would require a deeper recorder/merge
rearchitecture than this prerequisite PR. The limitation remains documented:
in-place re-fly merges close the slot via `MergeState.Immutable`; natural
chain extension is supported on the fresh-provisional path.

---

## Done — v0.9.1 test harness hardening

- ~~AtomicMarkerWrite xUnit flake on first/full runs.~~ `AtomicMarkerWrite_InPlaceContinuation_ExceptionDoesNotRemoveOrigin` could fail with `storedOrigin == null` during full-suite execution even though 50 standalone targeted runs were clean. The named `"Sequential"` collection serialized its own members but had no `CollectionDefinition`, so xUnit could still run it beside other collections. Fix: add `SequentialCollectionDefinition` with `DisableParallelization = true` plus a meta-test pinning that harness contract.

---
## Done — v0.9.1 Phase 6 anchor taxonomy + DAG propagation

- ~~Phase 6: emit AnchorCandidate entries for §7.2–§7.10 at commit time.~~ Implemented in `Source/Parsek/Rendering/AnchorCandidateBuilder.cs`; wired into `SmoothingPipeline.FitAndStorePerSection`.
- ~~Phase 6: DAG propagation per §9.1 across BranchPoint edges.~~ Implemented in `Source/Parsek/Rendering/AnchorPropagator.cs`; runs from `RenderSessionState.RebuildFromMarker` after the Phase 2 LiveSeparation seeds land.
- ~~Phase 6: cross-recording propagation at chain tips (§9.3).~~ Chain edges enumerate by `Recording.ChainId` + `Recording.ChainIndex` (NOT the EVA-linkage `Recording.ParentRecordingId`, which is already covered by the `BranchPointType.EVA` loop). Members are sorted by `ChainIndex` and consecutive pairs at compatible boundary UTs (within `1e-3` s tolerance) emit a chain edge. Mismatched boundaries log a `chain-edge-boundary-mismatch` Verbose and skip — see `AnchorPropagator.cs` edge enumeration.
- ~~Phase 6: suppressed-subtree filtering in propagation (§9.4 / HR-8).~~ Routed through `SessionSuppressionState.IsSuppressed` for both edge endpoints; a suppressed parent or child skips the edge with a `Pipeline-AnchorPropagate` Verbose `suppressed-predecessor` line.
- ~~Phase 6: §7.11 priority resolver.~~ `Source/Parsek/Rendering/AnchorPriority.cs` with rank table; integrated via `RenderSessionState.PutAnchorWithPriority`.
- ~~Phase 6: `.pann AnchorCandidatesList` block populated; `AlgorithmStampVersion` v2→v3, then v3→v4 in the ultrareview P1-A follow-up (HR-10).~~ The bit-pack puts `AnchorSide` into bit 7 of the persisted type byte, leaving the schema layout stable. The first bump (v2→v3) invalidated pre-Phase-6 files via the existing alg-stamp-drift path; the second bump (v3→v4) reflects the canonical-encoding gain of the `useAnchorTaxonomy` byte (P1-A) so a reader walking a v3 file with the new hash function correctly invalidates it before comparing the embedded hash against the recomputed one.
- ~~Phase 6: `useAnchorTaxonomy` rollout flag (default on).~~ Property + persistence wired alongside `useSmoothingSplines` / `useAnchorCorrection` in `ParsekSettings` and `ParsekSettingsPersistence`.
- ~~Phase 6: §7.4 RELATIVE-boundary ε resolver.~~ `IAnchorWorldFrameResolver.TryResolveRelativeBoundaryWorldPos` composes `TrajectoryMath.ResolveRelativePlaybackPosition` with v5/v6/v7 dispatch + v7+ absolute-shadow fallback.
- ~~Phase 6: §7.5 OrbitalCheckpoint ε resolver.~~ `IAnchorWorldFrameResolver.TryResolveOrbitalCheckpointWorldPos` runs the analytical Kepler propagation against the adjacent checkpoint section's `OrbitSegment`.
- ~~Phase 6: §7.6 SOI-transition ε resolver.~~ Same dispatch as §7.5 but the checkpoint stores Keplerian elements in the post-SOI body's frame; world-frame ε is computed in that body's frame.
- ~~Phase 6: §7.10 Loop ε resolver.~~ `IAnchorWorldFrameResolver.TryResolveLoopAnchorWorldPos` reads the loop anchor vessel's current world position via `FlightGlobals.Vessels.persistentId == LoopAnchorVesselId`. Phase 6's Loop ε is the *session-entry* corrective offset between the recorded loop seed and the live anchor's current pose. The per-cycle `loop_offset(loop_phase)` term in the design doc §7.10 / §15.14 ε formula is composed downstream by the existing loop playback path (`ParsekFlight.PositionLoopGhost` at `ParsekFlight.cs:17440`, which routes through `InterpolateAndPositionRelative` and reads the recorded RELATIVE-frame anchor offsets directly from the loop section's `frames` list). Phase 6 + the existing loop-relative playback together produce the design-doc formula: `ε_session_entry + recorded_loop_offset(loop_phase) + anchor_position_now` — Phase 6 owns the first term, the loop-relative playback path owns the other two.
- ~~Phase 6: §7.9 SurfaceContinuous priority demotion.~~ Rank moved from 2 to 6 so the Phase-7-pending ε = 0 stub cannot outrank a real OrbitalCheckpoint ε. Phase 7 will promote back to rank 2 with the corresponding `AlgorithmStampVersion` bump when the per-frame terrain raycast lands.
- ~~Phase 6: per-source in-game tests.~~ `Pipeline_Anchor_Dock`, `Pipeline_Anchor_RelativeBoundary`, `Pipeline_Anchor_OrbitalCheckpoint`, `Pipeline_Anchor_SOI`, `Pipeline_Anchor_Loop`, `Pipeline_Anchor_SuppressedSubtree` added to `RuntimeTests.cs`; each runs through the resolver test seam under the live KSP runtime to verify ε computation end-to-end.
- ~~Phase 6: §9.1 propagation rule applies the real `(recordedOffset − smoothedOffset)` correction term.~~ Earlier Phase 6 commit landed identity propagation; reviewer P1-1 caught that pre-Phase-9 recordings have a few-tick sampling-noise offset that must be corrected. New helper `RenderSessionState.TryEvaluatePerSegmentWorldPositions` returns `(recordedWorld, smoothedWorld)` for a (recordingId, sectionIndex, ut) tuple by composing the existing `TryFindFirstPointAtOrAfter` + `TryLookupSurfacePosition` + `SectionAnnotationStore.TryGetSmoothingSpline` + `CatmullRomFit.Evaluate` chain. The propagator computes recorded/smoothed offsets on each side of an edge and feeds them through `Propagate(epsilonUpstream, recordedOffset, smoothedOffset)`. Chain edges keep identity propagation (recordedOffset = 0 by PID continuity). Either side missing a spline / sample / Absolute frame triggers a `no-spline-skip` / `no-sample-skip` / `section-not-absolute-skip` Verbose and identity fallback (HR-9).
- ~~Phase 6: §9.4 / HR-8 suppressed-predecessor xUnit coverage.~~ New `AnchorPropagator.SuppressionPredicateForTesting` test seam routes around `SessionSuppressionState`'s `ParsekScenario.Instance` dependency; the suppressed-child and suppressed-parent test cases in `AnchorPropagationTests.cs` now actually exercise the §9.4 closure (previously the misleadingly-named `Run_SuppressedChild_*` test exercised the cycle path instead).
- ~~Phase 6: ProductionAnchorWorldFrameResolver xUnit guard-path coverage.~~ 18 new tests in `ProductionAnchorWorldFrameResolverTests.cs` exercise null-rec / out-of-range / wrong-adjacent-frame / pid==0 / etc. early-return paths. Each test wraps the call in a `try / catch (SecurityException)` guard following the `ParsekUITests.cs` headless-Unity pattern.
- ~~Phase 6: per-candidate Verbose at commit time.~~ Design-doc §19.2 Stage 3 row 1 calls for one Verbose per emitted candidate (not just a summary). `AnchorCandidateBuilder.BuildAndStorePerSection` now emits a `Pipeline-Anchor` Verbose per candidate with `bpType` so DockOrMerge byte aliasing of Undock/EVA/JointBreak surfaces in telemetry without bumping `AnchorSource`.
- ~~Phase 6: Loop sentinel guard.~~ `EmitLoopMarkers` now requires `LoopPlayback == true` AND `LoopIntervalSeconds > LoopTiming.UntouchedLoopIntervalSentinel` AND `LoopAnchorVesselId != 0`. The previous `<= 0.0` guard was dead code — `Recording.LoopIntervalSeconds` initializes to the sentinel (10.0).
- ~~Phase 6: design-doc Stage 1 / Sidecar canonical drift token set.~~ Both the §19.2 Sidecar I/O table and the inline note next to the `PannotationsSidecarProbe` description now enumerate the full token set including `recording-id-mismatch`, `payload-corrupt`, and `probe-failed`.
- ~~Phase 6: removed dead test seam `AnchorCandidateBuilder.TreeLookupOverrideForTesting`.~~ Builder takes the tree explicitly; the static seam was unused.
- ~~Phase 6: anchor source counters use a real `default` arm.~~ `AnchorCandidateBuilder.BuildAndStorePerSection` no longer subtracts to derive `splitCount` — a real counter increments inside the switch's `default` arm so a future `AnchorSource` value silently rolls into "other" instead of being misattributed.
- ~~Phase 6: dead `edge.IsChain ? AnchorSide.Start : AnchorSide.Start` ternary removed.~~
- ~~Phase 6 ultrareview P1-A: `useAnchorTaxonomy` flag participates in the `.pann ConfigurationHash`.~~ `PannotationsSidecarBinary.ComputeConfigurationHash` gained a `useAnchorTaxonomy` byte at offset 51 (length grew 51 → 52). `SmoothingPipeline.CurrentConfigurationHash` keys its cache on the live flag value via `AnchorCandidateBuilder.ResolveUseAnchorTaxonomy`. Without the flag in the hash, a `.pann` written when the flag was off would cache-hit a flag-on session and the §7.4-§7.10 anchors would never re-emit until manually deleted or alg-stamp bumped. Three new xUnit tests pin: hash differs by flag, single-arg overload defaults to flag-on (compat), and a flag-flip-write-then-read cycle surfaces `config-hash-drift` and triggers the lazy recompute. Design doc §17.3.1 Configuration Cache Key table now includes the row.
- ~~Phase 6 ultrareview P1-B: §9.1 propagation handles inertial-frame splines (FrameTag=1).~~ The previous helper treated only `FrameTag == 0` (body-fixed) as a real spline hit; `FrameTag == 1` (ExoPropulsive / ExoBallistic — every burn / coast section in production) fell back to `smoothedWorld = recordedWorld` so the §9.1 correction term cancelled to zero. `RenderSessionState.TryEvaluatePerSegmentWorldPositions` now takes an optional `bodyResolver` parameter and dispatches FrameTag=1 through `TrajectoryMath.FrameTransform.DispatchSplineWorldByFrameTag`. The propagator passes its own `ResolveBody` (which composes `BodyResolverForTesting` + `FlightGlobals.Bodies`) so production picks up the body via `FlightGlobals` and xUnit drives it via `TestBodyRegistry.CreateBody` + `FrameTransform.RotationPeriodForTesting` + `FrameTransform.WorldSurfacePositionForTesting`. The `body != null` check uses `object.ReferenceEquals(body, null)` to bypass Unity's overloaded `==` (a `TestBodyRegistry`-built body has an uninitialised Unity backing pointer that the overload would otherwise treat as null). Three new xUnit tests pin: end-to-end inertial §9.1 against a fakeKerbin fixture, direct helper-call inertial dispatch, and the no-resolver fallback contract.
- ~~Phase 6 ultrareview P2-A: edge propagation is order-independent via worklist.~~ `RecordingTree.BranchPoints` is a public persisted list with no enforced topological order. The previous single-pass walk processed edges in list order — if a downstream edge appeared before its upstream parent had its anchor seeded, the propagator wrote zero ε and never revisited the child. The new worklist seeds with recordings that already have an anchor (Phase 2 LiveSeparation seeds + the seed pass), pops parent ids, walks outgoing edges via an O(1) `outgoingByParent` index, and re-enqueues each child whenever a new anchor is written. Cycle defense via the existing `visitedEdges` HashSet is unchanged. Two new xUnit tests pin: a 4-recording linear chain with REVERSED `BranchPoints` list order propagates ε all the way to the leaf, and a no-seed test confirms the worklist correctly does nothing when there's no starting anchor.
- ~~Phase 6 ultrareview follow-up P1: edge propagation is section-precise via slot-keyed worklist.~~ The first P2-A iteration keyed worklist + outgoing-edge index by parent recordingId. That fixed BranchPoints list-order dependence but introduced a subtler order-dependence bug: when a recording was queued because an UNRELATED section had a seed anchor, edges whose `parentSectionIdx` was a DIFFERENT, still-unanchored section would fall through to ε = 0, write a stale child anchor, and mark the edge in `visitedEdges`. When the real upstream anchor for that section was later written, the edge was already visited and the corrected ε never flowed downstream. The follow-up keys the worklist + outgoing-edge index by `(parentRecordingId, parentSectionIdx)` ("slot" — encoded as `recordingId@sectionIdx`); seeds by walking every populated `(recordingId, sectionIdx)` pair; only walks edges whose parent slot has been seeded; and on a successful child write, enqueues the child SLOT (not just the recording). Edges depending on still-unanchored slots remain in their bucket and fire correctly when the slot is later anchored via another path. One new xUnit test pins the recovery case: a two-section recording (`recB`) with section 0 seeded and section 1 unanchored, edges `recA → recB.1` (writes recB.1) and `recB.1 → recC` (depends on recB.1), with reversed BranchPoint list and `[recB, recC, recA]` recordings iteration order — the slot-keyed walk defers `recB → recC` until `recB.1` is anchored, so recC inherits the correct ε instead of the stale zero.
- ~~§7.7 review pass 2: shared Kepler-evaluation helper with endpoint fallback.~~ The first §7.7 commit duplicated §7.5's Kepler eval inside `AnchorPropagator.TryEvaluateSmoothedWorldPos` but skipped §7.5's nearest-endpoint fallback for partial-first/partial-last checkpoints — a §7.7 BubbleEntry candidate UT equal to the Checkpoint section's endUT would silently produce ε = 0 when the last sampled checkpoint's endUT was a hair below the section's endUT (common when on-rails close finalises at a slightly later UT than the last sampled checkpoint). Extracted `TrajectoryMath.EvaluateOrbitSegmentAtUT(checkpoints, ut, bodyResolver)` shared between §7.5 (`ProductionAnchorWorldFrameResolver.TryResolveCheckpointSideWorldPos`) and §7.7 (`AnchorPropagator.TryEvaluateSmoothedWorldPos`); helper falls back to the first or last segment based on whether `ut` is past the range start or end. Six new xUnit tests in `OrbitSegmentTests` pin: null/empty checkpoint, null body resolver, body resolver returns null, partial-last endpoint fallback (P2-1 regression), partial-first endpoint fallback, and in-range correct-segment selection.
- ~~§7.7 review pass 2: CHANGELOG trim per project style.~~ Original v0.9.1 §7.7 bullet ran 3 sentences with `IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos`, `Active|Background ↔ Checkpoint`, `.pann AlgorithmStampVersion`, HR-10 — none user-facing. Trimmed to the 2-sentence player-visible "ghosts crossing in or out of the recording session's physics bubble snap cleanly at the boundary" wording per `feedback_changelog_style.md`.
- ~~§7.7 review pass 2: misleading test name `RecordingEndsWithCheckpoint_NoBubbleExit`.~~ The test asserts the OPPOSITE — BubbleExit IS present, only BubbleEntry is absent. Renamed to `RecordingEndsWithCheckpoint_BubbleExitOnly_NoTrailingBubbleEntry`.
- ~~§7.7 review pass 3 P1: Checkpoint playback path didn't apply the anchor ε.~~ `AnchorCandidateBuilder` wired §7.7 candidates onto the Checkpoint section's index, but `ParsekFlight.TryInterpolateAndPositionCheckpointSectionWithOrbitRotation` and the LateUpdate `CheckpointPoint` branch both set `ghost.transform.position` directly from the Kepler-bracketed lerp without consulting `allowAnchorCorrectionInterval`. The propagated ε was stored in `RenderSessionState` but invisible — every §7.7 BubbleEntry/Exit anchor (and any §7.x candidate that lands on a Checkpoint section, e.g., a future §7.5 OrbitalCheckpoint emit-on-self) was a no-op visually. Fix: thread the recording id into the Checkpoint Update path, query `allowAnchorCorrectionInterval(recordingId, sectionIdx, playbackUT)` after computing `interpolatedPos`, and add ε on hit; store `anchorRecordingId` / `anchorSectionIndex` in the `GhostPosEntry` so the LateUpdate `CheckpointPoint` branch re-queries and re-adds ε after FloatingOrigin shifts. Same lookup pattern PointInterp / SinglePoint paths use today.

## Done — v0.9.1 Phase 5 co-bubble overlap blend

- ~~Phase 5: detect co-bubble overlap windows at recording commit time.~~ `Source/Parsek/Rendering/CoBubbleOverlapDetector.cs` walks every recording pair and emits one window per UT range during which both sides have an Active/Background section + Absolute frame + same body + ≥ 0.5s overlap + < 2.5km separation. Windows truncate at separation excursion / TIME_JUMP / structural BranchPoint events (HR-7). Output is sorted deterministically by `(RecordingA, RecordingB, StartUT)` (HR-3).
- ~~Phase 5: build co-bubble offset traces.~~ Each window produces one `CoBubbleOffsetTrace` (resampled at the lower of `coBubbleResampleHz` (4 Hz default) and the section sample rates per §10.2). Trace stores per-axis offset in primary's frame, plus per-trace peer validation fields (`peerSourceFormatVersion`, `peerSidecarEpoch`, SHA-256 `peerContentSignature` over the peer's raw `TrajectoryPoint`s inside the window).
- ~~Phase 5: designated primary selection per §10.1.~~ `Source/Parsek/Rendering/CoBubblePrimarySelector.cs` resolves `peerRecordingId → primaryRecordingId` after the propagator settles, applying the five-rule cascade: live wins → DAG-ancestry hop count to live → earlier StartUT → higher sample rate at overlap midpoint → ordinal recording-id (HR-3). Map lives on `RenderSessionState`; cleared by `Clear` / `RebuildFromMarker`. Deterministic across save/load by construction (every input is session-only and deterministically rebuilt).
- ~~Phase 5: `CoBubbleBlender` consumer hook.~~ `Source/Parsek/Rendering/CoBubbleBlender.cs` exposes `TryEvaluateOffset(peerId, ut, out worldOffset, out status, out primaryId)`. Eleven-value `CoBubbleBlendStatus` enum surfaces every miss reason for diagnostics. Crossfade duration 1.5s (NOT in the cache key — render-time-only); past window end → MissCrossfadeOut + the consumer falls back to standalone Stages 1+2+3+4. Recursion guard short-circuits primaries (they always render standalone — §6.5).
- ~~Phase 5: `ParsekFlight.InterpolateAndPosition` integration.~~ New `allowCoBubbleBlend` gate fires AFTER the standalone Stages 1+2+3+4 path; on hit, the helper `TryComputeStandaloneWorldPositionForRecording` evaluates the primary's standalone position from the primary's RECORDING (HR-15: never reads live KSP state) and replaces the peer's world position with `primaryWorld + recordedOffset`. Per-second `RecordCoBubbleEvalForLogging` summary mirrors the Phase 1 spline-eval-summary contract.
- ~~Phase 5: `useCoBubbleBlend` rollout flag (default on).~~ Property + persistence wired alongside `useSmoothingSplines` / `useAnchorCorrection` / `useAnchorTaxonomy` in `ParsekSettings` and `ParsekSettingsPersistence`. Flag participates in the `.pann` `ConfigurationHash` (offset 52, length grew 52 → 53) so flag flips invalidate cached `.pann` files via `config-hash-drift` (HR-10 freshness — without the hash key, a `.pann` written when the flag was off would cache-hit a flag-on session and the `CoBubbleOffsetTraces` block would never re-emit until manually deleted or alg-stamp bumped).
- ~~Phase 5: `.pann CoBubbleOffsetTraces` block populated.~~ Per-entry schema: peerRecordingId (length-prefixed UTF-8) + peerSourceFormatVersion (int32) + peerSidecarEpoch (int32) + peerContentSignature (32 bytes) + startUT/endUT (double, double) + frameTag (byte) + sampleCount (int32) + uts/dx/dy/dz arrays + primaryDesignation (byte). Per-block cap `MaxCoBubbleTraceEntries = 1000`; per-trace UT-array cap `MaxCoBubbleSamplesPerTrace = 100_000` (≈ 4 Hz × 600s ceiling × 40× headroom). `AlgorithmStampVersion` v4→v5 so existing v4 `.pann` files are discarded and recomputed on first load (HR-10).
- ~~Phase 5: per-trace peer validation flow.~~ `SmoothingPipeline.ClassifyTraceDrift` checks (peer-missing / peer-format-changed / peer-epoch-changed / peer-content-mismatch) per-trace at load time; drift drops the affected trace only, not the whole `.pann` file. Whole-file drift (binary version, alg stamp, source epoch, source format, config hash) keeps the existing classifier path. Pipeline-CoBubble Info `Per-trace co-bubble invalidation` line emits per dropped trace.
- ~~Phase 5: in-game tests `Pipeline_CoBubble_Live` and `Pipeline_CoBubble_GhostGhost`.~~ Added to `Source/Parsek/InGameTests/RuntimeTests.cs` with `[InGameTest(Category = "Pipeline-CoBubble", Scene = GameScenes.FLIGHT)]`. Live test asserts the recorded offset is preserved within 0.05 m at the window midpoint; ghost-ghost test asserts deterministic primary selection (lower ordinal recording-id wins under HR-3) plus the per-window blender hit.

## Done — v0.9.1 Phase 5 review pass (P1-A through P3-A)

- ~~P1-A: `CoBubbleBlender.ResolveBodyForOffset` returned null in production.~~ The trace now persists `BodyName` (captured from the overlap window) and the production resolver walks `FlightGlobals.Bodies` by ordinal name match. On miss the blender returns a new `MissBodyResolveFailed` status with a Verbose `body-resolve-failed` log so the inertial→world lower no longer silently degrades to a no-op. `AlgorithmStampVersion` v5→v6 invalidates legacy traces lacking the field.
- ~~P1-B: trace math now lifts to inertial at recording time.~~ For FrameTag=1 traces `CoBubbleOverlapDetector.BuildTrace` lifts each peer-minus-primary world delta through `TrajectoryMath.FrameTransform.LiftOffsetFromWorldToInertial(delta, body, recordedUT)` so `LowerOffsetFromInertialToWorld(stored, body, playbackUT)` round-trips correctly across UT shifts. Without the lift the blender returned a stale offset pinned to the recording UT's body phase whenever playback UT differed.
- ~~P1-C: LateUpdate co-bubble override path.~~ New `GhostPosMode.CoBubble` carries `(peerRecordingId, primaryRecordingId, pointUT)` so `LateUpdate` re-evaluates `CoBubbleBlender.TryEvaluateOffset` + `TryComputeStandaloneWorldPositionForRecording` after each FloatingOrigin shift. The pre-fix `PointInterp` re-evaluation overwrote the primary+offset composition with the bare bracket lerp every late frame, producing visible per-frame flicker. Failure inside the LateUpdate `CoBubble` case falls through to the standalone path (HR-9) with a rate-limited `cobubble-late-update-fallback` Verbose.
- ~~P1-D: `TryComputeStandaloneWorldPositionForRecording` mishandled RELATIVE-frame primaries.~~ The helper now resolves the section's `referenceFrame` first; v6+ RELATIVE sections route through `ParsekFlight.TryResolveRelativeWorldPosition` (the helper that knows v6+ stores metre-scale dx/dy/dz in the lat/lon/alt fields), legacy v5 sections fall back to lat/lon/alt-as-degrees, OrbitalCheckpoint sections return false with a Verbose. Pre-fix the call landed `body.GetWorldSurfacePosition(metre, metre, metre)` which silently produced a sub-planetary primary position.
- ~~P2-A: detector accepted Active+Active overlaps.~~ KSP has exactly one focused vessel per scene at any UT, so two simultaneous Active sections come from different sessions (re-fly bridge), not co-bubble. Detector rejects the pair outright; only Active+Background or Background+Background are eligible.
- ~~P2-B: runtime per-trace peer validation.~~ `CoBubbleBlender.TryEvaluateOffset` re-checks `PeerSidecarEpoch` + `PeerSourceFormatVersion` against the live peer recording on every call. Drift returns the existing `MissPeerValidationFailed` enum value plus a §19.2 Stage 5 "Per-trace co-bubble invalidation" Info log; the SHA-256 content recompute stays load-time-only (too expensive for the per-frame path).
- ~~P2-C: window-enter Info logged regardless of crossfade phase.~~ The first-hit notify moved out of the non-crossfade else-branch so windows whose first sample lands inside the 1.5s crossfade tail still log the enter event. Dedup set in `RenderSessionState` continues to suppress re-emission when subsequent samples enter the steady region.
- ~~P2-D: crossfade-frame Verbose log.~~ Added a rate-limited `cobubble-crossfade peer={peer} primary={primary} blend={blend:F3}` Verbose so an operator can watch the blend factor decay (§19.2 Stage 5 row 3). Pre-fix the only crossfade signal in the log was the exit Info — fine for postmortem but useless for live tuning.
- ~~P2-E: primary-selection log carries the deciding rule index.~~ `CoBubblePrimarySelector.SelectPrimaryForPair` now returns the §10.1 rule index (1=live, 2=DAG-hops, 3=earlier-StartUT, 4=higher-sample-rate, 5=ordinal-id) and `RenderSessionState.NotifyCoBubblePrimarySelection` includes it as `rule={N}` in the dedup'd Info line.
- ~~P2-F: `CoBubblePrimarySelector` gates on `useCoBubbleBlend`.~~ `Resolve` early-returns an empty map with a `flag-off-skip-primary-resolve` Verbose when the flag is off. Pre-fix a save with stored traces loaded by a flag-off user got primaries assigned, which then triggered the blender's recursion guard for the wrong reason.
- ~~P3-A: `RenderSessionState.IsPrimary(recordingId)` is now O(1).~~ Parallel `PrimaryRecordingIdsInternal` HashSet maintained alongside `PrimaryByPeerInternal` so the recursion-guard hot path costs O(1) instead of O(N) per peer ghost per frame. Cleared together everywhere the dictionary is cleared; `PutPrimaryAssignmentForTesting` removes a primary id from the set only when no other peer still references it.
- ~~Phase 5 review pass: missing rule 2/3/4 + snapshot freezing tests.~~ Added `PrimarySelection_Rule2_ClosestToLiveInDagWins`, `PrimarySelection_Rule3_EarlierStartUTWins`, `PrimarySelection_Rule4_HigherSampleRateAtMidpointWins`, and `SnapshotFreezing_LivePrimaryOffsetIndependentOfRuntime` to `CoBubbleBlenderTests`. The structural-event split tests (Dock/Undock/EVA/JointBreak) called out in plan §9.1 are deferred — they overlap with Phase 6 anchor-taxonomy structural-event coverage.

## Done — v0.9.1 Phase 5 review pass 2 (P1-A through P2-B)

- ~~P1-A: scenario-load drop of valid same-tree co-bubble traces.~~ `ParsekScenario` OnLoad hydrates each recording's sidecars (and runs `SmoothingPipeline.LoadOrCompute`) BEFORE `FinalizeTreeCommit` appends the tree to `RecordingStore.CommittedRecordings`. The pre-fix per-trace peer validator walked `CommittedRecordings` only, so every same-tree peer was invisible at the moment its sibling's traces were validated and the loader dropped every valid same-tree trace as `peer-missing` on every save load. Fix: thread an optional `treeLocalLoadSet` (the in-progress tree's `Recordings` map) through `RecordingStore.LoadRecordingFiles` → `RecordingSidecarStore.LoadRecordingFiles` → `SmoothingPipeline.LoadOrCompute` → `ClassifyTraceDrift` → `ResolvePeerRecording`. The resolver consults the load set first, falling back to `CommittedRecordings` when the load set is null (production calls outside scenario-load) or doesn't contain the peer (cross-tree peers). Two new xUnit tests in `CoBubbleSidecarRoundTripTests` pin: same-tree peer not yet committed validates correctly via the load set; null load-set entries fall back to `CommittedRecordings`.
- ~~P1-B: lazy recompute wrote empty `CoBubbleOffsetTraces` blocks.~~ The recompute path in `SmoothingPipeline.LoadOrCompute` (file-missing / version-drift / alg-stamp-drift / config-hash-drift / epoch-drift / format-drift / payload-corrupt) called `FitAndStorePerSection` only and then wrote a fresh `.pann` with no co-bubble traces. Saves crossing an `AlgorithmStampVersion` bump or `useCoBubbleBlend` flag flip silently fell back to standalone playback until every recording was recommitted. Fix: the recompute path now calls `CoBubbleOverlapDetector.DetectAndStore` against a snapshot composed of the tree-local load set + `CommittedRecordings` + `rec` itself, so any pair of recordings that overlapped at recording time produces traces in the regenerated `.pann`. Wrapped in a try/catch with a `Pipeline-CoBubble` Warn so a detector exception cannot abort recording load (HR-9). New xUnit test `LoadOrCompute_DriftedAlgStamp_RecomputesCoBubbleTraces` writes a stale-alg-stamp `.pann`, calls `LoadOrCompute` with two overlap-eligible recordings in the load set, and asserts the rewritten `.pann` contains a trace for the pair.
- ~~P1-C: active re-fly RELATIVE primary read live anchor (HR-15 violation).~~ `TryComputeStandaloneRelativeWorldPosition` always routed v6+ RELATIVE sections through the live-anchor resolver (`TryResolveRelativeWorldPosition`). For a co-bubble primary that IS the active re-fly recording, the live anchor is the player's controls — the primary ghost would be dragged with player input ("Naive Relative Trap" §3.4), exactly the bug Phase 5's HR-15 contract was designed to prevent. Fix: after the anchor-pid guard, the helper resolves the active re-fly target's vessel persistent ID via a new static helper `ParsekFlight.TryResolveActiveReFlyPidStatic` (mirrors the existing instance method); when `section.anchorVesselId == activeReFlyPid` the helper routes through `TryComputeStandaloneAbsoluteShadowWorldPosition` (linear-interpolates the section's `absoluteFrames` shadow + `body.GetWorldSurfacePosition`). On no-shadow (legacy pre-v7 RELATIVE recordings) the helper fails closed with a rate-limited `primary-active-refly-no-shadow` Verbose so HR-9 surfaces the degradation. The active-re-fly branch runs BEFORE the `Instance` null-check so the absolute-shadow path is reachable from xUnit (no `ParsekFlight.Instance` standup needed). Two new tests in `CoBubbleStandalonePrimaryTests`: PID-resolver round-trip and the active-re-fly absolute-shadow dispatch (asserts no `Instance null` log fires when the active-re-fly anchor matches).
- ~~P2-A: peer-side co-bubble traces persisted memory-only.~~ `CoBubbleOverlapDetector.DetectAndStore` stored traces in BOTH sides of every overlap pair, but `SmoothingPipeline.PersistAfterCommit(rec)` only wrote `rec.pann`. After reload, only the just-committed side had the trace persisted; if the next session's selector designated the peer as primary, the blender found no trace and the window silently degraded to standalone. Fix: `PersistAfterCommit` now collects every recording in the snapshot whose in-memory store gained a trace pointing at `rec`, then calls a new `PersistPeerPannFiles` helper that resolves each peer's `.pann` path via `RecordingPaths.BuildAnnotationsRelativePath` + `ResolveSaveScopedPath` and writes via `TryWritePann`. Each peer write is wrapped independently and surfaces as a Warn on failure (HR-9 / HR-12: peer-side I/O failure must not abort the active commit). New xUnit test `PersistAfterCommit_PeerPannFiles_AlsoUpdated` validates the in-memory peer trace + the `PersistPeerPannFiles summary peerCount=1` log.
- ~~P2-B: long overlap windows ignored `BlendMaxWindowSeconds`.~~ `BlendMaxWindowSeconds` participates in the `.pann` `ConfigurationHash` and is documented as the per-trace duration cap, but `BuildTrace` only capped via `MaxCoBubbleSamplesPerTrace` and kept `window.EndUT` unchanged. Long overlaps (e.g. an N-orbit formation flight) either emitted huge traces or hit the sample cap mid-window leaving `EndUT` covering UTs without sample coverage — the blender would clamp to the last offset for the rest of the window, producing visually wrong results. Fix: `BuildTrace` now clamps `effectiveEndUT = min(window.EndUT, window.StartUT + BlendMaxWindowSeconds)`, uses the clamped end for both the sample loop and the trace's persisted `EndUT`/peer-content-signature window, and emits a rate-limited `window-clamped-to-max` Verbose per HR-9. Splitting an over-cap window into multiple traces is a Phase 5 follow-up, not a P2 must-fix. Two new xUnit tests in `CoBubbleOverlapDetectorTests`: window > cap clamps to `StartUT + cap`; window < cap round-trips unchanged.
- ~~Review pass 2: `AlgorithmStampVersion` v6→v7.~~ Both P1-B (recompute regenerates traces) and P2-B (window clamping changes trace contents) change cached `.pann` semantics. v6 files written before these fixes have semantically incorrect co-bubble blocks; bumping the alg stamp drives them through `alg-stamp-drift` on first load (HR-10). **Coordination note for Phase 8 (PR #644):** Phase 8 also bumped to 7 in commit `8b9f623d` for `OutlierFlagsList`; after Phase 5 lands at 7, Phase 8's rebase fixup needs to bump to 8. The Phase 5 stack is at the foundation; Phase 8 stacks on top.

## Done — v0.9.1 Phase 5 review pass 3 (P1-A leg + P2-1 + P2-2 + P3-1 + P3-2 + P3-3)

- ~~P1-A peer-content-signature leg: even after threading the load set through `ResolvePeerRecording`, the signature recompute still ran against `peer.Points`.~~ During OnLoad each recording's `.prec` is deserialized sequentially, so same-tree peers iterated AFTER the current recording have `peer.Points` empty when the current recording's `.pann` is validated. Recomputing SHA-256 over an empty Points list mismatches the stored signature and drops the trace as `peer-content-mismatch` on every load — the FIRST recording iterated in `tree.Recordings.Values` always lost its co-bubble traces silently. The two new tests from review pass 2 didn't catch it because both installed `CoBubblePeerSignatureRecomputeForTesting` / `CoBubblePeerResolverForTesting` seams that bypass the production code path. Fix: `ClassifyTraceDrift` now returns null with a `peer-content-validation-deferred` Verbose when `peer.Points == null || peer.Points.Count == 0`. Format / epoch checks still run from `.sfs` data; signature validation defers to the runtime per-trace check in `CoBubbleBlender` (P2-B from review pass 1) which sees both sides fully hydrated. New xUnit test `LoadOrCompute_PeerContentSignature_PeerStillHydrating_DefersValidation` exercises the production code path with a stub peer whose Points list is empty and a mismatching signature seam — pre-fix returns `peer-content-mismatch`, post-fix returns null and logs the deferred message.
- ~~P2-1: `idx <= 0` past-end clamp regression in three sibling helpers.~~ `TryComputeStandaloneAbsoluteShadowWorldPosition`, `TryComputeStandaloneAbsoluteFallbackWorldPosition`, and `TryComputeStandaloneWorldPositionForRecording`'s lat/lon path all used `idx <= 0` to detect both at-or-before-start (`idx == 0`) and past-end (`idx == -1`). When `ut > sample[Count-1].ut`, the loop completes without break and idx stays -1; the pre-fix code clamped to `sample[0]`, producing an early-recording position when the caller asked for a late one — a silent time jump. Fix: distinguish the two cases. `idx == -1` fails closed with a rate-limited Verbose (`primary-active-refly-shadow-past-end` / `standalone-fallback-past-end` / `standalone-absolute-past-end`); `idx == 0` keeps the existing at-or-before-start clamp. New xUnit test `StandaloneAbsoluteShadow_UTPastEnd_FailsClosed` drives the bug case end-to-end through the active-re-fly absolute-shadow path with a section spanning [10, 50] but absoluteFrames ending at 30, queried at ut=35.
- ~~P2-2: `StandaloneWorldPosition_RelativeFrameActiveReFlyPrimary_UsesAbsoluteShadow` test missed the positive no-shadow Verbose assertion.~~ The prior test asserted the `Instance null` log does NOT fire but never asserted the canonical no-shadow Verbose DOES fire when the body resolver returns null in xUnit. A future refactor that drops the no-shadow Verbose still passed. Fix: added the missing positive assertion against the canonical message text.
- ~~P3-1: lazy recompute populated peer in-memory traces but did not write peer's `.pann`.~~ When `LoadOrCompute` recomputed for `recA`, `DetectAndStoreCoBubbleTracesForRecording` wrote traces into BOTH sides' in-memory stores, but `TryWritePann(recA, ...)` wrote only `recA.pann`. P2-A's eager peer-side persistence ran only at commit time (`PersistAfterCommit`), not at lazy recompute. After an alg-stamp drift bump every recording's `LoadOrCompute` would eventually self-rewrite, but until both sides had iterated, `.pann` state was asymmetric. Fix: `DetectAndStoreCoBubbleTracesForRecording` now returns the list of peers touched by the recompute, and `LoadOrCompute` calls `PersistPeerPannFiles(rec, recomputePeerPersist, expectedHash)` mirroring `PersistAfterCommit`'s behaviour. New xUnit test `LoadOrCompute_LazyRecompute_PeerPannSymmetricallyUpdated` writes a stale-alg-stamp `.pann`, triggers lazy recompute with two overlap-eligible recordings in the load set, and asserts the peer `.pann` file exists on disk under the seam path with a trace pointing at the active recording.
- ~~P3-2: `PersistAfterCommit_PeerPannFiles_AlsoUpdated` didn't verify actual file write.~~ The prior test acknowledged in a comment that `RecordingPaths.ResolveSaveScopedPath` returns null in xUnit, so `PersistPeerPannFiles` always took the `skipped` path; the test only verified in-memory state + the summary log, not actual disk writes. Fix: added `SmoothingPipeline.PeerPannPathResolverForTesting` test seam that, when set, replaces the production resolver. The test now points peer writes at a temp directory, asserts the peer `.pann` file actually exists on disk, probes + reads it, and verifies the persisted trace contains the right peer ID. The summary log assertion now pins `persisted=1` (was previously consistent with `skipped=1` in xUnit). The seam is reset in `ResetForTesting` per `[Collection("Sequential")]` discipline.
- ~~P3-3: `ClassifyTraceDrift_TreeLocalLoadSet_NullPeer_StillFallsBackToCommitted` tested the wrong path.~~ The test installed `CoBubblePeerResolverForTesting` to return a valid peer; `ResolvePeerRecording` short-circuits to that seam BEFORE the load-set check, so the load-set null-value fallback was never executed. The test passed for the wrong reason. Fix: leave the resolver seam null, populate the load set with a `null` value, populate `RecordingStore.CommittedRecordings` directly with the peer (the canonical fallback target), and assert the resolution succeeds via the committed-recordings walk. The signature seam still matches because the committed peer has non-empty Points.

## Done — v0.9.1 Phase 5 review pass 4 (deferred-validation post-hydration sweep)

- ~~Review-pass-3 P1-A deferral leaked stale traces past OnLoad: `ClassifyTraceDrift` returned null when `peer.Points` was empty (canonical "deferred to runtime") but the runtime per-trace check in `CoBubbleBlender.cs:152, 162` only validates `PeerSourceFormatVersion` and `PeerSidecarEpoch` — it does NOT recompute the signature.~~ A same-tree peer that hydrated to different points than at commit time (partial hydration failure, save-edit-without-epoch-bump, etc.) had its stale trace stay installed for the entire session. Fix: enqueue a `DeferredCoBubbleValidation` entry whenever the empty-`peer.Points` deferral fires, then drain the queue in a post-tree-hydration sweep `SmoothingPipeline.RevalidateDeferredCoBubbleTraces(tree.Recordings)` invoked from both `ParsekScenario.OnLoad` call sites (committed trees + active tree). The sweep recomputes the signature against the now-populated peer and drops mismatching traces with a `Pipeline-CoBubble` Info log carrying `reason=peer-content-mismatch`; peers that still have empty Points (sidecar load failed entirely) drop with `reason=peer-still-not-hydrated`. The sweep is drained-as-it-processes so a second call after a tree's recordings have loaded is a no-op (idempotent). New `SectionAnnotationStore.RemoveCoBubbleTrace(recordingId, peerRecordingId, startUT, endUT)` helper for the targeted single-trace removal. New `SmoothingPipeline.DeferredCoBubbleValidationsCountForTesting` test seam so xUnit can assert the deferral was actually enqueued. Three new xUnit tests in `CoBubbleSidecarRoundTripTests`: peer hydrates to different points → trace dropped; signature matches → trace kept; peer never hydrates → trace dropped with the still-not-hydrated reason. Existing `LoadOrCompute_PeerContentSignature_PeerStillHydrating_DefersValidation` test extended to assert the deferred-set count goes from 0 to 1.

## Done — v0.9.1 Phase 5 review pass 5 (P1 offset-sign fix)

- ~~`CoBubbleOverlapDetector.DetectAndStore` stored both sides of every overlap pair with reversed-sign offsets.~~ The blender's contract is: a trace stored under recording X with `PeerRecordingId = Y` supplies `worldOffset` such that `X_world = Y_world + offset`, i.e. `offset = X − Y`. `BuildTrace`'s output uses the parameter name `peer` for "the recording the offset is computed against" (i.e. the OFFSET'S peer), and produces `Dx = peer − primary` with `PeerRecordingId = peer.RecordingId`. The pre-fix `CloneTraceWithPeer` then tried to derive both stored sides from a single `BuildTrace` output by flipping the offset and rewriting the peer-id, but its flip condition was exactly inverted — it kept the offset as-is when the trace's peer-id matched the storage-side's intended peer (which produced `Dx = primary − owner` instead of `owner − primary`), and flipped only when rewriting the peer metadata for the OTHER storage side (also wrong). Both stored sides ended up with reversed-sign offsets — peer ghosts rendered on the opposite side of the primary at the offset's distance. The two existing in-game tests (`Pipeline_CoBubble_Live`, `Pipeline_CoBubble_GhostGhost`) bypassed `DetectAndStore` and injected traces directly via `PutCoBubbleTrace` / synthetic stub resolvers, so the regression hid; no xUnit verified the round-trip from `DetectAndStore` → blender → ghost world position with non-zero offset. Fix: replace `CloneTraceWithPeer` with two separate `BuildTrace` invocations (one per storage side, with the role parameters swapped so each side's `Dx = peer − primary` matches the storage convention `Dx = owner − primaryRef`), followed by a small `RebrandTraceForPrimary` helper that overwrites the trace's `PeerRecordingId` / `PeerSourceFormatVersion` / `PeerSidecarEpoch` / `PeerContentSignature` to name the primary side. Each `BuildTrace` call independently produces the correct offset AND the correct signature; the rebrand step bridges `BuildTrace`'s naming gap (where "peer" = "the offset's peer") to the blender's storage naming (where `PeerRecordingId` = "the primary the offset references"). Cost: 2× world-position lookups per overlap window at commit time — bounded and acceptable per HR-3. `AlgorithmStampVersion` bumped 7 → 8 so existing v7 `.pann` files (which carry the wrong-sign offsets) are discarded and recomputed on first load (HR-10). New xUnit test `DetectAndStore_OffsetSign_RoundTripsThroughBlenderToCorrectPeerWorldPosition` in `CoBubbleOverlapDetectorTests` exercises the full round-trip: detector emits both sides; blender reads each side back; offset reproduces the recorded peer world position when added to the primary's world position. New alg-stamp pin `AlgStampVersion_BumpedToEightOrLater_ForOffsetSignFix` in `CoBubbleSidecarRoundTripTests`.

## Phase 8 stack coordination (PR #644)

Phase 8 PR #644 was rebased onto Phase 5 tip `83bef832` so the branch now carries every Phase 5 review-pass fix (passes 2-5). The two reviewer findings on PR #644 that overlapped with Phase 5 are resolved by virtue of the rebase pulling in the Phase 5 fixes directly:

- "Lazy `.pann` recompute never rebuilds co-bubble traces" — resolved by the rebase pulling in Phase 5 review pass 3 P3-1 (`9de806f6`): lazy recompute calls `DetectAndStoreCoBubbleTracesForRecording` and persists peer `.pann` files symmetrically.
- "`BlendMaxWindowSeconds` not enforced" — resolved by the rebase pulling in Phase 5 review pass 2 P2-B (`e2369a5e`): `BuildTrace` clamps `effectiveEndUT = min(window.EndUT, window.StartUT + cap)` and emits the `window-clamped-to-max` Verbose.
- `AlgorithmStampVersion`: Phase 5 tip is at 8 (review-pass-5 P1 offset-sign fix), and the Phase 8 commits bump 8 → 9 to invalidate Phase-5-tip `.pann` files lacking populated `OutlierFlagsList` entries. `CanonicalEncodingLength` stays at the Phase-8 value (86); Phase 5 didn't grow the hash payload, only reused already-reserved bytes. Co-Bubble + Outlier blocks coexist in the same `.pann` schema unchanged.

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

## 633. Ladders rendered extended in ghost when recorded vessel had them stowed

**Status:** ~~done~~ — fix landed on `claude/fix-ladder-state-bug-2cQL1`.

Stock retractable ladders showed up extended in the ghost snapshot even when
the recording started with them stowed. The recorder side was correct:
`PartStateSeeder.SeedLadders` only seeds deployed ladders into the
`deployedLadders` set, so a stowed ladder produces no `DeployableExtended`
seed event at recording start, and per-frame transitions during the recording
correctly emit `DeployableExtended` / `DeployableRetracted` events from
`FlightRecorder.CheckLadderState`. The toggle-action recording works.

The bug lived in `GhostPlaybackLogic.PopulateGhostInfoDictionaries`
(`GhostPlaybackLogic.cs:1062`): it built `state.deployableInfos` from the
build result but never explicitly snapped each entry to its stowed pose.
The ghost therefore inherited the prefab's default pose. Stock ladders are
authored in the deployed pose so the prefab default IS extended; without a
seed event no playback handler ever fires to retract the ladder, and the
ghost stayed visibly extended for the whole recording.

The same `state.deployableInfos` is already explicitly re-stowed at every
loop boundary by
`GhostPlaybackLogic.ReapplySpawnTimeModuleBaselinesForLoopCycle`
(`GhostPlaybackLogic.cs:1562-1569`), so the loop-cycle path was correct —
only the first-spawn path was missing the baseline. Fix: add a stow
baseline immediately after the dict build, mirroring the heat-info
cold-baseline pattern earlier in the same method
(`GhostPlaybackLogic.cs:1119-1126`). For each entry, call
`ApplyDeployableState(state, evt, deployed: false)`. Already-deployed
deployables get a `DeployableExtended` seed event at `startUT` from
`PartStateSeeder.EmitSeedEvents`, so the playback frame loop snaps them
back to deployed when it reaches the recording start. Fix covers all
deployables that route through `DeployableGhostInfo` — solar panels, gear,
ladders, animation groups, animate-generic, aero/control surfaces, robot
arm scanners — not just ladders. Showcase recordings flow through the same
spawn path so they benefit too.

Regression tests in `GhostSpawnDeployableBaselineTests.cs` pin: (a) the
baseline log fires when `deployableInfos.Count > 0`; (b) it does not fire
when `deployableInfos` is null or empty (no log noise); (c) the dict is
keyed by `partPersistentId` so seed events can find the matching info; (d)
defensive null-transform handling in `ApplyDeployableState` (unresolved
ghost paths must not NRE the spawn baseline).

## ~~632. Optimizer meaningful-action gate broke per-phase loop splits~~

**Status:** ~~done~~. PR #625 (the broken meaningful-action gate) was reverted
in PR #628. The persistence-based redesign shipped in this PR. Both the
player-facing per-phase loop split AND the eccentric-grazing chain-bloat
suppression now hold simultaneously. Plan:
[`docs/dev/plans/optimizer-persistence-split.md`](plans/optimizer-persistence-split.md).

History — what went wrong with PR #625:

PR #625 added a meaningful-action gate that suppressed pure Atmospheric↔Exo
and Approach↔Exo boundaries unless a thrust / decoupling / parachute / gear /
thermal `PartEvent` landed within ±5 s of the split UT
(`MeaningfulBoundaryWindowSeconds = 5.0`). Intent: stop eccentric atmo-
grazing periapsis passes from producing 2N chain segments per N orbits
(`extending-rewind-to-stable-leaves.md` §S16). The gate broke two extremely
common gameplay phases:

- **Passive deorbit reentries.** Engines off well before 70 km, parachutes
  deploy at ~5 km, `ThermalAnimationMedium` fires at `normalizedHeat ≥ 0.40`
  which usually only happens deep in atmo (>5 s after the 70 km crossing).
  The reentry segment glued to the orbital coast and lost its own loop toggle.
- **Staged ascents with a coast through 70 km.** First stage cuts off at
  ~50 km, vessel coasts through 70 km, circularization burn at apoapsis
  (~80 km). The `EngineShutdown` and circularization `EngineIgnited` events
  both fall outside the ±5 s window. The ascent segment glued to the orbital
  coast.

A rare focused-flight grazing case (the on-rails case is already structurally
guarded by `EccentricOrbitOptimizerInvariantTests`) was fixed at the cost of
breaking the player-facing per-phase loop split that
`docs/parsek-flight-recorder-design.md` §9A.5 codifies as the whole point of
`SplitEnvironmentClass`. Reverted in PR #628.

What this PR ships:

- **Persistence-based discriminator** in
  `RecordingOptimizer.FindSplitCandidatesForOptimizer` (§3 / §3.1 of the
  plan). For boundaries that aren't already meaningful via body change /
  Surface / ExoPropulsive short-circuits, the predicate suppresses an
  Atmo↔Exo* boundary iff one side is a brief (< 120 s) run that's bracketed
  by the same env class on the other side. Real ascents and reentries
  (sustained transitions to/from the new env class) keep splitting per phase.
  Eccentric grazing, single aerobrake passes, Karman-line tourist hops up to
  ~150 km apogee collapse to one segment.
- **Collapse-walk on `SplitEnvironmentClass` runs** (§3.2 of the plan). The
  bracket lookup walks through same-split-class adjacent sections — handles
  ExoBallistic↔ExoPropulsive thrust toggles AND forced section breaks (vessel-
  switch seam, source-change forced break) that produce same-raw-env adjacent
  sections. Single-step lookup would misclassify these; the collapse-walk
  handles them uniformly.
- **`TrackSection.isBoundarySeam` flag** (§5 of the plan).
  `BackgroundRecorder.FlushLoadedStateForOnRailsTransition` sets the flag on
  the no-payload boundary section it emits at the loaded→on-rails transition.
  The optimizer skips boundaries on either side of a flagged section as a
  hard step-1 override, ahead of body / Surface / ExoPropulsive short-
  circuits. Persisted in both text codec (sparse `seam=1` key) and binary
  codec (mandatory v8 format bump from `RelativeAbsoluteShadowFormatVersion`
  to `BoundarySeamFlagFormatVersion`).
- **30 tests** (22 unit + 3 integration through `RunOptimizationPass` + 4
  serialization round-trip + 1 in-game smoke). Cover each §3 short-circuit,
  the §3.1 collapse-walk on both mechanisms it solves, the §3.3 edge-of-
  recording fall-through and its seam-flag override, the strict `<` cumulative
  K boundary, accept-side discriminator logging, and the per-recording
  aggregate suppression-counter log.

Player-visible effects:

- Passive deorbit reentry / staged ascent with a coast across 70 km: per-
  phase chain segments preserved (regression from PR #625 stays fixed).
- Eccentric atmo-grazing focused recordings: stay as one chain segment
  instead of fragmenting per Pe pass.
- Single aerobrake pass: one segment.
- Karman-line tourist hop ≤ ~150 km apogee: one segment. > ~150 km: Exo phase
  becomes its own segment.
- Aborted Mun landing (Exo → Approach[< 120 s] → Exo, no touchdown): folds
  to one segment. Players can use the manual split UI if they want it
  separated.
- Existing recordings written before this PR: legacy chains are unchanged
  (`CanAutoMerge` phase-equality gate prevents retroactive merging). Producer-
  C seams written under old code load with `isBoundarySeam=false` and may
  still produce one extra chain segment on first load — same behaviour as
  pre-PR, no regression. New recordings under this PR carry the flag and the
  seam never produces a split.
- Binary `.prec` sidecars bumped from v7 to v8. Old `Parsek` versions cannot
  read recordings produced by this version. New code reads both v7 and v8
  files (default-false for the seam flag on v7).

## ~~629. Multi-stage crash showed only one half in Unfinished Flights (effective-leaf finalize)~~

Repro: `logs/2026-04-27_2157_stage-separation-bugs/KSP.log`. LU stack
launched (recording `34757abf...`, vessel `Kerbal X`, pid 2708531065). Stage
separation at BP `bc780859...` carved off the L probe as a new child recording
`b4b0470e...` with a DIFFERENT pid (334653631). Both halves crashed; only
`b4b0470e` was promoted to `CommittedProvisional`. The original `34757abf`
stayed in the default state with `TerminalStateValue=null` and disappeared
from the Unfinished Flights list. Smoking gun: `[Flight] FinalizeTreeRecordings:
rec='34757abf...' ... terminal=none ... leaf=False` despite `FinalizerCache`
having `terminal=Destroyed` available.

Root cause: `FinalizeIndividualRecording` decided "is leaf?" with the strict
`bool isLeaf = rec.ChildBranchPointId == null;` predicate. A recording whose
BP child has a different PID is still the effective continuation of its own
PID (the U side of the LU split), but the strict check treated it as a non-
leaf and skipped the entire terminal-state determination block. The codebase
already had `GhostPlaybackLogic.IsEffectiveLeafForVessel(rec, tree)` for this
exact case (added in #224 for breakup-continuous recordings), but
`FinalizeIndividualRecording` was never updated to consult it.

Fix: `FinalizeIndividualRecording` now takes an optional `RecordingTree
treeContext = null` parameter and computes `isLeaf` as `rec.ChildBranchPointId
== null || GhostPlaybackLogic.IsEffectiveLeafForVessel(rec, treeContext)`.
`FinalizeTreeRecordingsAfterFlush` threads its `tree` argument through, and
`FinalizePendingLimboTreeForRevert` passes the limbo `tree` so the lookup
works even when the tree isn't yet in `RecordingStore.CommittedTrees`. All
existing leaf-gated blocks (snapshot re-capture, terminal orbit refresh, no-
playback-data warning) inherit the broader definition because the intent
everywhere is "this recording's vessel has reached its terminal state".

## ~~630. Launch row dropped its Rewind-to-launch ("R") button when a sibling chain segment was an Unfinished Flight~~

Repro: launched `Kerbal X` (launch 2 in the session), separated stages, the
booster continuation crashed. Back at KSC the launch row showed neither R nor
Rewind-to-Staging — the player had no rewind action on the launch. Launch 1's
launch row in the same session DID show R because its tree was never split
into chain segments by the optimizer (no clean atmo→exo boundary before the
crash).

Tree shape from logs:

```
HEAD chainIndex=0  rec_5ca2cac9  Kerbal X            (owns rewindSave parsek_rw_ab2e3b)
TIP  chainIndex=1  rec_6bb1973f  Kerbal X (cont.)    terminal=Destroyed, parentBP=bp_LU
```

Root cause: `RecordingsTableUI.ShouldShowLegacyRewindButton` suppressed the R
button when `EffectiveState.IsChainMemberOfUnfinishedFlight(rec)` returned
true. That predicate scans every member of the chain — so the HEAD (the
launch row) tripped the suppression because the destroyed TIP qualified as an
Unfinished Flight, even though the HEAD itself has no `ParentBranchPointId`
and would never draw the Rewind-to-Staging button.

Fix: Replaced the chain-wide check with `EffectiveState.IsUnfinishedFlight(rec)`
so the R button is suppressed only on the row that is itself an unfinished
flight (which gets Rewind-to-Staging instead via
`DrawUnfinishedFlightRewindButton`). The chain HEAD keeps R-to-launch even
when a sibling TIP is the unfinished flight; the chain TIP still draws
Rewind-to-Staging and the legacy R is correctly hidden there. Updated the
helper's doc comment and the call-site comment in `DrawTreeMainRowRewindCell`
to spell out the new semantics. Added regression tests in
`RewindTreeLookupTests`:

- `ShouldShowLegacyRewindButton_ChainHeadWithUnfinishedTip_ReturnsTrue`
  (asserts both `head→true` and `tip→false` for the bug's exact tree shape).
- `ShouldShowLegacyRewindButton_StandaloneCrashedRecording_StillReturnsTrue`
  (inverse-regression: a plain crashed standalone with its own save still
  gets R).

The pre-existing `ShouldShowLegacyRewindButton_ChainMemberOfUnfinishedFlight_ReturnsFalse`
still passes — its head carries `ParentBranchPointId` AND has a destroyed
chain tip, so `IsUnfinishedFlight(head)` returns true and R is correctly
suppressed.

## ~~631. Re-Fly hides side-off vessels (e.g. previous lower stage) for the entire session~~

Repro logs: `logs/2026-04-27_2157_stage-separation-bugs/KSP.log` lines around
18045-18047. Re-Fly origin recording `5ca2cac9d7b24b87a0a4367a5929c0e7`
("Kerbal X" upper stage U, pid 2708531065) carries `ChildBranchPointId =
ff480fe7…` (the LU separation BP). That BP has two children: the same-PID
linear continuation of U, AND `d252fa498a5c476abb852f66d57f0f02` ("Kerbal X
Probe", pid 3295431853 = lower stage L) which is the side-off booster from the
original separation. L runs on its own ChainId, reached orbit, and is a
stand-alone branch. While re-flying U, the user expects L's ghost to keep
playing so they can watch the original booster do its thing — instead L
disappears from flight, map view, and ghost playback for the entire re-fly
session because the SessionSuppressedSubtree closure was treating every
BP-child as suppressed.

Root cause: `EffectiveState.ComputeSessionSuppressedSubtreeInternal`'s
BP-children loop walked every child of every BP encountered. Side-off
branches (children whose `VesselPersistentId` differs from the parent
recording's PID) are separate physical vessels with their own lineage; the
re-fly only re-records the same-PID linear continuation and cannot
legitimately supersede sibling branches with different PIDs. Same-PID
continuations across chains were already handled separately by
`EnqueueChainSiblings` and `EnqueuePidPeerSiblings`.

Fix: restrict the BP-children walk to children whose `VesselPersistentId`
matches the dequeued recording's `VesselPersistentId`. Side-off children are
skipped with a verbose `[ReFlySession] SessionSuppressedSubtree: skipped
side-off …` log line; the summary log gains a `sideOffSkips=N` counter.
PID 0 on EITHER side falls back to the prior wide-walk behavior so legacy /
unset-PID data is unchanged. The fewer supersede rows / fewer kerbal-death
tombstones produced at merge time are the correct outcome — the re-fly does
not supersede side-offs; the new flight will produce its own side-offs at
its own future staging events and supersede the old ones at that moment.

Regression coverage in `SessionSuppressedSubtreeTests`:
`BpChildrenWalk_SidePidChild_Excluded`,
`BpChildrenWalk_DownstreamOfSideOff_AlsoExcluded`,
`BpChildrenWalk_BothPidsZero_LegacyWideWalk_Preserved`,
`BpChildrenWalk_OriginPidZero_ChildPidNonZero_AdmittedAsLegacy`,
`BpChildrenWalk_OriginPidNonZero_ChildPidZero_AdmittedAsLegacy`. The two
`SessionSuppressionWiringTests` fixtures that incidentally used different
PIDs for origin/inside descendants were updated to use the same PID so they
still exercise the linear-continuation path the closure now scopes to.
## ~~632. Pipeline Phase 1: per-frame spline-eval summary log line L4 deferred~~ done

`ParsekFlight.cs` (around `InterpolateAndPosition` near `:14868`) carries a
`// TODO Phase 1: per-frame spline-eval summary log L4` comment for the
per-frame `Pipeline-Smoothing` `VerboseRateLimited` summary of spline
evaluations (L4 in the design doc §19.2 logging table). Cleanly placing the
counter requires a per-frame counter-reset hook in `GhostPlaybackEngine`
(matching the existing `GhostPlaybackEngine` frame batch counters pattern, per
`.claude/CLAUDE.md` "Batch counting convention"), which is outside Phase 1's
scope. Tracked here so it isn't lost; non-blocking for Phase 1 functional
behaviour. The orchestrator-side L1 / L3 / L5 / L7 / L8 / L9 / L11 lines all
ship in Phase 1 and are pinned by `SmoothingPipelineLoggingTests`; only the
hot-path L4 line is deferred.

---

## ~~627. Watch-mode cutoff false-positive during time warp (FloatingOrigin/Krakensbane frame seam)~~

Repro logs: `logs/2026-04-27_1902/KSP.log` line 208360 (timestamp 19:01:05.946),
~4-8x time warp.

```
[Zone] Ghost #0 "Kerbal X" exceeded ghost camera cutoff
(786169m from active vessel >= 305000m; render=772527m) — exiting watch mode |
ghostWorld=(-76794.73,-408.21,-26272.88) section=Absolute sectionUT=[124.0,226.8]
```

The new `ghostWorld=` field (added by PR #614 for diagnostics) shows the ghost
sat at floating-origin position (-76794, -408, -26272), magnitude ~81 km. Cross-
check: ghost #7 spawned 90 ms earlier at world=(-76197.99,-400.66,-25809.32)
and its appearance log correctly reported `dist=80568m` (~80 km). Both ghosts
were ~80 km from the active vessel, well inside the 305 km cutoff — but
`state.lastDistance` for ghost #0 read 786169 m and `renderDistance` read
772527 m, both ~10× the real distance. Watch mode auto-exited and the camera
snapped back to the active vessel.

Root cause: cached distance computed across a FloatingOrigin / Krakensbane
frame seam. `GhostPlaybackEngine.ResolvePlaybackActiveVesselDistance` (line
2328) and `ResolvePlaybackDistance` (line 2310) both compute
`Vector3d.Distance(worldPos, activeVesselPos)` once per frame and cache into
`state.lastDistance` / `state.lastRenderDistance`. During time warp, KSP can
advance the playback UT by multiple game seconds in a single frame and
re-position ghosts via fresh body-coord math while the active vessel's
`transform.position` is still in the pre-shift floating-origin frame. For one
frame the two values live in different floating-origin frames,
`Vector3d.Distance` picks up a phantom hundreds-of-km gap, the cutoff trips.
Same family as the `Krakensbane.GetFrameVelocity()` correction documented in
`.claude/CLAUDE.md` (KSP API & Code Gotchas).

Fix: 3-frame debounce on `WatchModeController` (approach 2 in spirit — a
"forced one-frame await" generalised to N frames). New constant
`WatchExitCutoffDebounceFrames = 3`, new instance method
`RegisterWatchCutoffSampleAndShouldExit(bool cachedCutoffTripped)` that
increments a `watchCutoffConsecutiveFrames` counter when the cached value
trips the cutoff and resets to 0 otherwise; returns true once the counter
reaches the threshold. Pure predicate
`ShouldExitWatchAfterCutoffDebounce(int)` exposed for tests. Counter resets
in both `ResetWatchEntryTransientState` and `ResetWatchState`, mirroring the
existing `watchNoTargetFrames` safety-net pattern (which also exits after 3
frames). `ParsekFlight.ApplyZoneRenderingImpl` drives the counter on every
watched-ghost frame regardless of zone hide/positioning order, so the bug
where `renderDistance >= Beyond` returned hidden before positioning ran (and
left a stale ghost transform that could indefinitely re-trigger any
sanity-check using `state.ghost.transform.position`) is structurally
impossible. The Register call is gated on `isWatchedGhost && watchMode != null`
so non-watched ghosts running later in the same frame never reset the counter
back to 0 (which would otherwise prevent the threshold from ever being
reached in any multi-ghost frame, since the controller-level counter is
shared across all ghost states). The first cutoff log at the actual exit now ends with
`after 3-frame debounce — exiting watch mode`, and intermediate frames emit a
rate-limited `[VERBOSE][Zone] cached cutoff tripped (...) debounce=N/3 —
staying in watch mode` line so the debounce window is observable from
`KSP.log`.

Why not the original "compare cached vs freshly-read live transforms" sanity
check (PR #617 first commit, reverted): the sanity check rejected exits when
`Vector3d.Distance(state.ghost.transform.position,
FlightGlobals.ActiveVessel.transform.position)` was within range, but
`state.ghost.transform.position` at the time of `ApplyZoneRenderingImpl` is
the previous frame's positioning result (positioning runs after zone
rendering). A real cutoff crossing during warp where last frame's transform
was still in range but the current frame's body math correctly says
350000 m would be wrongly rejected. Worse, when `renderDistance` is also
beyond-zone, the engine returns hidden before positioning runs, so the stale
transform persists and the rejection repeats every frame. Debounce sidesteps
both problems because it never reads `state.ghost.transform.position` at
all — it only counts how many consecutive frames the same cached signal has
held. Real exits happen ~50 ms late at 60 fps, which is imperceptible.

Regression coverage: `WatchModeControllerTests.ShouldExitWatchAfterCutoffDebounce_*`
(threshold truth table) plus
`RegisterWatchCutoffSampleAndShouldExit_WithinRangeFrame_ResetsCounter` (one
bogus frame followed by within-range frame: counter resets, no exit) and
`RegisterWatchCutoffSampleAndShouldExit_ConsecutiveCutoffFrames_ExitsAtThreshold`
(N consecutive cutoff frames trip the exit at exactly the threshold).

**Follow-up (2026-04-29, `logs/2026-04-29_2112_reflight-supersede-investigation`):**
the same camera-reset symptom reproduced after watching ghost `#0 "Kerbal X"`
through upper/lower stage separation, but this was not the one-frame
FloatingOrigin/Krakensbane seam that PR #617 debounced. The cached distance
stayed bad for the whole 3-frame debounce window, so watch mode correctly
exited. Root cause: immediately after the RELATIVE-to-ABSOLUTE section
boundary, ABSOLUTE playback and watch-distance resolution could fall back to
the flattened `traj.Points` list. That list can contain RELATIVE v6
anchor-local metre-offset samples adjacent to ABSOLUTE latitude/longitude/
altitude samples; interpolating across them as body-fixed coordinates produced
a planet-scale false position, terrain clamp, and ~800 km distance spike even
though the section-local ABSOLUTE frame was around 54 km altitude. Fix:
flight playback, loop playback, and watch cutoff distance resolution now use
the active ABSOLUTE `TrackSection.frames` when present, reserving the flat-list
path for legacy/no-section data only. Regression coverage:
`ZoneRenderingTests.TryGetAbsoluteSectionPlaybackFrames_AbsoluteSection_UsesSectionFrames`
and
`ZoneRenderingTests.TryGetAbsoluteSectionPlaybackFrames_RelativeSection_ReturnsFalse`
plus the empty-frame guard.

## 626. Watch-mode W->W switches restored a stale world camera direction instead of preserving the local viewing angle

When the user clicked Watch on ghost B while watching ghost A, returned to A
several seconds later, then re-clicked Watch on B, the camera resumed pointing
in the same world-space direction it had on B before the user left — but B's
horizon proxy / camera pivot had rotated continuously during the gap, so the
"same world direction" decomposed into a meaningfully different (and visually
surprising) local pitch/heading on the rotated basis. Logs:
`logs/2026-04-27_1902/KSP.log:208600-208760` — `Watch camera capture
(switch-source)` ... `Watch camera apply (switch-apply)` pairs show
`sourceWorldOrbit ≈ resolvedWorldOrbit` with a different local
pitch/hdg on each W->W round-trip.

Root cause: `WatchModeController.EnterWatchMode` stored the captured source
state into the per-mode remembered slot via the raw `RememberWatchCameraState`,
which kept `HasWorldOrbitDirection=true`. The local `switchCameraState` was
then passed to `TryApplySwitchedWatchCameraState`, where
`CompensateTransferredWatchAngles` decomposed the world direction into the
destination ghost's *current* basis — fine for chain transfers (immediate
within-frame auto-handoff via `TransferWatchToNextSegment`, no drift window),
wrong for explicit user W->W switches separated by many frames.

Fix: Extracted pure `WatchModeController.MakeWatchCameraStateTargetRelative`
helper that strips `HasTargetRotation` and `HasWorldOrbitDirection` from a
copy of a captured camera state. `EnterWatchMode`'s W->W branch now passes
the captured state through this helper before remembering and before applying,
so the restore re-applies the captured `(pitch, hdg)` directly relative to
the destination ghost's own target transform. `TransferWatchToNextSegment`
keeps the raw world-direction path because the auto-handoff applies on the
same frame.

Regression tests in `WatchModeControllerTests`:

- `MakeWatchCameraStateTargetRelative_ClearsBasisFields_KeepsPitchHeadingDistanceMode`
- `CompensateTransferredWatchAngles_TargetRelativeStateIgnoresNewTargetRotation`
- `CompensateTransferredWatchAngles_RawWorldDirectionStateStillProjectsForChainTransfers`

## 624. Finalizer pinned a low orbit with periapsis inside atmosphere as `Orbiting`

Plan: `docs/dev/plans/fix-finalizer-crew-unfinished-2026-04-26.md`.

`RecordingTree.DetermineTerminalState(int, Vessel)` overrode KSP's `SUB_ORBITAL`
to `Orbiting` whenever `vessel.orbit.eccentricity < 1` and `PeR > Radius`. For
atmospheric bodies (Kerbin: atmosphereDepth=70km), an orbit with Pe at 36km
altitude passed the check and finalized as `Orbiting`, even though it would
deorbit within a few orbits via drag. Observed in
`logs/2026-04-26_2247_investigate-finalizer-crew-unfinished/`: SMA=669km,
ecc=0.0967, PeR=636642 (Pe altitude 36642 m, well inside 70km atmosphere).

Fix: extracted pure `IsBoundOrbitAboveAtmosphere(eccentricity, periapsisRadius,
bodyRadius, bodyHasAtmosphere, atmosphereDepth)` helper and made the override
require `PeR > Radius + atmosphereDepth` for atmospheric bodies; atmosphereless
bodies (Mun, Minmus, etc.) keep the existing `PeR > Radius` semantics. Added
explicit null guard for `vessel.orbit.referenceBody` and updated the Info log
line to include `atmoTop` and `bodyHasAtmosphere`.

## 628. Five flaky in-game tests at HEAD were failing for harness reasons, not code regressions ~~done~~

Investigation log: `logs/2026-04-27_1902/parsek-test-results.txt` (5 FAILED in
FLIGHT). All five turned out to be test-authoring bugs / a timing race against
production behavior, not real engine regressions.

1. `Bug613_DeferredSyncWithResolvedAnchor_StillActivates` — the harness built
   the ghost as a bare `new GameObject(...)` with no `MeshRenderer` and no
   `GhostVisualsRoot` child. `TrackGhostAppearance` gates on
   `TryGetCombinedVisibleRendererBounds` walking the part-container's child
   `Renderer`s (with a fallback to the ghost root itself), so a bare
   `GameObject` could never satisfy the renderer probe and `appearanceCount`
   could never reach `>= 1`. The retired-anchor counterpart passed only
   because it asserted `appearanceCount == 0`, which was trivially true with
   no renderers. Fix: build the ghost via `GameObject.CreatePrimitive(Cube)`
   (with the auto-added `Collider` destroyed) so the harness has a real
   enabled `MeshRenderer` for the appearance probe to find.

2. `Bug613_FreshSpawnIntoUnresolvedRelativeSection_NoRetargetAtOrigin` /
   `Bug613_FreshSpawnWithResolvedAnchor_StillFiresRetarget` /
   `Bug613_ResolvedAnchorFiresOverlapExpiry` — all three called
   `Bug613LoopRelativeTrajectory` with `loopIntervalSeconds: 2.0` and the
   trajectory's hard-coded 5 s duration to drive the overlap-loop branch.
   `ResolveLoopInterval` clamps period upward to `LoopTiming.MinCycleDuration
   = 5 s` ("`#381` defensive clamp"), and `IsOverlapLoop(5, 5)` is false, so
   the engine silently took the single-ghost loop path with
   `PendingSpawnLifecycle.LoopEnter` instead of `OverlapPrimaryEnter`. KSP.log
   confirms: `[WARN][Loop] ResolveLoopInterval: period 2s below
   LoopTiming.MinCycleDuration 5s ... clamping defensively (#381)` and the
   subsequent `lifecycle=LoopEnter cycle=0` line. The retired-side
   counterparts passed only because they assert `Count == 0`, trivially true
   when the overlap branch never runs. Fix: parameterized
   `Bug613LoopRelativeTrajectory.durationSeconds` (default 5 s, kept for
   single-ghost / pause-window scenarios) and updated the FreshSpawn and
   OverlapExpiry harnesses to use `loopIntervalSeconds: 5.0,
   durationSeconds: 10.0` so `IsOverlapLoop(5, 10)` is true and the period
   stays at the 5 s minimum unclamped. The OverlapExpiry harness also bumps
   `currentUT` from `6.0` to `11.0` so the older overlap cycle's phase
   (`11 - 0*5 = 11`) actually exceeds the new `duration = 10` and walks the
   expire-overlap branch.

3. `RewindToLaunch_PostRewindFlightLoad_KeepsFutureFundsAndContractsFiltered`
   — the canary called `flight.CommitTreeFlight()` and then waited via
   `WaitForCommittedRecording` for the recording to land in
   `RecordingStore.CommittedRecordings`. The commit landed, but ~25 ms later
   `ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore` immediately
   pulled the just-committed tree back out of `CommittedRecordings` to keep
   it as the live active tree (the active vessel still represents the
   recording, so the auto-restore path took ownership). The test's wait
   yield-broke during the brief committed window, but Unity's coroutine
   semantics scheduled the next test-method line on the *next* frame — by
   then the auto-restore had run and `CommittedRecordings.FirstOrDefault`
   returned null. Fix: `WaitForCommittedRecording` now also looks up the
   recording in `flight.ActiveTreeForSerialization?.Recordings` (same
   `Recording` instance moves between the slots), latches the
   "count went up" observation so a single tick inside the brief committed
   window is sufficient, gates on `RewindSaveFileName` populated as the
   strong "the commit's rewind-save plumbing finished" signal, and accepts
   an optional `Action<Recording>` callback so the caller captures the
   reference inside the polling loop instead of racing the auto-restore on
   the next frame.

4. `TimeJumpManager` (cosmetic, caught by CI's `LiveKspLogValidationTests`):
   two `ParsekLog.Warn(Tag, ...)` callsites prefixed their payload with the
   literal string `"WARNING: "`. `ParsekLog.Warn` already emits
   `[Parsek][WARN][TimeJump]` itself, so the payload prefix duplicated the
   level token and tripped the live-log rule `WRN-001`. Fix: drop the
   redundant `"WARNING: "` from both `vessel '{0}' is in atmosphere — orbit
   propagation is approximate` and the sibling `... — epoch shift is
   approximate` warning.

## 625. Recording captured zero crew because stand-ins were just deleted from roster

Plan: `docs/dev/plans/fix-finalizer-crew-unfinished-2026-04-26.md`.

User re-launched a vessel whose original crew (Jeb/Bill/Bob) was still aboard a
prior orbiting mission. `CrewAutoAssignPatch` correctly swapped the editor
manifest to stand-ins (Urgan/Verdorf/Sara). On FLIGHT scene OnLoad,
`KerbalsModule.ApplyToRoster`'s "Step 2" displaced-unused branch checked only
`IsKerbalInAnyRecording` — which consults committed recordings, not the live
vessel about to start recording — so it deleted the three stand-ins from the
roster. KSP then loaded the saved ProtoVessel, which referenced now-missing
crew names; the seats came up empty. The recording started ~1 second later
with `0 start crew trait(s)`. Crew reappeared in-flight via a later
`SwapReservedCrewInFlight`, but the recording's start/end-crew was permanently
empty.

Fix: in `KerbalsModule.ApplyToRoster` Step 2, before the `Available + TryRemove`
delete branch, additionally call `roster.IsKerbalOnLiveVessel(standIn)` and
keep the stand-in if it is currently seated on a known vessel. New
`retainedLive` counter included in the `ApplyToRoster complete:` summary so
the new behavior is observable.

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

## Rewind to Separation — v0.9 carryover follow-ups

The feature itself shipped on `feat/rewind-staging` across the v0.9 cycle (design: `docs/parsek-rewind-to-separation-design.md`; pre-implementation spec archived at `docs/dev/done/parsek-rewind-separation-design.md`; roadmap + CHANGELOG under v0.9.0). Items 1-18 of the post-merge follow-up cascade are archived in `done/todo-and-known-bugs-v4.md`.

Items below were landed on PR #514 (`bug/extrapolator-destroyed-on-subsurface`) on top of v4's archive sweep and will move to the next archive when that PR merges.

19. **Unfinished Flight rows duplicated as top-level mission rows alongside the nested Unfinished Flights subgroup.** ~~done~~ — `RecordingsTableUI.DrawGroupTree` populates each tree's auto-generated root group from `grpToRecs`, which stores raw tree membership and does not know about the virtual Unfinished Flights subgroup. After item 18's reap-and-Immutable fix the duplication was no longer driven by lingering RPs, but a pre-merge UF (terminal=Destroyed, RP still alive) was still rendered twice: once via `BuildGroupDisplayBlocks(directMembers, …)` as a regular tree row, and once via `DrawVirtualUnfinishedFlightsGroup(…, nestedUnfinished)` as the nested system group. Fixed by filtering UF members out of `directMembers` in `DrawGroupTree` when `hasNestedUnfinished` is true; the trim is rate-limit-logged via key `uf-filter-out-of-tree-row-<groupName>`. The UF subgroup remains the sole render surface for those rows. Defensive comment added to `DrawVirtualUnfinishedFlightsGroup`'s Rewind/FF placeholder spelling out that the virtual group has no group-level Re-Fly button (each member maps to a specific RP slot, so a single "re-fly all" makes no sense).

20. **Coalescer produced an "Unknown" 0s ghost recording for controllable splits whose child died inside the breakup window.** ~~done~~ — `ParsekFlight.ProcessBreakupEvent`'s controlled-children loop ran `CreateBreakupChildRecording` for every entry in `crashCoalescer.LastEmittedControlledChildPids`, including pids whose live `Vessel` had been torn down by Unity before the breakup window expired. When the child also had no pre-captured snapshot (`crashCoalescer.GetPreCapturedSnapshot(pid) == null`), the resulting recording carried only the seed trajectory point, no events, no snapshot, and `VesselName="Unknown"` (the in-flight name resolution returns null on a destroyed Vessel and `CreateBreakupChildRecording` falls back to the literal "Unknown"). Player saw a 0s "Unknown" row in their tree's auto-group with no playback or replay value next to the real BREAKUP children. Fixed by short-circuiting the loop when both `childVessel == null && ctrlSnap == null`: the BREAKUP branch point on the parent recording already captures that the split happened, so the empty child recording is dropped before allocation. INFO log `ProcessBreakupEvent: skipping dead-on-arrival controlled child pid=… (vessel destroyed before window expired, no pre-captured snapshot) — would produce an 'Unknown' 0s row with no playback value` makes the skip auditable. Items 19 + 20 reproduced from the `2026-04-25_1047_uf-rewind-real-upper-stage` playtest (KSP.log lines 11049 and the IsUnfinishedFlight render trace at 12:08:38).

2026-04-26 follow-up from `logs/2026-04-26_1332_refly-bugs`: the same phantom row still reproduced when the child had a pre-captured snapshot but no live vessel at coalescer emission time. The skip predicate now treats any no-live-vessel controlled child as dead-on-arrival, regardless of snapshot presence; a snapshot alone cannot produce a useful controllable child recording after Unity has already torn down the vessel.

21. **Re-Fly session marker silently wiped on FLIGHT->SPACECENTER round-trip when the active recording was a previously-promoted Unfinished Flight.** ~~done~~ — Review follow-up: the original carve-out only accepted `CommittedProvisional` for in-place continuation and rejected `Immutable`. But `EffectiveState.IsUnfinishedFlight` accepts both Immutable and CommittedProvisional (line 156-157), and `RewindInvoker.AtomicMarkerWrite` has no MergeState gate — it will write an in-place marker for any committed origin with a matching vessel pid. The same save/load wipe symptom therefore reproduced for Immutable UFs. Extended the carve-out to accept `Immutable` as well as `CommittedProvisional` for in-place continuation; the placeholder pattern (origin != active) stays NotCommitted-only because no committed recording is reused there. Test `MarkerInvalid_InPlaceContinuation_Immutable_Cleared` flipped to `MarkerValid_InPlaceContinuation_Immutable_Preserved`. `MarkerValidator.Validate` enforced `active.MergeState == NotCommitted` for `ActiveReFlyRecordingId`. That worked for the original placeholder pattern (origin != active, where the active row was always a fresh `NotCommitted` placeholder) but post-#514 the in-place continuation pattern (`origin == active`, the existing recording continues being recorded into) reuses the existing recording's MergeState. For a UF-as-source of re-fly that recording is `CommittedProvisional` from the prior tree merge's `ApplyRewindProvisionalMergeStates`, so on the SPACECENTER load that precedes the merge dialog the validator failed and `LoadTimeSweep` cleared the marker. Fixed in `MarkerValidator.Validate` by gating MergeState as: accept `NotCommitted` always; accept `CommittedProvisional` and `Immutable` only when `marker.OriginChildRecordingId == marker.ActiveReFlyRecordingId` (in-place continuation). Tests `MarkerValid_InPlaceContinuation_CommittedProvisional_Preserved`, `MarkerInvalid_PlaceholderPattern_CommittedProvisional_Cleared`, and `MarkerValid_InPlaceContinuation_Immutable_Preserved` in `LoadTimeSweepTests.cs`. Reproduced from the `2026-04-25_1246_uf-rebuild-spawn-message-stale` playtest (KSP.log line 12:42:34.296 `Marker invalid field=ActiveReFlyRecordingId; cleared`).

22. **EVA splits never authored a Rewind Point, so destroyed EVA kerbals could not become Unfinished Flights.** ~~done~~ — `ParsekFlight.IsTrackableVessel` defined "trackable" as `SpaceObject` or any part with `ModuleCommand`. EVA kerbals carry `KerbalEVA` rather than `ModuleCommand`, so the kerbal vessel was classified non-controllable by `SegmentBoundaryLogic.IdentifyControllableChildren`. `TryAuthorRewindPointForSplit` for `BranchPointType.EVA` then ran with `controllable.Count = 1` (mother vessel only), `IsMultiControllableSplit(1) == false`, and bailed with `Single-controllable split: no RP (bp=… type=EVA controllable=1)` — no Rewind Point was created. The EVA kerbal recording was still committed and could end with `terminal=Destroyed`, but `EffectiveState.IsUnfinishedFlight` requires a matching RP via `ParentBranchPointId` or `ChildBranchPointId`, so without an RP the destroyed kerbal silently dropped out of Unfinished Flights and the player had no Re-Fly button. Fixed in `IsTrackableVessel` (and `IsTrackableVesselType`) by treating `v.isEVA || v.vesselType == VesselType.EVA` as trackable up front — EVA kerbals are directly controllable by the player even though their part lacks `ModuleCommand`, so for split-event classification they must count as a controllable output. Test `IsTrackableVesselType_EVA_ReturnsTrue` (renamed from `_ReturnsFalse`) in `SplitEventDetectionTests.cs` pins the type-only branch; the live-vessel branch can only be exercised in-game. Reproduced from the `2026-04-25_1314_marker-validator-fix` playtest (KSP.log lines 134680 and 137082 `Single-controllable split: no RP (bp=… type=EVA controllable=1)`).

23. **Map View ghost orbit gap + post-warp survivor spawn from `2026-04-25_1314_marker-validator-fix`.** ~~done~~ — the playtest had two separate issues. First, Flight Map View map-presence creation called `ResolveMapPresenceGhostSource(... allowTerminalOrbitFallback:false)`, so recording `#8` / `b85acd51...` stayed `source=None reason=no-current-segment terminalFallback=False` across the long sparse gap between the early relative section (`UT 1658.9-1668.1`) and the first orbit segment (`UT 171496.6`). It only created a map vessel once that segment became current, then tore it down again when `CurrentOrbitSegmentAt` returned null. Flight Map View now allows terminal-orbit fallback only when the current UT is inside the recording's activation window, before its end UT, and outside all recorded track-section coverage; existing map vessels are kept and orbit-updated through that fallback with an explicit transition log, while recorded pre-orbit coverage still suppresses future terminal orbit previews. Second, the surviving `#15 "Kerbal X"` capsule reached `terminal=Splashed` and was queued during warp, but `FlushDeferredSpawns` kept it pending forever because the endpoint was outside the active vessel's physics bubble. Deferred spawns now execute once warp is inactive, and the obsolete physics-bubble spawn helper was removed so the policy cannot silently keep terminal materializations queued by distance. Regressions landed in `GhostMapPresenceTests.ResolveMapPresenceGhostSource_TerminalFallback_FillsSparseOrbitGapBeforeEnd`, `GhostMapPresenceTests.ResolveMapPresenceGhostSource_TerminalFallback_DoesNotOverrideRecordedPreOrbitCoverage`, `GhostMapPresenceTests.TryResolveTerminalFallbackMapOrbitUpdate_ExistingOrbitSwitchesAcrossSparseGap`, `GhostMapPresenceTests.ResolveMapPresenceGhostSource_MaterializedRecordingSuppressesMapGhost`, and `DeferredSpawnTests.FlushDeferredSpawns_SpawnsQueuedSplashedSurvivorAfterWarpEnds`.

27. **AsteroidSpawner race injected an asteroid into a breakup branch and created duplicate disabled Unfinished Flights.** ~~done~~ — `logs/2026-04-26_2228_asteroid-duplicate-uf-fly-disabled` showed stock `AsteroidSpawner` creating `Ast. ODS-562` in the same physics window as a joint-break split. The deferred breakup scan saw three new vessels instead of the two rocket debris fragments, counted the asteroid as controllable because `VesselType.SpaceObject` is globally trackable, authored a RewindPoint slot for it, and then surfaced the two real debris recordings as duplicate Unfinished Flight rows with disabled `Fly` buttons because they shared the BP/RP but had no child slots. Fixed in two layers: the deferred active/background breakup scans now reject SpaceObject / PotatoRoid / PotatoComet / ModuleAsteroid / ModuleComet vessels before classification, and `EffectiveState.IsUnfinishedFlight` plus `RecordingStore.ApplyRewindProvisionalMergeStates` now require the recording to resolve to an actual RewindPoint child slot before showing/promoting it. Regression coverage: `SplitEventDetectionTests.IsSpaceObjectLikeBreakupScanReject_*`, `UnfinishedFlightsMembershipTests.DestroyedUnderRPWithoutSlot_NotMember`, `RecordingsTableUITests.ResolveUnfinishedFlightRewindRoute_MissingSlotDoesNotExposeUnfinishedFlightButton`, and `TreeCommitTests.CommitTree_DestroyedChildUnderRewindPointWithoutSlot_RemainsImmutable`.

Review follow-ups raised during the `2026-04-25_0153` post-landing review (design-level and diagnostic polish; not blockers on the current branch):

25. **In-game `RewindToLaunch_PostRewindFlightLoad_KeepsFutureFundsAndContractsFiltered` timed out on the very first real run because `flight.StopRecording()` does not commit the active tree.** ~~done~~ — `#527` review-fix added the live rewind canary in `RuntimeTests.cs`, but `flight.StopRecording()` only stops the underlying `FlightRecorder`; nothing in `ParsekFlight.StopRecording` adds the active tree to `RecordingStore.CommittedRecordings` or copies `RewindSaveFileName` to the tree root (that work lives in `CommitTreeFlight` -> `FinalizeTreeRecordings` -> `CopyRewindSaveToRoot` and the scene-exit MergeDialog path, neither of which the test triggered). The test then waited 10 s for the recording to materialize in `CommittedRecordings`, recorded `committedBefore=294, committedNow=294, isRecording=False`, and bailed before `InitiateRewind` could run. The test was added in `b3d73408` and never actually executed until the `2026-04-25_2147` playtest because the runner skips `AllowBatchExecution=false` rows in batch mode. Fixed by switching the canary to `flight.CommitTreeFlight()`, which finalizes the active tree, copies the rewind save filename to the root recording, and adds the recording to `CommittedRecordings` so `InitiateRewind` can resolve a real rewind owner; the underlying scenario decision is already pinned by the headless `RewindUtCutoffTests` suite. Reproduced from the `2026-04-25_2147` playtest (`parsek-test-results.txt` lines 30-31 + `Player.log` lines 444977-461516).

24. **Re-fly merge supersede only covered the chain head, leaving a chain-tip orphan after env-split crashes.** ~~done~~ — Review follow-up: `EnqueueChainSiblings` originally matched siblings by `ChainId` + `ChainBranch` across every committed recording, mirroring `IsChainMemberOfUnfinishedFlight`. The terminal-chain resolver `ResolveChainTerminalRecording` already scopes by owning tree, and the closure builder feeds both supersede commits and the tombstone scan — a future clone path / import / legacy save that drops the same `ChainId`+`ChainBranch` into a foreign tree could silently pull unrelated recordings into the closure (hidden in ERS, kerbal-death actions retired). Hardened with a `TreeId` gate: candidates must share the dequeued member's `TreeId`; recordings without a `TreeId` skip chain expansion entirely. SplitAtSection always emits same-tree segments by construction (`RecordingStore.cs:1992` sets `second.TreeId = original.TreeId`), so the gate is defense-in-depth for legacy / future shapes. Test `ChainExpansion_DifferentTree_Excluded` in `SessionSuppressedSubtreeTests.cs` installs two trees with colliding `ChainId`+`ChainBranch` and asserts the closure stops at the tree boundary. `EffectiveState.ComputeSessionSuppressedSubtreeInternal` (`Source/Parsek/EffectiveState.cs:523`) walked the suppressed-subtree closure forward via `ChildBranchPointId` only. Merge-time `RecordingOptimizer.SplitAtSection` splits a single live recording at env boundaries (atmo↔exo) into a `ChainId`-linked HEAD + TIP where the HEAD keeps the parent-branch-point link to the RewindPoint but ends with `ChildBranchPointId = null` (moved to the TIP at `RecordingStore.cs:2018-2019`), while the TIP carries the `Destroyed` terminal. After re-fly merge, only the HEAD got a supersede row pointing at the new provisional; the TIP stayed visible with the original "kerbal destroyed in atmo" outcome alongside the new "kerbal lived" re-fly. Fixed by adding an `EnqueueChainSiblings` helper invoked at the top of the dequeue body, BEFORE the `ChildBranchPointId` early-return: for each recording added to the closure, every committed recording sharing both `TreeId`, `ChainId`, and `ChainBranch` is also added (and re-enqueued so its own `ChildBranchPointId` walk runs). The contract matches `EffectiveState.IsChainMemberOfUnfinishedFlight` and `ResolveChainTerminalRecording`. Tests `ChainExpansion_HeadOrigin_IncludesTip`, `ChainExpansion_TipOrigin_IncludesHead`, `ChainExpansion_DifferentChainBranch_Excluded`, `ChainExpansion_ThreeSegments_AllIncluded`, `ChainExpansion_TipWithChildBranchPointId_BpDescendantsAlsoIncluded`, and `ChainExpansion_DifferentTree_Excluded` in `SessionSuppressedSubtreeTests.cs`; `AppendRelations_ChainHeadOrigin_WritesSupersedeRowPerSegment` in `SupersedeCommitTests.cs`; `CommitTombstones_KerbalDeathInTip_TombstonedWithChainOrigin` in `SupersedeCommitTombstoneTests.cs`. No retroactive migration: pre-existing affected saves keep the orphan TIP and require a manual `Discard`. Plan in `docs/dev/plans/fix-chain-sibling-supersede.md`.

2026-04-26 follow-up from `logs/2026-04-26_2051_reflight-upper-stage-offset`: the merge closure included the old destroyed tail plus the new optimized Re-Fly chain, but `TryCommitReFlySupersede` still resolved the supersede target to the in-place head because transient `CreatingSessionId` tags were gone by finalization time. `AppendRelations` rejected the null-terminal head, left the session marker in place, and kept the stale destroyed booster row visible. Fixed by resolving protected in-place chain members from contiguous post-optimizer UT bounds in the flat committed list when session metadata is missing; stale same-chain tails are non-contiguous and still receive supersede rows to the new tip. Regression: `TryCommitReFlySupersede_InPlaceContinuation_UntaggedOptimizerSplit_UsesContiguousTip`.

2026-04-26 follow-up from `logs/2026-04-26_2051_refly-upper-stage-position-supersede-watch`: duplicate real-vessel load was fixed by the slot scrub, but the split child still queued the synchronous decouple-callback seed even when the live controlled child had moved hundreds of metres during the coalescer window (`seedLiveRootDist=865.88m`). That raw distance is expected for a fast ascent over the 0.5s coalescer delay, so the replacement guard compares the live root to the captured seed propagated by its recorded velocity instead. Controlled children now prefer a live root-part sample only when that propagated residual exceeds 50m, which catches the captured ~270m radial miss without treating normal high-speed travel or ordinary decoupler shove as drift. The same package showed watch auto-follow logging success after `TransferWatchToNextSegment` refused a partially built ghost; policy now starts a retry hold and only logs auto-follow after the transfer succeeds, and watch mode uses a 300km entry / 305km exit hysteresis so a deferred near-cutoff transfer does not immediately pop the camera back out.

26. **In-game test `Bug289.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty` failed in FLIGHT scene with "Expected FinalizeIndividualRecording stable-terminal re-snapshot log line during finalize".** ~~done~~ — `RuntimeTests.cs:6898-6911` requires the captured log line to contain `[Flight]`, `FinalizeIndividualRecording`, `stable terminal state`, AND `[#289`. The production `ParsekLog.Info` call inside `TryRefreshStableTerminalSnapshot` (`ParsekFlight.cs:9935-9937`) was missing the `[#289]` tag — the sibling `CommitTreeSceneExit` log line at `ParsekFlight.cs:1577` already used the convention. Fixed by appending `[#289]` to the message. Reproduced from `logs/2026-04-25_2147` (parsek-test-results.txt + Player.log line 102836). Regression pin remains the in-game test (the function takes a live `Vessel`, so xUnit cannot reach it).

27. **In-game test `CrewReservationTests.ReplacementsAreValid` NRE'd in MAINMENU scene with "Object reference not set to an instance of an object".** ~~done~~ — `RuntimeTests.cs:4591` dereferenced `HighLogic.CurrentGame.CrewRoster` without a null guard. In MAINMENU, `HighLogic.CurrentGame` is null when no save is loaded. The early `replacements.Count == 0` return at line 4585 made the other scenes pass — they hit zero replacements first — but MAINMENU after the test-order swap ran with one replacement loaded from the save's static state, then NRE'd on the deref. The sibling `ReservedCrewNotAssigned` (line 4660) already null-guards the same access. Fixed by mirroring that guard right after the count-zero early return: skip via `InGameAssert.Skip("No crew roster available")` when `HighLogic.CurrentGame?.CrewRoster` is null. Reproduced from `logs/2026-04-25_2147` (Player.log line 663325).

28. **#571 in-game regression `GhostMapCheckpointSourceLogResolvesWorldPosition` failed in FLIGHT and TRACKSTATION with "Expected StateVector checkpoint source, got Segment".** ~~done~~ — Cross-reference: original closure for `~~571~~` (above) preserves the seed `OrbitSegment` as the Keplerian source of truth and adds densified `OrbitalCheckpoint` frames as section-local samples along that same arc. The shipped resolver `ResolveMapPresenceGhostSource` (`Source/Parsek/GhostMapPresence.cs:2430-2476`) intentionally returns `Segment` whenever the segment list covers `currentUT` — `TryResolveCheckpointStateVectorMapPoint` is consulted only for diagnostic detail, never to override the source — and the comment block at `Source/Parsek/GhostMapPresence.cs:2478-2482` plus the pinned xUnit `ResolveMapPresenceGhostSource_VisibleSegment_MatchesTrackingStationWrapper` confirm the contract. The in-game test was authored against an earlier interpretation of the closure rationale and asserted `StateVector` for a fixture where the segment and inline frames cover the same window. Fixed by aligning the in-game test with the resolver: it now asserts `Segment`, captures the `OrbitSegment` `out` and checks `body.name == resolvedSegment.bodyName`, and looks for `sourceKind=Segment` (instead of `sourceKind=StateVector`) in the captured decision line — the original `world=(x,y,z)` (not `(unresolved)`) world-resolution pin is preserved through `BuildOrbitSourceStructuredDetail` -> `TryResolveOrbitWorldPosition`. Added xUnit regression `ResolveMapPresenceGhostSource_OrbitalCheckpointWithCoexistingSegment_ReturnsSegment` in `Source/Parsek.Tests/GhostMapPresenceTests.cs` mirroring the in-game fixture (single OrbitSegment + OrbitalCheckpoint TrackSection both covering currentUT, two inline frames) so the contract is enforced from headless tests too. Reproduced from `logs/2026-04-25_2147/Player.log` line 134538 (`source=Segment branch=(n/a) … reason=runtime-571-checkpoint-world`).

29. **Re-Fly 22:10 cascade: duplicate upper-stage view, marker/RP loss, and zero-payload sidecar overwrite broke playback after rewind.** ~~done~~ — Reproduced from `logs/2026-04-25_2210_refly-bugs`. The duplicate upper-stage view was a post-load ordering race: `UpdateTimelinePlaybackViaEngine` spawned/positioned timeline ghosts before `RewindInvoker.ConsumePostLoad` completed strip/activate/atomic marker write, so the selected in-place continuation vessel could coexist for a few frames with its own pre-marker ghost. The later playback failure had two persistence causes: `MarkerValidator` rejected a valid live marker when the tree existed only as `RecordingStore.PendingTree`, `RewindPointReaper` could reap the RP still referenced by the live marker, and the pending-Limbo active tree carried stale-sidecar epoch failures for the root/upper-stage recordings; a later active-tree save wrote those failed records back out as empty `.prec` files, replacing the good committed launch trajectory and leaving no watchable ghost. Fixed by gating timeline playback while `RewindInvokeContext.Pending` is true, accepting pending-tree marker ownership during validation, preserving marker-referenced RPs during reap, repairing hydration-failed active-tree records from the committed tree during restore/save, and skipping an active-tree sidecar write if a failed-hydration record is still truly empty. Regression coverage: `LoadTimeSweepTests.MarkerValid_TreeExistsOnlyAsPendingTree_Preserved`, `LoadTimeSweepTests.Reaper_PreservesEligibleRpReferencedByActiveMarker`, `QuickloadResumeTests.RestoreHydrationFailedRecordingsFromCommittedTree_RestoresFailedActiveTreeMatches`, and `RewindTimelineTests.ShouldSkipTimelinePlaybackForPendingReFlyInvoke_ReturnsPendingState`.

30. **Re-Fly merge left a clickable real upper-stage vessel alongside the playback ghost.** ~~done~~ — Reproduced from `logs/2026-04-26_1025_3bugs-refly` (`SuppressedSubtree=[2 ids: 89eff843..., 805d53b7...]`, `ApplyVesselDecisions: ghost-only for 'Kerbal X Probe'` only the leaf, `Held ghost spawn succeeded on retry: #9 'Kerbal X' id=1d6d2116...`, then post-facto `Stripping orphaned spawned vessel 'Kerbal X Probe'`). `MergeDialog.BuildDefaultVesselDecisions` only iterated `tree.GetAllLeaves()`, so a non-leaf parent recording inside `EffectiveState.ComputeSessionSuppressedSubtree` (a recording with `ChildBranchPointId != null` because of breakup/decoupling/chain-split branches) never had its `VesselSnapshot` nulled by `ApplyVesselDecisions`. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` then saw `rec.VesselSnapshot != null` for the parent and spawned a real vessel for it. Fixed in `MergeDialog.cs` by computing the suppression closure from `ParsekScenario.Instance.ActiveReFlySessionMarker` in `ShowTreeDialog` and passing it (plus the active Re-Fly target id) into a new `BuildDefaultVesselDecisions(tree, suppressedRecordingIds, activeReFlyTargetId)` overload. The new pass force-flips every closure id present in the pending tree to ghost-only, except the active Re-Fly target id which must stay spawnable (it is the live vessel the player is flying). Tests `BuildDefaultVesselDecisions_SuppressedNonLeafForcedGhostOnly`, `BuildDefaultVesselDecisions_NullSuppression_LeafBehaviourUnchanged`, `BuildDefaultVesselDecisions_SuppressedIdNotInTree_CountedAndIgnored`, and `BuildDefaultVesselDecisions_ActiveTargetNotInClosure_NoSkipLog` in `MergeDialogVesselTests.cs`. New `[MergeDialog]` `forcing ghost-only on suppressed`, `keeping active Re-Fly target spawnable`, and `suppressed-subtree pass complete` log lines make the per-id and aggregate decisions auditable from `KSP.log` alone.

31. **Re-Fly in-place continuation merge skipped supersede rows for sibling/parent recordings; a destroyed-final-state sibling like "Kerbal X Probe" stayed visible after merge.** ~~done~~ — Reproduced from `logs/2026-04-26_1025_3bugs-refly/KSP.log` (`SessionSuppressedSubtree: 3 recording(s) closed from origin=89eff843...`, then `TryCommitReFlySupersede: in-place continuation detected (provisional == origin == 89eff843...); skipping supersede merge (no self-supersede row)`). `MergeDialog.TryCommitReFlySupersede`'s in-place continuation branch (provisional `RecordingId == origin id`) called `SupersedeCommit.FlipMergeStateAndClearTransient` but completely skipped `SupersedeCommit.AppendRelations`, so the 3-recording closure (origin + 2 chain siblings, including the destroyed Kerbal X Probe segment) wrote zero supersede rows. With no supersede rows, `EffectiveState.IsVisible` returned true for the destroyed sibling and it stayed in the recordings list. First-cut fix in `MergeDialog.cs` called `SupersedeCommit.AppendRelations` on the in-place path before `FlipMergeStateAndClearTransient`, and re-introduced the `old==new` self-link guard inside `SupersedeCommit.AppendRelations` so the trivial origin-self entry is filtered without producing a 1-node `EffectiveRecordingId` cycle. The journaled merge (`MergeJournalOrchestrator.RunMerge`) is still skipped; only `AppendRelations` runs to write rows for sibling/parent ids in the closure. The existing `RelationExists` duplicate guard makes the call resume-safe. **Review follow-up (optimizer-split chain-tip resolve):** `MergeDialog.MergeCommit` runs `RecordingStore.RunOptimizationPass()` BEFORE `TryCommitReFlySupersede`. When the in-place continuation crossed an environment boundary (atmo↔exo), `RecordingOptimizer.SplitAtSection` (`Source/Parsek/RecordingOptimizer.cs:513-514` and `:536-537`) MOVES `VesselSnapshot` and `TerminalStateValue` from the original head to a freshly-allocated chain TIP, leaving the head with `TerminalStateValue == null`. The first-cut fix passed the head straight to `AppendRelations`, which then failed `ValidateSupersedeTarget`'s `null TerminalState` clause — throw in DEBUG, silent empty subtree in RELEASE — and the sibling supersede rows the in-place fix needs were never written. Resolved in the in-place branch of `TryCommitReFlySupersede` by walking `EffectiveState.ResolveChainTerminalRecording(provisional)` to find the chain tip (same helper `EffectiveState.IsUnfinishedFlight` already uses for post-split terminal lookup) and passing the resolved tip to `AppendRelations` as the validated supersede target; a new optional `extraSelfSkipRecordingIds` parameter on a 4-arg `SupersedeCommit.AppendRelations` overload carries the **full chain membership of the in-place continuation** so no chain segment of the new flight ends up with a row pointing at another member (every member is part of the new flight; superseding any of them would collapse ERS via `EffectiveRecordingId` redirect). The skip set is built by enumerating `RecordingStore.CommittedRecordings` (the same source the closure walk reads at `EffectiveState.cs:550`) and matching `TreeId + ChainId + ChainBranch` against the provisional — the exact predicate `EnqueueChainSiblings` uses at `EffectiveState.cs:722-724` — so the skip set's scope is coherent with the closure walk. Lone-origin in-place merges (no `ChainId` set) degenerate to a single-element skip set with the provisional's own id, which is already filtered by the trivial `old==new` self-link guard inside `AppendRelations`. Same-`ChainId`-but-different-`ChainBranch` siblings (e.g. legacy / clone / import shapes) are correctly excluded from the skip set and still get supersede rows when they enter the closure via the BP walk. **Review follow-up #2 (full-chain skip set):** the first cut of this fix added only the head's id to the skip set, which left a 3+-segment in-place chain (HEAD -> MIDDLE -> TIP) writing a row `old=MIDDLE new=TIP` and silently collapsing MIDDLE in ERS — replaced with the full-chain enumeration described above. The legacy 3-arg `AppendRelations` overload (used by `SupersedeCommit.CommitSupersede` and `MergeJournalOrchestrator.RunMerge`) is unchanged. Tests `TryCommitReFlySupersede_InPlaceContinuation_AppendsSupersedeRowsForSiblings` (rewritten so the head has no terminal post-split and the tip carries the terminal payload, mirroring the post-`RunOptimizationPass` reality), `TryCommitReFlySupersede_InPlaceContinuation_LoneOrigin_FiltersSelfLinkOnly`, `AppendRelations_SelfLinkSkipped_OtherSubtreeIdsStillWriteRows`, `TryCommitReFlySupersede_InPlaceContinuation_OptimizerSplit_ResolvesChainTipAndWritesSiblingRows` (full dialog path with the optimizer-split topology and a prior-attempt sibling whose row IS the one we care about), `AppendRelations_ExtraSelfSkip_FiltersHeadWhileTipIsTheTarget` (direct `AppendRelations` API test independent of dialog wiring), `AppendRelations_LegacyThreeArgOverload_NoExtraSkip_BehavesAsBefore` (regression-pin so the journaled merge path is not silently affected), `TryCommitReFlySupersede_InPlaceContinuation_ThreeSegmentChain_NoMemberSupersededByAnotherMember` (HEAD -> MIDDLE -> TIP regression-pin for review follow-up #2), `TryCommitReFlySupersede_InPlaceContinuation_SameChainIdDifferentBranch_StillSuperseded` (same-`ChainId`/different-`ChainBranch` sibling stays supersedable), and `TryCommitReFlySupersede_InPlaceContinuation_NoChain_ChainSkipSetLogsSizeOne` (lone-origin pin) in `SupersedeCommitTests.cs`. New `[Supersede]` `AppendRelations: skip self-link` and `AppendRelations: skip extra-self-link` Verbose lines plus `skippedSelfLink=N skippedExtraSelfLink=N` aggregate, and `[MergeDialog]` `in-place continuation supersede append wrote N relation(s)`, `resolved chain tip for supersede target: head=... -> tip=...`, and `chain-skip-set: chainId=... chainBranch=... treeId=... members=[...] head=... tip=... size=N` make the new path auditable from `KSP.log`. The same optimizer-split-vs-validation interaction theoretically affects the journaled merge path (`MergeJournalOrchestrator.RunMerge` also calls `AppendRelations` post-`RunOptimizationPass`); not yet observed in playtests because the journaled provisional rarely crosses an env boundary in the brief re-fly window before merge — flagged in this entry for future hardening if it ever bites. Items 30 + 31 reproduced from the same `logs/2026-04-26_1025_3bugs-refly` playtest.

**Review follow-up #3 (non-split null terminal):** the optimizer-tip resolve above only helps chains with a HEAD -> TIP split. The `logs/2026-04-26_1923_refly-upper-stage-still-broken` shape also showed a size=1 chain-skip-set where `provisional == head == tip`; if that recording's `TerminalStateValue` is null, `AppendRelations` still fails the same `null TerminalState` invariant and there is no split tip to rescue it. Fixed by repairing an in-place supersede target with missing terminal state from its captured `SceneExitSituation` before calling `AppendRelations` (for the user log shape, `SPLASHED` maps back to `TerminalState.Splashed`). Regression coverage: `TryCommitReFlySupersede_InPlaceContinuation_NoSplitNullTerminal_RepairsFromSceneExitSituation`.

32. **Re-Fly quickload sidecar epoch mismatch reproduced again in `logs/2026-04-26_1025_3bugs-refly` -- confirmed benign; bug #270 + #585-followup mitigations are doing their job.** ~~no-fix-needed~~ — Around `10:14:55.168-185` two recordings hit the canonical bug `#270` stale-sidecar surface (`c9df8d86...` `.sfs` epoch 2 vs `.prec` epoch 5; `89eff843...` `.sfs` epoch 1 vs `.prec` epoch 3) on the rewind quickload of tree `Kerbal X` (id `50e9197d...`). The user's hypothesis "the writer is committing the `.prec` before the `.sfs`, or reconciliation is running more aggressively than designed" is wrong: `RecordingStore.SaveRecordingFilesToPathsInternal` increments `rec.SidecarEpoch` ONCE per `OnSave`, stages the `.prec` write through `SidecarFileCommitBatch.Apply`, and `ParsekScenario.SaveActiveTreeIfAny` then writes the now-incremented epoch into the `.sfs` `RECORDING_TREE` ConfigNode in the same call. Both files always carry the same epoch within a single save. The mismatch on quickload is structural by design: the `.prec` is per-recording-id (one global file overwritten by every `OnSave`), the `.sfs` is a snapshot of one specific save point. Loading an older `.sfs` (a rewind quicksave taken before subsequent saves) yields a smaller epoch than the on-disk `.prec`, which now belongs to a discarded future timeline. Three protection layers fired correctly in this log: (1) `ParsekScenario.SpliceMissingCommittedRecordings` at `10:14:55.193` reported `loadedBefore=8 committed=10 after=10 splicedRecordings=2 refreshedRecordings=2 ... refreshedIds=[c9df8d86...,89eff843...] source=committed-tree-in-memory` -- the two stale-sidecar IDs were repaired from the in-memory committed tree before the tree was stashed; (2) `Stashed pending tree 'Kerbal X' (10 recordings, state=Limbo)` with `2 sidecar hydration failure(s)` records the Limbo-stash for revert-detection dispatch; (3) `[ReconciliationBundle] Restored: recs=10 trees=1 actions=26 rps=1 ... marker=False journal=False crew=3 groups=1 hidden=0 milestones=2` brought the post-load in-memory state back to the pre-rewind snapshot. The `ShouldSkipSaveToPreserveStaleSidecar` callee-side gate stays armed as defense-in-depth for any subsequent `SaveActiveTreeIfAny` / `BgRecorder` / scene-exit force-write that might still reach the saver with `SidecarLoadFailed=true`+empty state. No code change required; this entry exists so the next reproduction with the same shape can be cross-referenced quickly. If a future repro shows the splice logging `refreshedRecordings=0` for a stale-sidecar id while the matching committed-tree record DOES carry data, that would be a real divergence and warrants reopening; this log shows the contract holding.

33. **Re-Fly relative-anchor/watch/reentry cascade from `logs/2026-04-26_1332_refly-bugs`.** ~~done~~ — The log showed the upper-stage recording entering a RELATIVE section anchored to the booster pid during the original flight. On booster Re-Fly, that pid resolved to the live player-controlled Re-Fly target, so the upper-stage ghost inherited the booster's current frame instead of replaying ground-relative recorded motion. After merge + rewind, the retired-anchor path hid the ghost but left stale position data that could trip the 300 km watch cutoff; the same Re-Fly load also activated reentry FX before the first playback transform, producing a one-frame particle burst at the hidden KSC-surface prime pose. Fixed by bypassing live anchors whose pid is the active Re-Fly target and reconstructing from the recorded anchor trajectory, treating unresolved relative sections as unresolved for distance math instead of falling back to stale ghost transforms, rejecting invalid watched-ghost distances for the full-fidelity override, suppressing hidden-prime visual FX until after activation/position synchronization, and broadening item 20's dead-on-arrival controlled-child skip. Targeted regression coverage: `RelativeAnchorResolutionTests`, `ZoneRenderingTests`, and `CrashCoalescerTests`.

34a. **Re-Fly merge left a destroyed cross-chain sibling visible in the mission list and as a duplicate ghost.** ~~done~~ — Reproduced from `logs/2026-04-26_2357_newest`. After re-flying probe `f3f1f2e6` (Kerbal X Probe atmo, in-place continuation, chain `301a95f0`), the destroyed sibling `29f1d9a8` (same vessel PID `2450432355`, separate chain `73e8a066`, started at UT 162.10 — i.e. AFTER the rewind UT 141.36) stayed in `EffectiveState.IsVisible` because `MergeDialog.TryCommitReFlySupersede` wrote `0` supersede rows: `Added 0 supersede relations for subtree rooted at f3f1f2e6 (subtreeCount=2 ...)`. Root cause was in `EffectiveState.ComputeSessionSuppressedSubtreeInternal` — the BFS walked chain siblings via `EnqueueChainSiblings` (which gates on identical `ChainId` + `ChainBranch`) and BranchPoint children, but never crossed `ChainId` boundaries even for recordings sharing the origin's `VesselPersistentId`. The closure picked up only `[origin, new-chain-tip]`, both of which the chain-skip-set protects from supersede, so `AppendRelations` produced no rows. Fixed by adding `EnqueuePidPeerSiblings` next to `EnqueueChainSiblings`: for every dequeued recording with a non-zero `VesselPersistentId`, enqueue every same-tree recording sharing that PID whose `Recording.StartUT > marker.InvokedUT - 0.05s`. The UT epsilon (`PidPeerStartUtEpsilonSeconds`) absorbs sampler-stamped float jitter without admitting any pre-rewind history (which legitimately predates the rewind point and must not be collapsed). The `pidPeersAdded` counter is appended to the existing `[ReFlySession] SessionSuppressedSubtree` log line so the new walk is auditable from `KSP.log`. Regression: `EffectiveStateTests.ComputeSessionSuppressedSubtree_CrossChainSamePidPostRewindPeer_Included` covers the cross-chain post-rewind sibling, the same-chain sibling already covered by the existing closure walk, and the pre-rewind history exclusion. Tests at 9238/9238 pass.

34b. **Re-Fly playback rendered sibling-chain Relative ghosts at wrong positions (Kerbal X upper-stage bouncing around the map).** ~~done~~ — Reproduced from `logs/2026-04-26_2357_newest`. The Kerbal X upper-stage recording `a0d14b08` (sibling chain to the re-flown probe — NOT a parent) had Relative-frame sections anchored to the probe's PID (`2450432355`) covering UT 438.73→456.55 (post-rail-exit). During Re-Fly, `RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly` had a `victimIsParentOfActiveReFly` gate at line 90 that PR #594 added to avoid hiding legitimately Relative-anchored sibling/rendezvous ghosts. The gate was correct for hide-vs-show but wrong for choose-anchor-source: for any victim whose section anchor is the active Re-Fly target's PID, the live anchor IS the player's hand-controlled vessel and decoded relative offsets cannot match the recording. Without the bypass, `ParsekFlight.TryUseAbsoluteShadowForActiveReFlyRelativeSection` (which already exists for v7 absolute-shadow lookup on parent-chain victims) never engaged for the upper-stage, and the ghost rendered at `worldPos=(-690.8,13.1,-694.6) → (213.2,17.6,-1178.9) → (931.9,9.9,-1187.2) → (1514.5,3.7,-1313.5)` over 5 s of playback — sub-surface jumps of >1 km. Fixed by removing the `!victimIsParentOfActiveReFly return false` early-out in `ShouldBypassLiveAnchorForActiveReFly` (the explicit `victimRecordingId != activeReFlyRecordingId` guard at the bottom is the only legitimate exclusion: the active recording itself is the live vessel and its live pose IS correct). The hint argument is retained on the call site for telemetry. The `ParsekFlight.ShouldBypassLiveRelativeAnchorForActiveReFly` wrapper computes the parent-chain trace for the log line but no longer uses it as a gate. **Create-time analog:** `GhostMapPresence.ResolveStateVectorWorldPosition` (the wrapper over the pure `ResolveStateVectorWorldPositionPure`) now also detects the active-Re-Fly anchor case and passes the parallel `absoluteFrames` entry into the pure helper, which uses it via the standard surface lookup. The `Branch="absolute-shadow"` outcome is distinguishable from the regular Absolute path in logs and tests. The `GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly` parent-chain restriction is intentionally LEFT IN (it's about whether to hide a ghost entirely, not about which anchor source to use; sibling ghosts should still spawn — just at the right place). Regression: `RelativeAnchorResolutionTests.ShouldBypassLiveAnchorForActiveReFly_SamePidSiblingChain_ReturnsTrue` (renamed from `_SamePidButNotParent_ReturnsFalse` to reflect the new contract), `StateVectorWorldFrameTests.AbsoluteShadow_UsesShadowSurfaceLookup_ReturnsLookupResult` (new, asserts the shadow point's body-fixed lat/lon/alt feeds the surface lookup instead of the relative offsets in the original point), `StateVectorWorldFrameTests.AbsoluteShadow_NullShadow_FallsThroughToRelativeBranch` (negative test).

34c. **Recorder re-entered Relative mode against a stale anchor on vessel-switch resume, producing the bouncing trajectory data in the first place.** ~~done~~ — Same playtest as 34a / 34b. The upper-stage's recording at UT 438.71 came off rails as `Absolute` (boundary point), then 0.02 s later opened a Relative section anchored to PID `2450432355` (the probe). Two concurrent root causes: (1) `FlightRecorder.treeVesselPids` was cached ONCE in `InitializeEnvironmentAndAnchorTracking` at recording start (UT ~8). The probe joined the tree at UT 140.79 — long after the cache was frozen — so subsequent `UpdateAnchorDetection` calls treated the probe as a non-tree vessel and let `AnchorDetector.FindNearestAnchor` pick it as the upper-stage's anchor again. (2) `RestoreTrackSectionAfterFalseAlarm` restored the saved `anchorVesselId` blindly via `isRelativeMode = resumeAnchor != 0`, with no check that the anchor was still loaded or still outside the tree. Fix (1): rebuild `treeVesselPids = BuildTreeVesselPids()` at the top of every `UpdateAnchorDetection` non-surface tick. Cost is O(tree members + bg map) per call — same order as `BuildVesselInfoList` itself, negligible vs. physics frame work. Fix (2): in `RestoreTrackSectionAfterFalseAlarm`, after picking up `resumeAnchor` from the saved section, validate it: if the scene's vessel list is queryable AND the anchor is now in `BuildTreeVesselPids()` OR `FindVesselByPid` returns a non-loaded result, downgrade the resume to `ReferenceFrame.Absolute` and emit `[Anchor] RELATIVE resume rejected: anchorPid=N treeMember=B loaded=B vesselPid=N — starting ABSOLUTE section instead`. The next `UpdateAnchorDetection` tick re-picks a valid anchor cleanly. The validation skips when `FlightGlobals.Vessels` is unqueryable (xUnit + pre-FlightReady scene loads) so existing `EnvironmentTrackingIntegrationTests.RestoreTrackSectionAfterFalseAlarm_*` tests continue to pin metadata-preservation semantics for the in-game path. This fix prevents new corrupted recordings; pre-existing data with stale-anchor sections still relies on Fix 34b's playback bypass for correct visual reconstruction. **PR #613 review P1 follow-up:** when the downgrade fires, the boundary point harvested from the prior section's `frames` is anchor-local Cartesian metres — seeding it into the new ABSOLUTE section would write a meaningless metre-scale "lat/lon/alt" sample at the seam, re-introducing the corrupted-trajectory class this fix closes. The boundary substitution now picks the parallel v7 `absoluteFrames` shadow entry when present (carrying the focused vessel's true body-fixed position at the same UT) or skips the boundary seed entirely when the prior section is legacy (no shadow). New `[Anchor] RELATIVE->ABSOLUTE resume: substituting absolute-shadow boundary point` / `skipping boundary seed` Verbose lines make the substitution auditable from `KSP.log`. Regression: `EnvironmentTrackingIntegrationTests.RestoreTrackSectionAfterFalseAlarm_StaleAnchorDowngrade_DoesNotSeedRelativeOffsetAsAbsoluteBoundary` (forces the downgrade via the new `FlightRecorder.SetResumeValidationVesselsOverrideForTesting` test seam, asserts the new section's first frame matches the absolute-shadow values, NOT the relative-offset point's negative-altitude tell), `RestoreTrackSectionAfterFalseAlarm_StaleAnchorDowngrade_NoShadow_SkipsBoundarySeed` (legacy v5/v6 path; reopened section has zero frames). **PR #613 review P2 follow-up:** `GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly` now also fires for `Branch="absolute-shadow"` (not just `"relative"`). The absolute-shadow branch is the v7 sibling positioning source for the same RELATIVE section, returned by `ResolveStateVectorWorldPosition` when the section's anchor PID matches the active Re-Fly target. Suppression depends on the section's underlying RELATIVE shape, not on which positioning source the resolver picked; without this both-branches check the parent-chain doubled-ProtoVessel guard would silently break for v7 recordings. Regression: `Bug587ThirdFacetDoubledGhostMapTests.Suppresses_WhenBranchIsAbsoluteShadow_ParentChainVictim` (positive — absolute-shadow branch fires suppression) and `NotSuppressed_WhenBranchIsAbsolute_RealAbsoluteSection` (negative — only `absolute-shadow` piggybacks; a true Absolute section is unaffected). Tests at 9242/9242 pass.

35. **Watch-mode camera jumped back to active vessel mid-flight when watching a ghost whose Relative-frame anchor was now in stable orbit.** ~~done~~ — Reproduced from `logs/2026-04-27_0123_watch-jump-and-ghost-misalign`. Sequence: user re-flew probe `ffb070a1` (the in-place target), merged, launched a NEW vessel from the pad, then entered watch on upper-stage ghost `85398aca` from that prior recording. At UT 123.5 of playback the ghost transitioned into a Relative section anchored to PID `1917766001` (the just-re-flown probe, now in stable orbit ~818 km from the pad-launched active vessel). The relative-anchor resolver decoded the recorded 10 m offset against the live probe's CURRENT orbital pose, placing the ghost ~818 km away. `[Zone] Ghost #0 "Kerbal X" exceeded ghost camera cutoff (818734m from active vessel >= 305000m; render=805213m) — exiting watch mode` fired and `FlightCamera.SetTargetVessel` reverted to the new active vessel — the user-visible "camera jumped back to the pad" symptom. Root cause: PR #613's absolute-shadow / recorded-anchor bypass at `TryResolveRelativeAnchorPose` only fires when `marker != null && ActiveReFlyRecordingId == OriginChildRecordingId` — i.e. exclusively during an active in-place Re-Fly session. Once the merge clears the marker, every subsequent watch session of any recording with a Relative section anchored to a vessel that has since progressed (post-merge orbit, plain rewind, time-warp etc.) decodes against the wrong live pose. Fix: extended `TryResolveRelativeAnchorPose` so it always probes the recorded anchor pose (dropping the early-exit `if (liveAnchorAvailable && !bypassLiveAnchor) return Live`), and when both poses are available compares them via the new pure `RelativeAnchorResolution.IsStaleLiveAnchor(liveWorld, recordedWorld, threshold, out delta)` helper. When |delta| > 250 m the live anchor is treated as stale and `bypassLiveAnchor` is set, so `SelectAnchorFrameSource` returns Recorded. Threshold rationale: 250 m sits above the 200 m DockingApproachMeters noise floor (so legitimate close-rendezvous ghosts don't false-positive) and well below the km-scale drift the bug exhibits. NaN / Infinity deltas defensively return false to avoid mis-flagging arithmetic-NaN as staleness. New `[Playback] Stale relative anchor detected: anchorPid=N victim=... liveWorld=(...) recordedWorld=(...) delta=Nm threshold=250m — preferring recorded anchor pose` Verbose-rate-limited line documents each fire. Pure helper enables xUnit coverage without a live KSP scene: regression tests `RelativeAnchorResolutionTests.IsStaleLiveAnchor_DeltaBelowThreshold_ReturnsFalse`, `_DeltaAtThreshold_ReturnsFalse` (strict >, boundary stays Live), `_DeltaAboveThreshold_ReturnsTrue` (the bug case), `_NaNDelta_ReturnsFalse`, `_InfinityDelta_ReturnsFalse`, `_ZeroDelta_ReturnsFalse`. **Companion observability additions to make the next "ghost positioned wrong" investigation diagnosable from KSP.log alone:** (a) Ghost-spawn appearance log (`[GhostAppearance] Ghost #N appearance#M ...`) now appends `anchorWorld=(x,y,z) anchor-root=(dx,dy,dz) |anchor-root|=Nm` for Relative-frame sections via the new `GhostPlaybackEngine.DescribeAppearanceLiveAnchorContext` helper. The earlier line only carried `recordingStart` lla which was useless for current-frame debugging. (b) Watch-mode cutoff log appends `ghostWorld=(...) section=... sectionUT=[...] anchorPid=N anchorWorld=(...) |anchor-ghost|=Nm` via the new `ParsekFlight.DescribeWatchCutoffContext` helper, so the user can see WHY the cutoff fired (which section, which anchor, where it actually is). Tests at 9254/9254 pass.

34. **Tracking Station ghost-detail panel flickered + visually clashed, and Parsek's IMGUI ghost icons / labels punched through the Esc pause overlay.** ~~done~~ — Reproduced from `logs/2026-04-26_2301_tracking-station-ui-and-esc-menu`. Two independent regressions in the same fix: (a) `ParsekTrackingStation.DrawSelectedGhostActionSurface` fell through to `GUI.skin.window` instead of routing through `ParsekUI.GetOpaqueWindowStyle()` like every other Parsek window — that style is semi-transparent and Unity replaces its `font`/`padding` between Layout and Repaint events, so the ghost-detail panel flickered frame-to-frame and looked nothing like the rest of the mod. The second button row (`Materialize 106 + Fly 58 + Delete 68 + Recover 76` plus per-button margins) also overflowed the 330-px window, and `Fly` / `Delete` / `Recover` were permanently disabled by `BuildActionStates` because `GhostTracking{Fly,Delete,Recover}Patch` blocks those stock actions on ghost ProtoVessels. Fixed by switching the ghost-actions window to the same opaque style as the control surface, dropping the dead-action row entirely from `BuildActionStates` (so the array now returns just Focus / Target / Recording / Materialize), and giving Materialize its own full-width row with a clear hint of what it does. (b) Custom map markers, ghost labels, and the Tracking Station / KSC / Flight Parsek windows all live on the IMGUI surface, which Unity sorts under the KSP Canvas — so the stock Esc pause overlay (a Canvas UI) drew under our IMGUI instead of over it. Fixed by adding `PauseMenuGate.IsPauseMenuOpen()` (defensive wrapper around `KSP.UI.Screens.PauseMenu.exists && PauseMenu.isOpen`, with a `ProbeForTesting` injection seam for unit tests) and early-returning from `ParsekFlight.OnGUI`, `ParsekKSC.OnGUI`, and `ParsekTrackingStation.OnGUI` while the gate is true. Tests `PauseMenuGateTests` (probe pass-through, swallowed-failure default), `BuildActionStates_WithEligibleRecording_EnablesSafeActionsAndOmitsBlockedStockActions`, and the updated `BuildActionStates_BeforeRecordingEnd_DisablesMaterializeAndExplainsReason` pin the new contract; the rest of the existing `GhostTrackingStationPatchTests` suite still proves the stock Fly/Delete/Recover patches keep blocking those actions on ghost vessels.

The three latent carryover items below are tracked in the design doc under Known Limitations / Future Work and are not yet addressed:

- Index-to-recording-id refactor to lift the 13 grep-audit exemptions added in Phase 3.
- Halt `EffectiveRecordingId` walk at cross-tree boundaries (v1 does not produce cross-tree supersedes; latent-invariant guard).
- Wider v2 tombstone scope (contracts, milestones) when safe.

---

# Known Bugs

## ~~571. Map View ghost icons show weird trajectories that do not match the recorded path~~

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. "the ghost icons in map
view had very weird trajectories, did not move on their correct paths."

**Suspected supporting evidence in the cleaned KSP.log:**

- `[LOG 13:11:38.311] [Parsek][VERBOSE][Policy] Deferred ghost map vessel for #7 "Kerbal X Probe" — recording starts pre-orbital`
- `[LOG 13:11:44.714] [Parsek][VERBOSE][Policy] Deferred ghost map vessel for #8 "Kerbal X" — recording starts pre-orbital`
- `[LOG 13:13:19.369] [Parsek][INFO][GhostMap] Created ghost vessel 'Ghost: Kerbal X' ghostPid=1840826626 type=Ship body=Kerbin sma=4070696 for recording index=8 (from segment) orbitSource=visible-segment segmentBody=Kerbin segmentUT=171496.6-193774.6 …`
- `[LOG 13:13:19.582] [Parsek][INFO][GhostMap] Removed ghost map vessel for recording #8 ghostPid=1840826626 reason=ghost-destroyed`

The capture shows ghost map vessels being created from `visible-segment` /
`endpoint-terminal-orbit` sources, then torn down within ~200ms with
`reason=ghost-destroyed` or `reason=tracking-station-existing-real-vessel`.
Combined with the `Deferred ghost map vessel … recording starts pre-orbital`
deferrals, the player sees orbit-line previews that don't track the recorded
trajectory.

**Diagnosis (2026-04-25):** the symptom has two contributing root causes:

- **Part A (primary, recorder-side):** long warp produces a single
  `OrbitalCheckpoint` track section spanning more than one orbital period. In
  this playtest, recording `b85acd51ea7f4005bb5d879207749e8c` covered
  `UT=171496.6-193774.6` (~22 ks, ~1.36 Kerbin orbital periods) as a single
  `OrbitSegment` (sma=4070696, ecc=0.844672, mna=1.185624, epoch=171496.6) with
  9 sparse trajectory points. `GhostMapPresence.CreateGhostVesselFromSegment`
  (`Source/Parsek/GhostMapPresence.cs:934`) faithfully renders that single
  Keplerian arc — but at warp speed the player sees the icon trace the full
  ellipse 1.36 times during one segment window, which reads as a "weird
  trajectory" that does not match the live ship's path. Conceptual fix:
  densify checkpoint sections during long warp by sampling additional
  interpolated trajectory points along the same Keplerian arc the segment
  encodes, so the playback path has motion samples between segment endpoints.
- **Part B (secondary, predicted-tail-side):** the `MissingPatchBody`
  warning floor (#575) discards the entire predicted patched-conic chain on
  the first null-body patch. In the captured log every entry is
  `patchIndex=1`, meaning patch 0 was always valid but `ResetFailedResult`
  threw it away anyway. Without the predicted tail the recording stores no
  augmentation data between checkpoint segments. This half is closed by
  fixing #575 to keep partial results before the first null patch.

**Files (with line references from the diagnosis pass):**

- Recorder-side densification: the `OrbitalCheckpoint` capture path in
  `Source/Parsek/FlightRecorder.cs` and the orbit-segment add path in
  `Source/Parsek/RecordingStore.cs`.
- Render path (already correct): `Source/Parsek/GhostMapPresence.cs` —
  `CreateGhostVesselFromSegment` line 934, `BuildAndLoadGhostProtoVessel`
  line 3009, sparse-orbit-gap fallback at line 1283-1304 (item 23 fix).
- Predicted-tail path (Part B): `Source/Parsek/PatchedConicSnapshot.cs:151-162`
  (`ResetFailedResult` discard floor) and
  `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:287-296`.
- Latent guard fixed with Part A: the tracking-station refresh path now checks
  `FindOrbitSegmentForMapDisplay` for `HasValue` before reading `seg.Value`, so
  a mid-frame missing segment retires the ghost instead of throwing.

**Reproducer hooks:** in
`logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned`:

- Recorder pattern: `Boundary point sampled at UT=171496.6` →
  `TrackSection started: env=ExoBallistic ref=OrbitalCheckpoint
  source=Checkpoint at UT=171496.59` → `Orbit segment added to TrackSection
  checkpoints: body=Kerbin UT=171496.59-193774.62` → `Recording #N "Kerbal X"
  eligible: UT=[1659.0,193774.6] points=9`. Fixture recording IDs:
  `8e27ba1144a7484b815847c05c49d10e` (pre-merge),
  `b85acd51ea7f4005bb5d879207749e8c` (post-merge).
- Render: `Created ghost vessel 'Ghost: Kerbal X' ghostPid=1840826626 …
  orbitSource=visible-segment segmentBody=Kerbin
  segmentUT=171496.6-193774.6 … sma=4070696 ecc=0.844672`. Pin segment span
  > 1 orbital period.
- `MissingPatchBody` storm: 153× pairs where every entry has `patchIndex=1`,
  never `patchIndex=0`.

**Resolution (2026-04-25):** Closed by the predicted-tail partial-prefix fix
from PR #542 plus Part A recorder-side checkpoint densification. Long
`OrbitalCheckpoint` sections now keep the `OrbitSegment` as the Keplerian source
of truth, but add section-local trajectory points at 5 degrees of true anomaly
(minimum window 600s, max 360 points, endpoints included); the representative
`UT=171496.6-193774.6` Kerbin checkpoint adds 42 points and short 300s windows
add none. Format-v6 `.prec` sidecars preserve those checkpoint frames, the
optimizer trims them with the section instead of dropping them as noise, playback
logs the checkpoint point and resolved world position, and map-source decisions
now log `Segment` / `TerminalOrbit` / `StateVector` / `None` source detail in one
structured line.

---

## ~~572. Landed capsule was not spawned at the end of its recording~~

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. "the capsule I landed was
not spawned at the end of the recording."

**Diagnosis (2026-04-25):** duplicate of already-closed #570. The capsule the
user described is recording `79a0fa28567c4b9494e7bc5797718037` ("Kerbal X" #15,
terminal=Splashed terminalUT=194183.0, endUT=194195.7). It was queued for
post-warp spawn at `KSP.log` line 537676 (`Deferred spawn during warp: #15
"Kerbal X"`) and then logged 1815× as `Deferred spawn kept in queue (outside
physics bubble): #15 "Kerbal X"` between lines 537702 and 541400. That
diagnostic line is emitted only by the pre-#534 bubble gate in
`ParsekPlaybackPolicy.FlushDeferredSpawns` — the captured commit `ef63407a`
on `bug/extrapolator-destroyed-on-subsurface` predates the merge of `fcb8a656`
(PR #534) on that branch. Current `main` removed `ShouldDeferSpawnOutsideBubble`
entirely; the spawn would now fire the first non-warp frame.

`ShouldSpawnAtRecordingEnd` correctly flagged the recording as needing
materialization (`Spawn #15 'Kerbal X' UT=194195.7 terminal=Splashed` timeline
row at line 23799). Only the now-deleted bubble gate held the spawn. Existing
regression `DeferredSpawnTests.FlushDeferredSpawns_SpawnsQueuedSplashedSurvivorAfterWarpEnds`
already pins this case.

**Action:** re-test on a build with the post-`fcb8a656` fix tail (any current
`main` build); the symptom will not reproduce. Closed as a duplicate of #570.

**Status:** CLOSED 2026-04-25 (duplicate of #570).

---

## ~~615. KSC ghost playback interpreted v6 RELATIVE offsets as lat/lon/alt~~

**Source:** PR #594 review follow-up. This is a separate latent playback bug
from `#613` / the RelativeAnchorResolution retire work: it affects the Space
Center visual ghost path even when no anchor-retire suppression is involved.

**Diagnosis (2026-04-26):** `ParsekKSC.InterpolateAndPositionKsc` accepted the
flat `Recording.Points` list and called `body.GetWorldSurfacePosition` on
`TrajectoryPoint.latitude` / `longitude` / `altitude` without first resolving
the covering `TrackSection.referenceFrame`. For format-v6 `ReferenceFrame.Relative`
sections those fields are anchor-local Cartesian metres, not geographic
coordinates, so KSC ghosts could be drawn deep underground while the flight
playback path resolved the same data correctly through the anchor vessel.

**Fix:** `InterpolateAndPositionKsc` now takes the full `Recording`, selects the
active `TrackSection` (using section-local frames when available), and routes
RELATIVE sections through `TrajectoryMath.ResolveRelativePlaybackPosition` /
`ResolveRelativePlaybackRotation` using `FlightRecorder.FindVesselByPid`, matching
flight-scene playback. If the anchor is not resolvable before the ghost has a
valid pose, the KSC ghost stays hidden; after a valid pose, it freezes at its
last known transform with a WARN / Verbose diagnostic instead of reinterpreting
dx/dy/dz as lat/lon/alt. Destroyed-recording explosion FX may still use that
last valid frozen pose, but never an unpositioned default transform. Absolute,
OrbitalCheckpoint, and no-section playback continue through the body surface
lookup path. `KscGhostPlaybackTests` pins resolvable relative playback,
unresolved-anchor hide/freeze semantics, cache reset across frame sources,
frozen-pose explosion eligibility, and the unchanged absolute/checkpoint/no-section
paths. `SceneAndPatch.ParsekKscRelativePlaybackUsesLiveAnchor` adds a live KSP
runtime canary for the production KSC surface/anchor lookup path.

**Status:** CLOSED 2026-04-26. Fixed on `fix/ksc-relative-frame-dispatch`.

---

## ~~610. Quickload-resume tail trim destroys other vessels' continued recordings during Re-Fly load~~

**Source:** `logs/2026-04-26_0118_refly-postfix-still-broken/KSP.log`. Same
playtest as `#611`. The user reported "the exo recording of the upper
stage again disappeared after booster Re-Fly" — even though `#601`
(PR #575) was supposed to splice post-RP recordings back from the
committed tree before the committed copy is detached.

The splice DID run correctly:

- `01:11:28.092` `SpliceMissingCommittedRecordings: tree 'Kerbal X'
  loadedBefore=8 committed=10 after=10 splicedRecordings=2 refreshedRecordings=2`
- spliced ids = `2f1ac072` (capsule exo half) + `223cc6e2` (booster exo
  half); refreshed ids = `0b394bb5` (capsule atmo, root) + `3a9f8573`
  (booster atmo, the in-place continuation target).
- After splice the tree had all 10 recordings — including the capsule's
  exo half whose data spans `UT 279.7 → 696.17`.

But ~1.5 seconds later, the quickload-resume path still destroyed it:

- `01:11:29.555` `Quickload tree trim: tree='Kerbal X' cutoffUT=203.12
  trimmedRecordings=4/10 prunedFutureRecordings=2 prunedBranchPoints=1`
- The 2 future-only recordings pruned were exactly the 2 spliced exo
  halves (`StartUT >= 203.12`). The 4 trimmed recordings had their
  `Points/PartEvents/TrackSections` past `203.12` removed AND
  `ExplicitEndUT` clipped to `203.12`. The capsule atmo (`0b394bb5`)
  on disk now ends at `203.12` instead of its real terminal at
  `279.7` — the 76 seconds between the cutoff and the atmo→exo
  boundary are gone too.

**Cause (`Source/Parsek/FlightRecorder.cs:267-302` →
`Source/Parsek/ParsekScenario.cs:2619-2656`):**
`PrepareQuickloadResumeStateIfNeeded` always called
`TrimRecordingTreePastUT(ActiveTree, resumeUT)`, which clips every
recording in the tree to the cutoff UT and prunes recordings that
start past it. That contract is correct for F9 quickload (the world
genuinely rewound and every recording's post-cutoff data is stale)
but wrong for Re-Fly: the splice has just re-installed other-vessel
post-RP recordings as preserved forks, and the in-place continuation
only needs to scrub the active recording's tail so the recorder can
append fresh post-cutoff samples.

**Fix:** added a pure-function
`ChooseQuickloadTrimScope(treeId, marker, out reason)` on
`ParsekScenario` that returns `ActiveRecOnly` when
`ParsekScenario.Instance.ActiveReFlySessionMarker` pins this same
tree, and `TreeWide` otherwise (no marker, marker has no tree id,
or marker pins a different tree). `PrepareQuickloadResumeStateIfNeeded`
now consults the helper and calls `TrimRecordingPastUT(activeRec, ...)`
in the `ActiveRecOnly` branch (only the in-place continuation target's
tail is touched; siblings remain untouched as fork timelines). The
chosen scope + reason are appended to the `Quickload resume prep:`
log line — `trimScope=ActiveRecOnly (refly-active sess=… markerTree=… originRec=…)`
or `trimScope=TreeWide (no-active-refly-marker | refly-marker-tree-mismatch …)`
— so the branch is auditable from `KSP.log` alone.

**Unity-overload trap:** the natural `if (ParsekScenario.Instance != null)`
check returns false in unit tests because `ParsekScenario` is a
`UnityEngine.MonoBehaviour` whose overloaded `==` operator treats
non-Awake'd instances as fake-destroyed. Production used `?.` which
the C# spec defines as reference-equality; we mirrored that
(`var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;`)
so the integration test for the `ActiveRecOnly` branch actually
exercises the marker path.

**Tests:** 8 new cases in `QuickloadResumeTests` —
`ChooseQuickloadTrimScope_NoMarker_ReturnsTreeWide`,
`ChooseQuickloadTrimScope_MarkerWithoutTreeId_ReturnsTreeWide`,
`ChooseQuickloadTrimScope_MarkerForDifferentTree_ReturnsTreeWide`,
`ChooseQuickloadTrimScope_MarkerForThisTree_ReturnsActiveRecOnly`,
`ChooseQuickloadTrimScope_NullResumeTreeId_ReturnsTreeWide`,
`PrepareQuickloadResumeStateIfNeeded_ReFlyActive_TrimsActiveOnlyKeepsSibling`
(reproduces the production shape — sibling recording starting past
cutoff is preserved with all its data + track sections + part events),
`PrepareQuickloadResumeStateIfNeeded_NoReFlyMarker_KeepsTreeWideTrim`
(sanity for F9 quickload), and
`PrepareQuickloadResumeStateIfNeeded_ReFlyMarkerForOtherTree_KeepsTreeWideTrim`
(stale/unrelated marker can't accidentally protect a different
tree). All 8947 tests pass.

**Status:** Open until merged.

---

## ~~611. Re-Fly doubled-vessel suppression silently fails when active tree is in PendingTree (load window)~~

**Source:** `logs/2026-04-26_0118_refly-postfix-still-broken/KSP.log`. After
`#587 third facet` (PR #574) and its P2 review follow-up landed and shipped,
the user re-flew the booster again and reported the same long-standing
"upper stage doubled — real vessel copy" symptom they have reported across
~6 playtests. PR #574's parent-chain gate was supposed to suppress the
doubled `Ghost: Kerbal X` ProtoVessel, but in this playtest the gate
**silently** declined to suppress (`create-state-vector-done` instead of
`create-state-vector-suppressed`), and the user could click "aim camera"
on the doubled ProtoVessel's parts to confirm it was a real KSP `Vessel`.

The diagnosis took longer than it should have because the predicate emits
**no log when it returns false** — every reject branch is silent. The only
forensic evidence of failed suppression is the absence of the
`create-state-vector-suppressed` line. We had to read the source to
discover that the BFS walk was searching `RecordingStore.CommittedTrees`,
which has been emptied by the time the gate fires (because
`TryRestoreActiveTreeNode` calls `RemoveCommittedTreeById` after the
splice, leaving the freshly-loaded tree only in `PendingTree`).

**Concrete log evidence in this playtest:**

- `01:11:28.092` SpliceMissingCommittedRecordings finishes
  (`splicedRecordings=2 refreshedRecordings=2`); the load tree is now
  stashed Pending-Limbo, and the committed counterpart is removed.
- `01:11:29.460` Created ghost vessel `Ghost: Kerbal X` for recording #0
  (`0b394bb5...`), the capsule's atmo half. `sma=2 ecc=1.000000`,
  `frame=relative`, `anchorPid=3026957949` (the active booster).
  Predicate didn't fire — the user sees a clickable, aim-camera-able
  ProtoVessel co-located with the booster.
- `01:12:46.250` Recording #1 (`2f1ac072...`, the capsule's exo half)
  reaches the same gate; this time the predicate fires
  (`reason=refly-relative-anchor=active relationship=parent`). So the
  parent-chain logic IS correct — it was the topology lookup that
  failed for #0. The atmo half slipped through because the BFS bailed
  on `active-not-found` (active recording was in PendingTree, not
  searched).

**Cause (`Source/Parsek/GhostMapPresence.cs:776-849`):**
`IsRecordingInParentChainOfActiveReFly` was called with
`RecordingStore.CommittedTrees` only. The active recording's tree had
been moved to `PendingTree` by the load-side splice path. The BFS walk
silently returned false, the predicate returned
`not-suppressed-not-parent-of-refly-target`, and the doubled ProtoVessel
got created.

**Fix (`fix/refly-pending-tree-and-observability`):** added a
`ComposeSearchTreesForReFlySuppression(committedTrees, pendingTree)`
helper that the production call site now uses to compose committed +
pending into the helper's search list. `IsRecordingInParentChainOfActiveReFly`
gained an `out string walkTrace` parameter populated with one of four
explicit termination reasons (`active-not-found` /
`active-has-no-parent-bp` / `found-victim-in-parent-chain` /
`exhausted-without-victim`) plus visited-BP ids and parents-encountered
ids. The trace is bubbled into the predicate's `suppressReason` for both
the suppressed and not-suppressed paths, and a new Verbose
`[GhostMap] create-state-vector-not-suppressed-during-refly` decision
line fires whenever a Re-Fly session is active but the predicate
declined to suppress — making future "predicate didn't fire" diagnoses
readable from `KSP.log` alone, no source-reading required.

**P1 review follow-up:** the initial fix added the pending tree to the
parent-chain BFS but the predicate has TWO gates against the load
window — a separate active-recording PID lookup at the top of
`ShouldSuppressStateVectorProtoVesselForActiveReFly` was still
walking only the flat `RecordingStore.CommittedRecordings` list. At
load time `RemoveCommittedTreeById` has emptied that list for this
tree, so the gate bailed with `not-suppressed-active-rec-pid-unknown`
BEFORE the new BFS pending-tree path could run, and the doubled
ProtoVessel still got created. The PID lookup now walks the same
composed search trees first (resolving the active recording's
`VesselPersistentId` directly from the tree's `Recordings` map) and
falls back to the flat list only if the trees can't yield a non-zero
PID. The success reason now carries `activePidSource=search-tree:<id>`
or `activePidSource=committed-recordings-flat-list` so the load-window
vs steady-state distinction is auditable; the rejection reason carries
`searchTrees=<n> committedRecordings=<n> activeRecId=<id>` so a future
"predicate didn't fire" diagnosis can see exactly which lookup source
came up empty.

**Tests:** 9 new cases in `Bug587ThirdFacetDoubledGhostMapTests` —
`ComposeSearchTreesForReFlySuppression_NoPending_ReturnsCommittedAsIs`,
`ComposeSearchTreesForReFlySuppression_NullCommitted_ReturnsEmptyOrPendingOnly`,
`ComposeSearchTreesForReFlySuppression_PendingDistinctFromCommitted_AppendsPending`,
`ComposeSearchTreesForReFlySuppression_PendingSameIdAsCommitted_KeepsPendingDropsCommitted`,
`IsRecordingInParentChainOfActiveReFly_ActiveInPendingTree_FoundViaSearchList`,
`IsRecordingInParentChainOfActiveReFly_WalkTrace_ExhaustedShape`,
`IsRecordingInParentChainOfActiveReFly_WalkTrace_ActiveNotFoundShape`,
`Suppresses_LoadWindowShape_EmptyCommittedRecordings_ActiveInPendingTree`
(P1 follow-up: reproduces the exact production load-window shape —
empty `committedRecordings` plus the active recording in the composed
search trees, with `VesselPersistentId` set on the tree's recording —
and asserts the success reason carries `activePidSource=search-tree:`),
and `NotSuppressed_LoadWindowShape_ActiveMissingEverywhere_ReportsZeroCounts`
(asserts the new rejection reason format). The existing
`NotSuppressed_WhenCommittedListIsNull` test was updated to reflect
the unified bail behavior; the existing `Suppresses_…_VictimIsParent`
and docking-target no-suppress tests use `Assert.StartsWith` since
`suppressReason` now carries the appended `activePidSource` and
`walkTrace` strings.

**Status:** Open until merged.

---

## 613. Relative-frame ghost playback retains stale anchor pid after Re-Fly rewind, freezing the ghost at world origin

**Source:** `logs/2026-04-26_1025_3bugs-refly/KSP.log`. Bug B in the
3-bug post-fix playtest. After a Re-Fly rewind, recording #9 ("Kerbal X")
entered its first relative-frame track section (`UT=199.3-214.7`,
`anchorPid=3151978247`) at line ~17943. The very next line emitted the
WARN `[Anchor] RELATIVE playback: anchor vessel pid=3151978247 not found
— ghost frozen at last known position`, and the subsequent
`[GhostAppearance]` line reported `root=(0.00,0.00,0.00)` with
`rootRot=identity` and a render distance of `1140719m`. The recorded
anchor pid (`3151978247`) had been the active Re-Fly probe; it was
destroyed in the background at 10:14:23 (line ~11001
`[BgRecorder] Background vessel destroyed`) and the rewind erased it from
the future, so the post-rewind `FlightGlobals.Vessels` never contained
that pid again.

**PID lifecycle (verified):** `TrackSection.anchorVesselId` (uint pid) is
captured by `FlightRecorder.ApplyRelativeOffset` at recording time
(`Source/Parsek/FlightRecorder.cs:5566`) and persisted in the recording's
on-disk track sections (`RecordingStore.cs:5132`,
`TrajectorySidecarBinary.cs:631`). It is **never rewritten** after the
recording finalizes — recordings are immutable across rewinds. After a
Re-Fly rewind the recording sits in `RecordingStore.CommittedRecordings`
with the original anchor pid intact, but the live `FlightGlobals.Vessels`
no longer contains that pid (the anchor's recording-side
`Vessel.persistentId` is gone with the destroyed-future strip).
`FlightRecorder.FindVesselByPid(3151978247)` then returns null on every
playback frame. The ghost-map presence path already gracefully defers in
this case (`GhostMapPresence.TryResolveStateVectorMapPointPure` returns
`relative-frame-anchor-unresolved`), but the in-flight ghost-positioning
path was still using the older "freeze at last known position" branch,
which renders a freshly-spawned ghost at `(0,0,0)`.

**Fix direction:** retire (hide) the ghost during the relative section
rather than re-resolve. Re-resolution by name was rejected: the recorded
anchor was a transient sibling vessel (a decoupled probe) with no stable
identifier — there is nothing to rebind to in the post-rewind scene. The
fix in `Source/Parsek/ParsekFlight.cs` (`InterpolateAndPositionRelative`
~line 15264, `PositionGhostRelativeAt` ~line 15400) replaces the
freeze-in-place branch with `ghost.SetActive(false)` plus a one-shot
per-(recordingIndex, anchorPid) WARN under the `[Anchor]` tag carrying a
greppable `relative-anchor-retired` keyword. Hiding strictly dominates
freezing: if the anchor reappears on a later frame the engine re-enters
the same method and repositions; if it never reappears, the ghost stays
gracefully ungraphable instead of marooned at world origin with bogus
distance reports. The `LateUpdate` Relative-mode path in `ParsekFlight`
already deactivated the ghost on null anchor, so this brings the two
positioning entry points into alignment.

**Decision helper:** new pure static `RelativeAnchorResolution` (Decide /
DedupeKey / FormatRetiredMessage) so the resolver decision is unit
testable with an injectable resolver delegate, mirroring the existing
`GhostMapPresence.AnchorResolvableForTesting` pattern.

**Tests:** 21 cases in `Source/Parsek.Tests/RelativeAnchorResolutionTests.cs`
covering: Decide outcomes (Resolved / Retired / pid==0 short-circuit /
null resolver), DedupeKey uniqueness across (recIdx, pid) combos and high
bit handling, FormatRetiredMessage greppable keyword + identifying field
inclusion + null-vessel-name placeholder + Re-Fly root-cause mention, the
log-assertion case that pipes the formatted message through
`ParsekLog.Warn("Anchor", ...)` and asserts the resulting line carries
`[WARN]`, `[Anchor]`, and `relative-anchor-retired`, and the rewind
scenario coverage (anchor still alive in post-rewind FlightGlobals -->
Resolved with no spurious retirement; anchor erased --> Retired with no
freeze-path reachable). The Outcome enum has a defensive shape test that
locks the contract to exactly two values, blocking any future "partial
positioning" outcome. The P1-fix follow-up adds 5 cases pinning the
`ShouldSkipPostPositionPipeline` predicate (true/false round-trip + pure
function), the `GhostPlaybackState.anchorRetiredThisFrame` default + reset
through `ClearLoadedVisualReferences`, and a 3-frame integration scenario
that mirrors the production engine + positioner contract: frame 1 sets
the flag and emits the one-shot WARN, frame 2 re-enters the retire branch
on the same key without re-emitting the WARN, frame 3 resolves the anchor
and the gate lets the activation pipeline run again. In-game coverage
(`Source/Parsek/InGameTests/RuntimeTests.cs`,
`Bug613_RetiredAnchor_EndsFrameInactive_NoAppearance` /
`Bug613_DeferredSyncWithResolvedAnchor_StillActivates` /
`Bug613_PerFrameClear_StaleFlagDoesNotLeak`) drives a full
`engine.UpdatePlayback` frame with a mock `IGhostPositioner` whose
`InterpolateAndPositionRelative` mimics the production retire branch
(`SetActive(false)` plus `state.anchorRetiredThisFrame = true`); the
asserts pin `ghost.activeSelf == false`, `appearanceCount == 0`, and the
absence of any `[GhostAppearance]` log line on a retired frame, plus the
positive case where deferred-sync activation still flips the ghost active
when the anchor is resolved.

**P1 review narrative (PR #594):** The first commit retired the ghost
correctly inside the positioner (`SetActive(false)` plus the WARN), but
review noticed that in the same `engine.RenderInRangeGhost` frame
`ActivateGhostVisualsIfNeeded` ran unconditionally after positioning and
flipped the ghost back to active before the frame returned. The visible
symptom was unchanged from the original bug B repro: a (0,0,0) ghost
appearance for one rendered frame on every per-frame call inside the
relative section. Two fix shapes were considered:

- **Option (a):** thread an explicit out-parameter / return value
  (`AnchorOutcome` enum) up through `InterpolateAndPositionRelative` and
  `PositionLoopGhost` so the engine can branch directly on the decision.
  Cleanest signal but five callsites in the engine (`RenderInRangeGhost`,
  loop-playback main, loop-primary, loop-overlap, and the `WatchSync`
  rebuild path) plus the loop-pause window each need to consume the new
  shape, and the watch-sync path crosses an additional helper boundary
  (`PositionLoadedGhostAtPlaybackUT`) that does not currently take the
  positioner. Touches the `IGhostPositioner` interface contract.

- **Option (b, shipped):** single `bool anchorRetiredThisFrame` on
  `GhostPlaybackState`. Engine clears it before each per-frame call to
  the positioner; positioner sets it true on the retire branch; the
  engine's existing post-position pipeline reads it and skips the
  visuals + activation + appearance steps. Pure predicate
  `RelativeAnchorResolution.ShouldSkipPostPositionPipeline(bool)` named
  the gate so the call sites are reviewable from xUnit. No
  `IGhostPositioner` signature churn, no new event types, and the flag's
  one-frame scope is self-documenting.

The shipped fix gates six engine callsites (`RenderInRangeGhost` line
~1035, loop-playback main line ~1457, `HandleLoopPauseWindow` line ~1865
caught in the P1 second pass, primary-loop overlap line ~1659,
`OverlapGhost` loop line ~1796, and the `SynchronizeLoadedGhostForWatch`
watch-sync rebuild line ~3667). Each gate calls
`ApplyFrameVisuals(skipPartEvents=true, suppressVisualFx=true)` to tear
down any previously-emitting plumes/audio, then early-returns / falls
through past `ActivateGhostVisualsIfNeeded` and `TrackGhostAppearance`.
The retire branch in `ParsekFlight.InterpolateAndPositionRelative` and
`PositionGhostRelativeAt` (the `PositionLoopGhost` callee) now takes an
extra `GhostPlaybackState retireSignalState` argument that may be null in
test fixtures that drive the method without a state object; the
production callsites always pass the live state.

**P1 review narrative (PR #594, round 2):** A second review pass caught
that the six visibility gates above only cover
`ActivateGhostVisualsIfNeeded` + `TrackGhostAppearance` + transient
`ApplyFrameVisuals` events. Three additional code paths still ran
side-effect helpers (explosion FX, completion-event queueing, loop
camera-action / restart payloads) from the stale (0,0,0) transform of
the just-hidden ghost when the relative anchor was unresolvable:

1. `RenderInRangeGhost` falling through to
   `TryHandleEarlyDestroyedDebrisCompletion` (line ~1070) with
   `ghostActive` computed BEFORE positioning. On a retired frame the
   helper still called `TriggerExplosionIfDestroyed` against the stale
   transform, marked `explosionFired`/`completed`, and queued a
   `PlaybackCompletedEvent` that policy handlers would react to.
   Fix: gate the call on `!retired`; emit a one-shot
   `early-completion suppressed: anchor retired` Verbose log when
   skipping. If the recording was a legitimate early-debris destruction,
   the next replay frame with a resolvable anchor handles the completion.

2. `UpdateLoopingPlayback` cycle-change endpoint (line ~1296):
   `PositionGhostAtLoopEndpoint` routes through `positioner.PositionLoop`
   which CAN raise `state.anchorRetiredThisFrame`, but the immediately
   following block called `TriggerExplosionIfDestroyed`, emitted
   `OnLoopCameraAction(ExplosionHoldStart/End)` with
   `AnchorPosition = state.ghost.transform.position`, and emitted
   `OnLoopRestarted` with `ExplosionPosition` from the same stale
   transform. Fix: clear the flag before `PositionGhostAtLoopEndpoint`,
   read it back, suppress all four side effects (explosion + camera +
   restart event + retarget event) when retired. Suppression log:
   `loop endpoint side effects suppressed: anchor retired ghost #N
   "vesselName" cycle=K`.

3. `UpdateExpireAndPositionOverlaps` overlap-expiry endpoint (line
   ~1723): same pattern — `PositionGhostAtLoopEndpoint` followed by
   `TriggerExplosionIfDestroyed` + `OnOverlapCameraAction` +
   `OnOverlapExpired`, all reading `ovState.ghost.transform.position`.
   Fix: identical clear-then-check-flag wrap; suppress on retired.

4. `HandleLoopPauseWindow` (line ~1865): the existing visibility gate
   ran AFTER `TriggerExplosionIfDestroyed`. P3 follow-up: the retire
   early-return only called `HideAllGhostParts` (which itself only
   calls `MuteAllAudio`) so previously-emitting engine plumes / RCS /
   reentry FX continued rendering at the (0,0,0) retired position. Fix:
   gate `TriggerExplosionIfDestroyed` on `!loopPauseRetired`, and add
   `ApplyFrameVisuals(skipPartEvents:true, suppressVisualFx:true,
   allowTransientEffects:false)` before the early-return so the FX
   teardown matches the contract of the other five visibility gates.
   Suppression log: `loop endpoint side effects suppressed: anchor
   retired ghost #N "vesselName" loop-pause`.

The total surface is now **6 visibility gates** + **3 endpoint
side-effect gates** (loop cycle endpoint, overlap expiry, loop pause)
+ **1 early-completion gate** in `RenderInRangeGhost`. In-game tests
(`Source/Parsek/InGameTests/RuntimeTests.cs`,
`Bug613_RetireDuringRender_DoesNotFireEarlyDestroyedCompletion`,
`Bug613_ResolvedAnchorFiresEarlyDestroyedCompletion`,
`Bug613_RetireDuringLoopCycleEndpoint_NoExplosionOrCameraRestart`,
`Bug613_ResolvedAnchorFiresLoopCycleEndpoint`,
`Bug613_RetireDuringOverlapExpiry_NoExplosionOrCamera`,
`Bug613_ResolvedAnchorFiresOverlapExpiry`,
`Bug613_RetireDuringLoopPause_StopsEngineFx`,
`Bug613_ResolvedAnchorLoopPauseFiresExplosion`) extend the existing
`Bug613_*` mock-positioner harness with a `SetsLoopRetireFlag` knob and
two new test trajectories (`Bug613EarlyDebrisRelativeTrajectory` for
the early-debris path, `Bug613LoopRelativeTrajectory` for the three
loop-endpoint paths). Each retire test asserts `explosionFired==false`,
no event-list emissions, and the suppression log line; each negative
test asserts the same side effects DO fire when the anchor resolves.

**P1 review follow-up (finalize-spawn retarget):** PR #594's gates
covered continuing playback, loop endpoints, overlap expiry, loop pause,
and early-completion side effects, but `FinalizePendingSpawnLifecycle`
still ran immediately after `PrimeLoadedGhostForPlaybackUT`. On a fresh
loop or overlap-primary spawn whose first priming pass landed inside a
Relative section with an unresolvable anchor, the priming positioner set
`state.anchorRetiredThisFrame = true`, hid the ghost, and left its pivot
under the just-built origin-positioned hierarchy. The pending lifecycle
then cleared normally and emitted `OnLoopCameraAction` /
`OnOverlapCameraAction(RetargetToNewGhost)` with
`GhostPivot = state.cameraPivot`. Watch mode could anchor at world origin
for one frame before the next per-frame gate suppressed continuing
playback. Fix: `FinalizePendingSpawnLifecycle` now leaves
`OnGhostCreated`, pending-lifecycle cleanup, flag visibility, and range
entry logging intact, but suppresses only the `RetargetToNewGhost`
camera side effect when `state.anchorRetiredThisFrame` is true. It emits
the greppable Verbose line `finalize-spawn retire: suppressing
RetargetToNewGhost (anchor retired on first spawn) ghost #N
"vesselName"` with the lifecycle and cycle context. In-game regressions
`Bug613_FreshSpawnIntoUnresolvedRelativeSection_NoRetargetAtOrigin` and
`Bug613_FreshSpawnWithResolvedAnchor_StillFiresRetarget` drive both
`LoopEnter` and `OverlapPrimaryEnter` fresh-spawn paths through the
mock relative positioner, asserting that retired anchors keep the state
machine and `OnGhostCreated` path complete without camera retargeting,
while resolved anchors still emit exactly one retarget with the spawned
state's `cameraPivot`.

**Status:** Open until merged.

---

## ~~616. Re-Fly load creates a clickable GhostMap ProtoVessel for the upper-stage ghost before the relative section begins~~

**Source:** `logs/2026-04-26_1522_refly-1332-postmerge/KSP.log`.
This is the user's "real/clickable upper-stage duplicate" report after
choosing Fly for the booster Re-Fly, but the duplicate is not a terminal
spawned vessel. It is the lightweight GhostMap / ProtoVessel presence used
for map targeting and marker interaction.

**Evidence:** recording `#0` / `854fdf7703a3416d8750da7bba9a26af`
created a `Ghost: Kerbal X` ProtoVessel at `15:12:55.943`:

- `KSP.log:14873`:
  `create-state-vector-not-suppressed-during-refly ... branch=no-section ...
  reason=not-suppressed-not-relative-frame`
- `KSP.log:14874`:
  `Created ghost vessel 'Ghost: Kerbal X' ghostPid=3652013656 ...`
- `KSP.log:15103`, `15116`, `15129`, `15150`, `15263`, `15746`,
  `15905`, `15929`, `15944`, and `15961` then update the same
  `ghostPid=3652013656` through `update-state-vector ... branch=Relative
  ... anchorPid=3314061462`, with `localOffset` growing from roughly
  `13.2m` to `52.7m`.

That is the visible/clickable duplicate tracking the live booster/probe at a
recorded relative offset. `KSP.log:14879` shows the native orbit icon hidden
for GhostOrbitLine's `terminal-below-atmosphere` reason (`drawIcons=NONE
iconSuppressed=True`); that is not the Re-Fly suppression gate. The important
point is that the ProtoVessel itself remains registered and therefore can
still participate in click / targeting / marker behavior even when the native
icon is hidden.

**Cause:** PR #601's active-Re-Fly relative-anchor suppression is only checked
when `GhostMapPresence.CreateGhostVesselFromStateVectors` creates a new
ProtoVessel. In this repro, the create attempt for `#0` runs at UT `126.6`,
before the state-vector resolver has entered a relative section, so
`ShouldSuppressStateVectorProtoVesselForActiveReFly` rejects suppression with
`not-suppressed-not-relative-frame`. A few seconds later the already-created
ProtoVessel transitions into a relative section anchored to the active Re-Fly
target; `GhostMapPresence.UpdateGhostOrbitFromStateVectors` updates its orbit
without re-running the active-Re-Fly suppression gate.

**Relevant code:** `Source/Parsek/GhostMapPresence.cs`:
`CreateGhostVesselFromStateVectors` runs
`ShouldSuppressStateVectorProtoVesselForActiveReFly` before loading the
ProtoVessel, but `UpdateGhostOrbitFromStateVectors` resolves the same
state-vector branch and immediately calls `orbit.UpdateFromStateVectors`
without the matching gate.

**Fix direction:** add a defense on both surfaces:

- Preferred create-time guard: if an active in-place Re-Fly marker is live,
  the victim recording is in the active target's parent/ancestor chain, and
  any upcoming relative section in the recording would use the active target
  as the anchor, defer or suppress GhostMap creation before the bad section is
  reached.
- Required update-time guard: before `UpdateGhostOrbitFromStateVectors` applies
  a `branch=Relative` update, run the same parent-chain + active-anchor
  predicate and remove/defer the existing GhostMap vessel when it flips true.
  This is the safety net for already-created ProtoVessels and for any future
  create-time lookahead miss.

Tests should cover: create allowed in an absolute prefix but update suppressed
on later relative section; create suppressed when already in the bad relative
section; unrelated/docking relative anchors remain allowed; pending-tree search
continues to work for in-place Re-Fly load windows.

**Resolution (2026-04-26):** CLOSED for v0.8.3. `GhostMapPresence` now runs an active-Re-Fly relative-section lookahead at state-vector ProtoVessel create time, so a recording created during its absolute/no-section prefix is still deferred if a later relative section would anchor to the active Re-Fly target through the parent-chain predicate. The per-frame state-vector update path now returns an update-time suppression decision when an already-created map ghost transitions into that unsafe relative branch; Flight Map View removes and re-defers the entry, while Tracking Station queues the removal until after its dictionary enumeration. Regression coverage lives in `Bug616GhostMapReFlyLookaheadTests` plus the existing GhostMap ancestor/third-facet suites.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~617. Re-Fly upper-stage live playback still intermittently takes the relative-anchor retire path despite recorded fallback succeeding nearby~~

**Source:** `logs/2026-04-26_1522_refly-1332-postmerge/KSP.log`.
This is the relative-anchor lock / pop-out observed for the post-178s
upper-stage tail recording `e77d90b616b94fe58d4c9f68f6448d35`.

**Evidence:** at `KSP.log:16055-16069`, recording `#1` / `e77d90...`
starts a relative playback section anchored to the active booster/probe:

- `KSP.log:16055`: `RELATIVE playback started ... anchorPid=3314061462`
- `KSP.log:16056`: relative offset `|offset|=55.77m ... source=recorded`
- `KSP.log:16058`: `Ghost #1 "Kerbal X" spawned`
- `KSP.log:16060`: `[Anchor] relative-anchor-retired:
  callsite=InterpolateAndPositionRelative ... anchorPid=3314061462`
- `KSP.log:16068`: the GhostMap create path for the same recording is
  correctly suppressed with
  `reason=refly-relative-anchor=active relationship=parent`
- `KSP.log:16069`: a `GhostAppearance` still records the playback ghost in
  `activeFrame=Relative` with `anchorPid=3314061462`

This proves the parent-chain / active-anchor predicate is sound: GhostMap sees
the bad relationship and suppresses it. The live playback path still reaches
the retire/hide branch in at least one invocation. The precise mechanics need
one more trace pass before implementation: `KSP.log:16056` logs
`source=recorded` for the same recording and millisecond as the
`relative-anchor-retired` WARN, and `KSP.log:16070` logs `source=recorded`
again two seconds later.

**Cause candidates:** PR #601 clearly did not make all live relative playback
surfaces consistent, but the "missed callsite" is not proven yet. Two shapes
fit the log:

- Two distinct playback invocations reach the same
  `InterpolateAndPositionRelative` retire branch on the same frame. One path
  uses the recorded fallback successfully, while a loop / overlap / watch-sync
  or other sibling surface still retires the ghost. The hardcoded
  `callsite=InterpolateAndPositionRelative` string would make both surfaces
  look identical in `KSP.log`.
- A single resolver path succeeds on most frames but fails for a narrow
  coverage gap. For example, the active Re-Fly target's pre-merge / mutating
  anchor trajectory may not provide recorded anchor coverage for the requested
  UT, so `TryResolveRelativeAnchorPose` falls through to the retire branch on
  that frame.

Either way, for the in-place Re-Fly case the anchor pid is not an unrelated
missing vessel: it is the active Re-Fly target, and the victim recording is an
upper-stage parent-chain recording. The same relationship should not lock the
ghost to the live booster/probe or produce a stale retired-frame appearance.

**Relevant code:** `Source/Parsek/ParsekFlight.cs`:
`IGhostPositioner.InterpolateAndPositionRelative` delegates into
`InterpolateAndPositionRelative`, whose unresolved-anchor branch emits the
`RelativeAnchorResolution.FormatRetiredMessage(... "InterpolateAndPositionRelative")`
warning. `GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly`
already has the active-marker, pending-tree search, and parent-chain predicate
needed to classify this as the bad Re-Fly relationship.

**Fix direction:** first trace which surface emits the WARN and whether
`TryResolveRelativeAnchorPose` has partial-coverage failures for active Re-Fly
anchor recordings. Then share the PR #601 active-Re-Fly parent-chain decision
with every live relative positioner surface. For this specific in-place Re-Fly
relationship, do not lock the visual ghost to the live booster/probe and do
not produce a stale appearance from the retired branch. Either reconstruct
from the recorded anchor pose when available or suppress the visual/marker for
the session, but the behavior must be consistent across GhostMap create/update
and live playback. Regression coverage should pin both possible shapes:
multiple caller surfaces through the same positioner, and recorded-anchor
coverage gaps for the active Re-Fly target's pre-merge trajectory.

**Resolution (2026-04-26):** CLOSED for v0.8.3. The retire WARN was traced to partial recorded-anchor coverage in the live relative interpolation path, not to a separate third hardcoded callsite. `TryResolveRelativeAnchorPose` now explicitly selects the anchor frame source: unrelated recordings keep the fast live-anchor path, while active in-place Re-Fly parent-chain victims bypass the live active vessel, use exact recorded anchor coverage when available, and fall back to the nearest recorded absolute anchor pose before retiring. Large nearest-pose fallback gaps now emit a rate-limited `recorded-anchor-fallback-gap` WARN so visually suspicious snaps are visible in `KSP.log`. The selector is wired into the production resolver and covered by `RelativeAnchorResolutionTests`.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~618. Re-Fly merge cleanup only covers the active probe chain; optimizer-created upper-stage chain tips survive the merge~~

**Source:** `logs/2026-04-26_1522_refly-1332-postmerge/KSP.log`. This is the
"old booster recordings are not cleared correctly after merge" report and is
independent of the PR #601 relative-anchor wiring bugs.

**Evidence:** the original post-merge optimizer split the root upper-stage
recording:

- `KSP.log:13526`: `SplitAtSection: split
  854fdf7703a3416d8750da7bba9a26af at UT=178.0`
- `KSP.log:13527`: branch point parent updated from `854fdf...` to
  `e77d90b616b94fe58d4c9f68f6448d35`
- `KSP.log:13529`: split recorded as `'atmo' [8..178] + 'exo' [178..16743]`

During the later in-place Re-Fly merge, supersede/cleanup only considered the
probe chain:

- `KSP.log:18876`: in-place continuation branch starts for
  `b5c29201864344b7af4db82f6cc1897d`
- `KSP.log:18877`: target resolves from probe-chain head `b5c292...` to tip
  `7e0f795600c34137a763017caefd3da4`
- `KSP.log:18878`: `chain-skip-set` contains only
  `[b5c292...,7e0f795...,672978...,934879...]`
- `KSP.log:18881`: `Added 0 supersede relations for subtree rooted at
  b5c292...`

The upper-stage chain `c36da010...` (`854fdf...` + `e77d90...`) is never named
in the merge supersede set. After merge, `KSP.log:21382` and `21392` still
resolve the root and the `e77d90...` tip as not-crashed/orbiting, and the
timeline includes `Spawn #10 'Kerbal X'` from `e77d90...` at `KSP.log:21405`.

**Cause:** the in-place merge/supersede flow starts from the active Re-Fly
origin (`b5c292...`) and expands that recording's chain siblings. That is
correct for cleaning the prior probe attempt, but it does not also cover the
parent upper-stage chain that was split by the optimizer and remains attached
to the branch point as the ancestor of the active Re-Fly target. The merge
dialog and supersede code also mix topology leaf-ness with chain-terminal
semantics: `BuildDefaultVesselDecisions` can log `854fdf...` as a leaf even
though the effective terminal chain tip is `e77d90...`.

**Product/semantics note:** the upper-stage chain was created during
the original flight before any Re-Fly. There are two plausible intents:

- Treat the Re-Fly merge as replacing the whole stale future of this branch,
  so ancestor chains that were never separately confirmed should default to
  ghost-only or superseded cleanup.
- Treat the Re-Fly as an in-place continuation of one specific probe recording,
  preserving parent/ancestor flight history unless the user explicitly chooses
  otherwise.

The implemented fix takes the conservative interpretation: it does not add
unconditional destructive ancestor supersede relations. Instead, it changes the
merge dialog defaults so directly connected single-parent parent-chain terminal
tips that represent stale old-future materialization default to ghost-only while
the active in-place Re-Fly chain remains spawnable.

**Relevant code:** `MergeDialog.TryCommitReFlySupersede` builds the full-chain
skip set from the provisional's own `TreeId + ChainId + ChainBranch`, then
passes that target to `SupersedeCommit.AppendRelations`. That deliberately
avoids collapsing members of the same in-place flight, but it does not add the
ancestor/parent upper-stage chain as a cleanup candidate. `RecordingStore.RunOptimizationPass`
can create new chain tips before `TryCommitReFlySupersede` runs, so merge
decisions made before or without chain-tip awareness leave those new tips
visible/spawnable.

**Possible fix direction, if ancestor cleanup is confirmed:** teach the
re-fly merge cleanup to include ancestor chains that are logically superseded
by the in-place Re-Fly, not just the active target's own chain. The expansion
should walk from the active origin through the parent branch-point topology and
chain-predecessor/tip links, then apply the right action per relationship:
keep the active in-place chain visible as the new flight, but default stale
parent-chain terminal tips that represent the old future to ghost-only in the
dialog, with a user override before any history is discarded. Avoid
unconditional supersede of ancestor chains until the UX contract is clear.
`BuildDefaultVesselDecisions` should use effective chain terminal records when
deciding spawnability, so an optimizer-created tip like `e77d90...` cannot
evade the ghost-only decision through a head/tip mismatch.

Regression coverage should model the exact tree: root upper-stage head
`854fdf...`, optimizer tip `e77d90...`, branch point to active probe
`b5c292...`, and probe chain tips `672978...` / `7e0f795...` /
`934879...`. Expected outcome: the active probe chain remains the new in-place
flight; stale upper-stage parent-chain tips do not stay as visible/spawnable
recordings after the merge.

**Resolution (2026-04-26):** CLOSED for v0.8.3. `EffectiveState.ResolveChainTerminalRecording` now accepts pending-tree context, and `MergeDialog.BuildDefaultVesselDecisions` applies an active-Re-Fly parent-chain pass that resolves optimizer-created chain tips such as `854fdf... -> e77d90...` before deciding spawnability. The pass only affects directly connected single-parent ancestor-chain terminal tips and leaves multi-parent/destructive cleanup semantics alone. Regression coverage: `Bug618ReFlyMergeParentChainTipTests`.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~619. Postmerge Re-Fly log follow-ups: pre-pose ReentryFx, unresolved-distance formatting, and delayed phantom cleanup~~

**Source:** `logs/2026-04-26_1522_refly-1332-postmerge/KSP.log`. These are
secondary findings from the same investigation; they are not the main Bug A/B/C
root causes but should be tracked so they are not mistaken for fixed behavior.

**Aero / Reentry FX pre-pose:** `KSP.log:14573` shows the initial
`recording-start-snapshot` build path, and `KSP.log:14862` activates reentry
FX for ghost `#0 "Kerbal X"` at `alt=0m` before the playback pose has settled.
The existing `suppressVisualFx` fixes cover priming/reposition paths such as
`PrimeLoadedGhostForPlaybackUT`, but this snapshot-spawn path still lets
`UpdateReentryFx` lazily build and activate FX at the hidden surface prime
pose. Fix direction: carry the same hidden-prime / not-yet-positioned visual-FX
suppression into the recording-start-snapshot spawn path.

**Unresolved relative distance log:** `KSP.log:16071` logs a zone transition
with `dist=179769313486232...m`, which is `double.MaxValue` formatted as a
literal distance. `ParsekFlight.ResolveUnresolvedRelativeSectionDistanceFallback`
intentionally returns `double.MaxValue` for unresolved relative sections, but
the zone-transition log should render that state as `unresolved` rather than a
physically meaningful distance. This is downstream of item 617's retire path;
fixing 617 should make it rarer, but the formatter should still be defensive.

**Phantom orbiting cleanup is late:** after the re-fly merge, a phantom
orbiting `Kerbal X` survived into the next rewind separation save. `KSP.log:19138`
strips one `Kerbal X` by name, and `KSP.log:19217` strips an orphaned orbiting
`Kerbal X` from `flightState`. That cleanup path works as a later quickload /
rewind defense, but merge-time cleanup should not rely on a subsequent rewind
to remove the artifact. This is likely downstream of item 618's stale
upper-stage chain-tip survival. If item 618 is deferred, keep this as a
defense-in-depth cleanup gap.

**Resolution (2026-04-26):** CLOSED for v0.8.3. The actionable log follow-ups are covered by the same post-merge Re-Fly pass: lazy reentry FX pending builds now wait until the fresh recording-start-snapshot ghost has completed its first playback sync instead of building from the hidden prime pose, and `RenderingZoneManager` formats unresolved/invalid distances as `unresolved`. The late phantom orbiting cleanup symptom is addressed by item 618's parent-chain terminal-tip ghost-only default, which prevents the stale optimizer-created upper-stage tip from remaining a spawnable merge default.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~620. Landed probe terminal spawn can produce a NaN orbit and enter a spawn-death retry loop~~

**Source:** `logs/2026-04-26_1522_refly-1332-postmerge/KSP.log`. This was first
noticed during the same postmerge Re-Fly investigation, but it is a separate
snapshot / spawn-integrity bug rather than a relative-anchor issue.

**Evidence:** later playback of `#9 "Kerbal X Probe"` reaches its landed
terminal materialization and immediately dies:

- `KSP.log:21340-21345`: playback completes, watch exits, and Parsek queues a
  warp-deferred spawn for `#9 "Kerbal X Probe"`.
- `KSP.log:21349`: collision bounds falls back because the spawn snapshot has
  `no PART subnodes`.
- `KSP.log:21354-21360`: Parsek respawns the vessel as `LANDED`.
- `KSP.log:21365-21374`: KSP reports `M : Infinity`, `Radius: NaN`,
  `vel: [NaN, NaN, NaN]`, then `[OrbitDriver Warning!]: Kerbal X Probe had a
  NaN Orbit and was removed.`
- `KSP.log:21375-21378`: Parsek detects the spawn death and resets the policy
  for another deferred spawn.

**Cause hypothesis:** the terminal landed snapshot is structurally incomplete
or corrupted for KSP load purposes. The missing `PART` subnodes already prove
the snapshot is not a normal loadable vessel tree, and KSP's NaN orbit removal
suggests the landed/orbit metadata written into the ProtoVessel is invalid or
not sufficiently normalized for a landed terminal spawn. The retry policy then
treats the immediate removal as a spawn-death retryable failure, which can
produce repeated respawn attempts instead of quarantining the bad snapshot.

**Fix direction:** investigate the snapshot source for recording
`7e0f795600c34137a763017caefd3da4` / `#9` and the landed spawn path in
`VesselSpawner`. Add a validation gate before terminal materialization:
snapshots with no `PART` subnodes or non-finite orbit/surface metadata should
not be loaded into KSP as real vessels. They should either remain ghost-only
with a clear WARN or be repaired from a valid terminal/live snapshot before
spawn. The spawn-death retry loop should also cap or permanently abandon
snapshots that KSP removes for NaN orbit immediately after load, so one corrupt
terminal cannot churn indefinitely.

**Resolution (2026-04-26):** CLOSED for v0.8.3. Terminal materialization now validates snapshots before loading them into KSP. Snapshots with no `PART` subnodes, non-finite surface/orbit metadata, invalid body references, or unrecoverable body/orbit provenance are rejected with a clear materialization reason and marked `SpawnAbandoned`/ghost-only instead of being retried until KSP removes them for NaN orbit. The guard covers the main flight respawn path, `SpawnAtPosition`, KSC fallback spawning, and chain-tip fallback spawning. Regression coverage lives in `SpawnSafetyNetTests`, `SpawnAuditFollowupTests`, `VesselGhosterTests`, and spawn collision wiring tests.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~621. Re-Fly parent-chain relative ghosts can use the mutated active recording as their "recorded" anchor~~

**Source:** `logs/2026-04-26_1657_refly-postmerge-followup/KSP.log`.

**Evidence:** the booster/probe snapshots do not contain upper-stage parts, so
the observed upper-stage double is not snapshot contamination. The upper stage
recording is a separate `Kerbal X` parent-chain recording whose relative
sections anchor to the booster/probe PID. During in-place Re-Fly, the recorded
anchor resolver could select the active Re-Fly recording after it had been
mutated by the new flight, so `source=recorded` still meant "the new booster
trajectory" rather than "the pre-Re-Fly booster trajectory." That made the
upper-stage ghost replay as a constant recorded offset from the live/new
booster path, producing the inaccurate trajectory the playtest reported.

**Resolution (2026-04-26):** CLOSED for v0.8.3. In-place Re-Fly invocation now
freezes the active recording's pre-Re-Fly trajectory payload. Parent-chain
relative-anchor playback prefers that frozen copy whenever it bypasses the live
active Re-Fly vessel, and never treats the live-mutating active recording itself
as the recorded anchor source for another recording.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~622. Re-Fly merge skips stale original booster exo tails as if they were new in-place chain members~~

**Source:** `logs/2026-04-26_1657_refly-postmerge-followup/KSP.log`.

**Evidence:** after booster Re-Fly, stale original exo segment
`a676f02df8fb4ad8b098e6a9c00ea37e` remained in the committed tree with no
supersede relation. The merge resolved the new in-place chain tip correctly
(`b2fd292a... -> 1e0e0eae...`) but built `chain-skip-set` from every recording
with the same `TreeId + ChainId + ChainBranch`, which included stale
`a676...` alongside new Re-Fly split segments `f594...` and `1e0...`.
`AppendRelations` therefore skipped the exact required relation
`old=a676... new=1e0...` as an extra self-link and wrote zero supersedes.

**Additional topology issue:** the original optimizer split also rewrote the
upper-stage branch point at UT `116.711` to parent recording `44b89...`, whose
recording starts at UT `170.561`. That stale parent pointer can distort
parent-chain walks after atmo/exo splitting.

**Resolution (2026-04-26):** CLOSED for v0.8.3. In-place Re-Fly origins are
tagged with the active session id, optimizer split children inherit that tag,
and merge-time self-skip only protects session-owned same-chain members.
Pre-existing same-chain optimizer tails are now superseded to the new chain tip.
Optimizer branch-point reassignment now checks the branch point UT and only
moves `ChildBranchPointId` / `ParentRecordingIds` to the second half when the
branch belongs to that half's time range.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~623. Parent-chain upper-stage ghosts use booster-relative playback during booster Re-Fly instead of planet-relative playback~~

**Source:** follow-up analysis from `logs/2026-04-26_1657_refly-postmerge-followup/KSP.log` and user clarification on 2026-04-26: ghost playback of the upper stage during booster Re-Fly should use the absolute trajectory relative to the planet, not a relative trajectory to the booster.

**Evidence:** relative mode is entered at recording time by proximity, not by Re-Fly playback intent. `FlightRecorder.UpdateAnchorDetection` selects the nearest valid loaded in-flight vessel and enters `ReferenceFrame.Relative` when it is inside the 2300m physics-bubble entry threshold, staying relative until 2500m. After staging, the upper stage and booster/probe were close, loaded, and in flight, so the upper-stage recording legitimately entered a relative section anchored to the booster/probe PID. `FlightRecorder.ApplyRelativeOffset` then overwrote the section's ordinary `TrajectoryPoint` lat/lon/alt fields with anchor-local offsets, leaving no independent planet-relative samples in the relative section payload. Later Re-Fly playback could only reconstruct through an anchor trajectory, which made the upper stage appear at a constant or inaccurate offset from the re-flown booster/probe.

**Resolution (2026-04-26):** CLOSED for v0.8.3. Recording format v7 adds `TrackSection.absoluteFrames`: relative sections continue storing anchor-local frames for docking/rendezvous playback, but also persist a planet-relative absolute shadow frame for each relative sample. The text and binary sidecar codecs round-trip the shadow payload, deep-copy and merge/optimizer trim paths preserve it, and active in-place Re-Fly parent-chain playback now prefers the absolute shadow path when the relative anchor is the active Re-Fly target. Old v6 relative recordings without shadow frames still fall back to the existing recorded-anchor reconstruction rather than being made unplayable.

**Follow-up (2026-04-26, `logs/2026-04-26_1923_refly-upper-stage-still-broken`):** the v7 path was active (`RELATIVE absolute shadow playback`), but the first absolute-shadow frame in the relative section was later than the section start. The visual ghost therefore clamped to the first later shadow point at the moment the Re-Fly loaded, producing the user's "initially behind the booster / inaccurate" path. Fixed by seeding a relative-section boundary frame with both the anchor-local payload and the planet-relative shadow at RELATIVE entry, plus a playback bridge that uses the previous absolute section's last point for already-recorded v7 sections whose shadow starts late.

**Tests:** `TrajectorySidecarBinaryTests.WriteRead_RelativeSection_PreservesAbsoluteShadowFrames`, `TrajectorySidecarBinaryTests.TryProbe_MapsVersionToEncodingAndSupport` v7 coverage, `TrajectorySidecarProbeVersionContractTests.Probe_V6SidecarV7Recording_RejectedAsStale`, `SessionMergerTests.ResolveOverlaps_TrimsRelativeAbsoluteShadowWithFrames`, `RecordingOptimizerTests` coverage for boring-tail and overlapping-section shadow-frame trims, and `RelativeAnchorResolutionTests.TryFindAbsoluteShadowBridgeFrame_UsesPriorAbsoluteSectionBoundary`.

**Status:** CLOSED 2026-04-26. Fixed for v0.8.3.

---

## ~~614. GhostMap parent-chain walk misses optimizer-split chain ancestors during Re-Fly~~

**Source:** `logs/2026-04-26_1025_3bugs-refly/KSP.log`. Follow-up to `#611`:
after the load-window pending-tree fix shipped, the user re-flew the booster
again and a `Ghost: Kerbal X` ProtoVessel was still created — this time for
the **root** recording (`c9df8d86b79b4f6b91c049696b5cc8e2`), an ancestor two
hops up the parent chain from the active Re-Fly target
(`89eff8431cb843b783e5dbe693ed30f2`). The direct parent
(`1d6d2116543245249c9ca7f26dd361f7`) was correctly suppressed at log line
~14640 (`reason=refly-relative-anchor=active relationship=parent`). The root
got `reason=not-suppressed-not-parent-of-refly-target` at log line ~14211
and the bogus state-vector ghost was created at log line ~14212 with
`sma=2 ecc=1.000000`.

**Cause (`Source/Parsek/GhostMapPresence.cs:976-1093`):** the BFS walk in
`IsRecordingInParentChainOfActiveReFly` only followed
`Recording.ParentBranchPointId` links. The user's tree topology was:
`c9df8d86 (root, ChainIndex=0)` -> [optimizer split] ->
`1d6d2116 (mid, ChainIndex=1)` -> [Breakup BP `f1c7b08f`] ->
`89eff8431 (leaf)`. The root -> mid edge is an **optimizer chain split**
(`RecordingStore.RunOptimizationSplitPass` at `RecordingStore.cs:2016-2049`):
the second half (`mid`) shares the root's `ChainId` + monotonically
incremented `ChainIndex`, but does NOT receive a `ParentBranchPointId`.
The comment at `RecordingStore.cs:2041-2046` is explicit: *"The chain
linkage (shared ChainId) connects it to subsequent segments. Code that
walks from a BranchPoint to the chain tip must follow ChainId, not just
ChildRecordingIds."* The reverse walk (used here) had the same bug — it
must follow `ChainId` predecessors, not just `ParentBranchPointId`.

The structured `walkTrace` in the failed log line confirms the BFS
terminated at mid: `walkTrace=(exhausted-without-victim
activeId=89eff843 treeId=50e9197d victim=c9df8d86 visitedBPs=1
parentsEncountered=1 bpsNotFound=0 bps=[f1c7b08f:parents=1]
parents=[1d6d2116])`. One BP visited, one parent recording reached
(mid), then exhausted — never traversed the chain link from mid to root.

**Fix (`fix/ghostmap-ancestor-chain-suppression`, PR #593):** the BFS now
walks BOTH `ParentBranchPointId` AND chain-predecessor links (same
`ChainId`, same `ChainBranch`, `ChainIndex - 1`) for every recording it
reaches, including the active recording itself (so a mid-chain active
seeds the chain-predecessor leg too). New helper
`GhostMapPresence.TryFindChainPredecessor(tree, rec)` encapsulates the
chain-predecessor lookup: returns null for standalone recordings,
`ChainIndex == 0`, and missing predecessors; scoped per-tree and
per-`ChainBranch` so legacy / future cross-tree clones cannot leak.

The `walkTrace` gains a `chainHops` counter that increments on every
chain-predecessor enqueue (active-seed + every fan-out hop) plus a
`chainHopsViaAncestors` counter for the fan-out subset. A future
regression of the same shape will surface as `chainHops=0` on a
topology that should have chain links — visible in `KSP.log` without
re-reading source. The previous trace string `active-has-no-parent-bp`
was renamed to `active-has-no-parent` because the bail condition is
now "neither BP-parent nor chain-predecessor", not BP-only.

**Tests:** 14 new cases in `Bug614GhostMapAncestorChainTests` covering
the user's exact 3-recording chain shape (`SuppressesRoot_…`,
`SuppressesMid_…`), sibling-branch negatives
(`DoesNotSuppressSibling_…`, `DoesNotSuppressUnrelatedTreeRecording`),
multi-hop chain depth (`SuppressesAllChainAncestors_WhenChainHasMultipleSplitSegments`),
mid-chain active recording (`SuppressesChainPredecessor_WhenActiveIsMidChainNoBpParent`),
true-root bail behaviour, chain cycles
(`HandlesChainCycleGracefully_VisitedSetCapsTheWalk`), the
`TryFindChainPredecessor` helper edge cases (standalone recording,
`ChainIndex == 0`, `ChainBranch` scoping, missing predecessor), and the
`chainHops=` walkTrace counter contract on both success and failure
paths. The end-to-end production-gate test
`EndToEnd_SuppressesRoot_ProductionGate_UserShape` drives
`ShouldSuppressStateVectorProtoVesselForActiveReFly` with the user's
exact production tree shape and asserts both the suppress decision and
the `chainHops=` marker on the `suppressReason`. All 9025 tests pass.

---

## ~~612. Boring-tail trim never fires for stable orbits because terminal-shape match used exact float equality~~

**Source:** user reported the orbital "boring tail" trim mechanism never trims
in practice. Logs from `logs/2026-04-26_1025_3bugs-refly/KSP.log` confirmed
that neither the success log (`TrimBoringTail: trimmed ...`) nor the existing
divergence skip log fired at all for orbital-terminal recordings, even when
the recording clearly ended in a long stable coast.

**Root cause:** `RecordingOptimizer.OrbitShapeMatchesTerminal` compared the
tail's `OrbitSegment` parameters (SMA, eccentricity, inclination, LAN, argP)
against the recording's `TerminalOrbit*` fields with exact `!=` equality.
Numerical drift across the boring tail from rails / pack-unpack / conic
prediction is unavoidable on real recordings, so the byte-for-byte match
rejected on the first parameter. The same failure mode bit the surface-state
path before its tolerances landed (`#356` follow-up). Two compounding
problems made this hard to diagnose: the existing `TailMatchesTerminalOrbit`
skip log only fired when the predicate was actually called, but the player
was reporting "no trim happens" with no log line either way — and
`TrimBoringTail`'s six entry guards (leaf / duration / track-sections /
last-section-not-boring / lastInteresting-NaN-too-few-points / buffer-not-met)
all early-returned silently, so we could not tell which guard rejected.

**Fix:** `OrbitShapeMatchesTerminal` now uses `Math.Abs(delta) > eps` checks
sized to absorb stable-orbit jitter while still catching real maneuvers.
Body name remains exact equality.

- `TailOrbitSmaAbsoluteEpsilonMeters = 10.0`,
  `TailOrbitSmaRelativeEpsilon = 1e-3`. SMA uses
  `max(absolute, relative * |terminal SMA|)` so the check scales from a
  700 km LKO (relEps -> ~700 m) to a 13 Mm Mun encounter (~13 km) without
  losing sensitivity to small absolute changes. A 1 m/s circularization
  burn at LKO shifts SMA by ~tens of metres, well above 10 m.
- `TailOrbitEccentricityEpsilon = 1e-3` absolute. A real burn shifts
  eccentricity by `>= 1e-3`; rails jitter is closer to `1e-5..1e-4`.
- `TailOrbitAngleEpsilonDegrees = 0.01` absolute (inclination, LAN, argP).
  Stable-orbit prograde precession over a 15-minute coast stays under
  `0.01 deg`; a real plane-change burn is hundreds of times larger.

Each `OrbitShapeMatchesTerminal` rejection now logs which field diverged
and the actual delta (Verbose, `[Optimizer]` tag), so future tolerance
tuning has structured data instead of "trim mysteriously didn't fire".

`TrimBoringTail`'s entry guards now each emit a structured one-line skip
reason (Verbose, `[Optimizer]` tag): `not-leaf`, `too-short`,
`no-track-sections`, `last-section-not-boring`, `all-boring-too-few-points`,
`buffer-not-met`, `terminal-mismatch`, `no-points-past-trim-ut`,
`keep-count-too-low`, plus a `rec-null-or-too-few-points` guard at entry.
Each line carries the recording id and the relevant numeric values so the
rejection branch is auditable from `KSP.log` alone.

**Tests:** `RecordingOptimizerTests.cs`:
- `TrimBoringTail_StableOrbitWithRailsJitter_StillTrims` constructs a
  jittered tail (SMA shifted by 3.5 m, ecc by 5e-4, angles by 0.005 deg)
  and asserts the trim now succeeds.
- `TrimBoringTail_StableOrbitWithRealManeuver_DoesNotTrim` uses an
  eccentricity delta of 0.05 (50x epsilon) and asserts the trim still
  rejects.
- `TrimBoringTail_NotLeaf_LogsSkipReason`, `_TooShort_LogsSkipReason`, and
  `_TerminalMismatch_LogsSkipReasonWithDelta` capture log output via
  `ParsekLog.TestSinkForTesting` and assert the corresponding skip-reason
  string fires.

Two pre-existing tests had constructed deltas chosen to break exact
equality (`eccentricity = 0.01001` and `semiMajorAxis = 1200000.5`), well
below the new tolerances; they were updated to use deltas above the
epsilons (`eccentricity = 0.05`, `semiMajorAxis = 1300000.0`) so they
continue to pin the guard-rejects-real-changes contract.

**Wraparound follow-up (P2 review on PR #589):** the first-pass angle
checks used raw `Math.Abs(seg.angle - rec.terminal.angle)` for
inclination, LAN, and argument of periapsis. LAN and argP routinely cross
the 0/360 boundary on a stable orbit; e.g. `terminal LAN = 359.997` vs
`segment LAN = 0.002` is a real wrapped delta of `0.005 deg` (within
epsilon), but raw `Abs(a - b) = 359.995` would have triggered a false
mismatch and the trim would have stayed broken even after the epsilon
fix. Inclination stays in `[0, 180]` and never hits the wrap branch, but
uses the same helper for symmetry / centralized math.

The fix reuses `TrajectoryMath.AngularDeltaDegrees(a, b)` (the existing
`Math.Abs(a - b) % 360, > 180 ? 360 - delta : delta` helper used by
`TrajectoryMath.OrbitsAreEquivalent`); promoted from `private` to
`internal static` so `RecordingOptimizer` can call it. The helper's
contract is now documented (inputs in any range; result in `[0, 180]`)
and pinned by `TrajectoryMathTests.cs` covering simple deltas, zero
delta, the wraparound short-path case, the half-turn boundary,
inclination-range inputs, out-of-range inputs (small negatives,
`> 360`), and the always-non-negative result invariant.

The LAN / argP / inc divergence log lines now report
`<field> wrapped delta <value>deg` so the log no longer lies about what
the comparison saw (a tail at LAN = 0.002 vs terminal LAN = 359.997 logs
a wrapped delta of `0.005`, not `359.995`).

Wraparound regressions in `RecordingOptimizerTests.cs`:

- `TrimBoringTail_LanWrapsAroundZeroBoundary_StillTrims` (terminal
  `LAN = 359.997` vs segment `LAN = 0.002`, wrapped delta `0.005`) — trim
  succeeds.
- `TrimBoringTail_ArgPWrapsAroundZeroBoundary_StillTrims` (terminal
  `argP = 0.001` vs segment `argP = 359.998`, wrapped delta `0.003`) —
  trim succeeds.
- `TrimBoringTail_LanCrossBoundaryRealManeuver_DoesNotTrim` (terminal
  `LAN = 359.5` vs segment `LAN = 0.5`, wrapped delta `1.0`) — trim is
  rejected, AND the divergence log line carries `LAN wrapped delta` (so
  the wrap fix is observable from `KSP.log` alone).

---

## ~~573. Real vessel copy of the upper stage materializes alongside the ghost during Re-Fly~~

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. "when I did the Re-Fly,
when controlling the booster, the upper stage ghost was visible, but also a
real vessel copy of the upper stage (that should not exist)." User flagged this
as `(again - was not fixed)` — i.e. believed already-shipped.

**Prior fix on record:** `CHANGELOG.md` 0.9.0 Bug Fixes — *"Strip-killing the
upper stage during re-fly no longer trips spawn-death respawn, so a duplicate
upper-stage vessel doesn't materialise next to the booster."* That fix
addressed the spawn-death respawn path. The user's recurrence suggests either
a regression, a different code path that bypasses the strip-kill protection,
or an incomplete fix that only covered one trigger.

**Suspected supporting evidence in the cleaned KSP.log:**

- `[LOG 13:04:04.437] [Parsek][INFO][GhostMap] Tracking-station handoff skipped duplicate spawn for #1 "Kerbal X" — real vessel pid=2708531065 already exists`
- `[LOG 13:09:32.514] [Parsek][INFO][KSCSpawn] Spawn not needed for #15 "Kerbal X": source vessel pid=2708531065 already exists - adopting instead of spawning duplicate`

The duplicate-protection branches that *do* exist (Tracking-station handoff,
KSCSpawn) fire with the right "already exists" guard. The duplicate that the
user saw must come from a third path.

**Files to investigate:**

- `Source/Parsek/RewindInvoker.cs` — the post-load `Activate` step, atomic
  provisional commit, and `ReFlySessionMarker` write. If the booster activates
  but the upper-stage strip didn't propagate, the upper-stage real vessel
  survives.
- `Source/Parsek/LoadTimeSweep.cs` — discards zombie `NotCommitted` provisionals
  + session-provisional RPs; check whether a stale provisional upper-stage
  recording is materializing during the sweep.
- `Source/Parsek/VesselSpawner.cs` — `SpawnVesselOrChainTipFromPolicy`,
  `MaterializeFromRecording`, and any path that doesn't consult the same
  "already exists" guard as Tracking-station handoff and KSCSpawn.
- `Source/Parsek/ParsekScenario.cs` — `StripOrphanedSpawnedVessels`. The strip
  log around `[LOG 13:10:15.508]` removes 5 orphaned spawned vessels before the
  rewind FLIGHT load; verify the upper-stage `pid` is included.

**Diagnosis (2026-04-25, revised after PR #541 review):** the first attempt at this fix (commit `c9d257f8`) widened `ParsekPlaybackPolicy.RunSpawnDeathChecks` to short-circuit while `RewindContext.IsRewinding` is true. PR review correctly pointed out this could not address the production sequence: `ParsekScenario.HandleRewindOnLoad` calls `RecordingStore.ResetAllPlaybackState` (which zeros `VesselSpawned` + `SpawnedVesselPersistentId` on every recording) and then `RewindContext.EndRewind()` BEFORE OnLoad returns, while still loading into Space Center. By the time the FLIGHT update path can run `RunSpawnDeathChecks`, the rewind flag is false AND the spawn-tracking fields are already zero, so `ShouldCheckForSpawnDeath` returns false on every iteration and the detector never engages.

The actual production duplicate-spawn fires through a different code path. Concretely, in the 2026-04-25_1314 playtest:

1. `[LOG 13:10:13.161] [Rewind] Rewind replay duplicate scope armed: rec=8e27ba1144a7484b815847c05c49d10e sourcePid=2708531065` — `InitiateRewind` arms `RecordingStore.RewindReplayTargetSourcePid = 2708531065` (the booster's pid).
2. `HandleRewindOnLoad` strips ALL recording-named vessels from flightState, calls `ResetAllPlaybackState`, calls `EndRewind`.
3. Player launches a NEW vehicle `Jumping Flea` (pid 2905720181) at 13:10:47.578 — different stock craft, different pid.
4. Player time-warps for ~1.5 minutes; chain replay advances through the rewound `Kerbal X` tree (treeId `7e46a9f16c9a4dcd90d1c1baaea6e2f5`).
5. `[LOG 13:13:19.600] PlaybackCompleted index=15 vessel=Kerbal X ... needsSpawn=True ... Deferred spawn during warp: #15 "Kerbal X"` — recording `2c276b3c6a9c438eb288dc4cbd55a3ee` (chain leaf, terminal Splashed at UT 194195.7, `vesselPersistentId = 2708531065` because chain segments share the source pid) reports `needsSpawn=True` because it has a `VesselSnapshot`, terminal Splashed, and is the effective leaf for that pid.
6. After the player exits warp the deferred-spawn flush calls `ParsekFlight.SpawnVesselOrChainTipFromPolicy → SpawnAtChainTip → SpawnChainTipWithResolvedState → RespawnVessel(preserveIdentity:true)`. `TryAdoptExistingSourceVesselForSpawn` cannot adopt (no real vessel with pid 2708531065 exists — it was stripped). `RespawnVessel` then materialises a fresh vessel with pid 2708531065 — the user's "real vessel copy of the upper stage". `RunSpawnDeathChecks` never runs on this path because spawn-death only fires AFTER a vessel was successfully tracked, then disappeared; here the vessel is being created from scratch.

**Fix:** introduce scoped `Recording.SpawnSuppressedByRewind` metadata, persisted in tree mutable state as `spawnSuppressedByRewind`, `spawnSuppressedByRewindReason`, and `spawnSuppressedByRewindUT`. `ParsekScenario.HandleRewindOnLoad` calls `MarkRewoundTreeRecordingsAsGhostOnly` after `ResetAllPlaybackState`, but the helper now applies the marker only to the active/source recording (`reason=same-recording`) or a same-source recording whose UT range overlaps the rewind target. Same-tree future recordings are logged as `reason=same-tree-future-recording` and intentionally left spawn-eligible, so #573 no longer turns an entire tree into ghost-only history (#589). `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` still treats `same-recording` as an absolute #573 duplicate-source block, but consumes/clears legacy unscoped markers before continuing through normal spawn-at-end gates. `RecordingStore.ResetRecordingPlaybackFields` clears the marker and metadata so repeated rewinds start clean. Regression coverage in `RewindTimelineTests` and `RewindSpawnSuppressionTests`: same-recording #573 suppression, future same-tree spawn eligibility at `endUT`, legacy marker consumption, repeated-rewind stale-marker clearing, reset lifecycle logging, and log assertions for applied/skipped/cleared decisions.

The `RewindContext.IsRewinding` short-circuit in `RunSpawnDeathChecks` from `c9d257f8` is retained as defense-in-depth (with corrected wording in code + tests calling out that it does NOT cover the production sequence), so a future regression that splits the rewind sequence across update ticks can't trip the spawn-death detector.

**Out of scope (separate follow-up):** the diagnosis pass also flagged
`VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay` (`Source/Parsek/VesselSpawner.cs:99-141`) as deserving a sanity audit — its #226 replay/revert duplicate-spawn exception bypasses the adoption guard whenever the source PID matches the scene-entry active vessel, which expanded scope beyond the booster-respawn intent during this playtest. Not changed in this PR; track as a separate concern.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~574. Extrapolator: 146 sub-surface state rejections classified as Destroyed for the same recordings~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
146 occurrences each of:

- `[Parsek][WARN][Extrapolator] Start rejected: sub-surface state body=Kerbin ut=<F> alt=<F> (threshold=<F>); classifying recording as Destroyed`
- `[Parsek][WARN][Extrapolator] TryFinalizeRecording: patched-conic snapshot failed for '<HEX>' with NullSolver; falling back to live orbit state`
- `[Parsek][INFO][Extrapolator] TryFinalizeRecording: sub-surface destroyed terminal applied for '<HEX>' (terminalUT=<F>) — skipping segment append`

The branch the playtest commit lives on is exactly
`bug/extrapolator-destroyed-on-subsurface`, so the user is already
investigating this. The 146-times repetition for the same recording IDs across
an hour-long session suggests the same recording is being finalized repeatedly
(once per re-evaluation pass) and each pass re-applies the destroyed terminal.

**Files to investigate:**

- `Source/Parsek/BallisticExtrapolator.cs` — `Start`, sub-surface threshold
  check, the `classifying recording as Destroyed` branch.
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` — the scene-exit
  seam. Check whether the "destroyed terminal applied" path is idempotent or
  re-runs on every re-evaluation.
- Cross-reference with bug #571 (weird map trajectories) — sub-surface
  reclassification feeds the orbit data that drives the map vessel preview.

**Diagnosis (2026-04-25):** the normal finalisation-cache producer path was
recording-state harmless after the first Destroyed terminal because the cache
appliers reject already-finalized recordings, but it still rebuilt a failed
finalization cache and re-emitted the NullSolver/sub-surface logs every 5s.
The lower-level `IncompleteBallisticSceneExitFinalizer.TryApply` path was not
strictly idempotent if invoked directly: a second call could re-run the
delegate and overwrite terminal UT/orbit fields. No downstream ERS,
GhostMapPresence, timeline, or Re-Fly merge path needed the repeated
classification once `TerminalState=Destroyed` was already known.

**Fix:** already-Destroyed recordings now short-circuit before the live-orbit
fallback/default ballistic finalizer. The live-orbit origin-adjacent
`NullSolver` fallback remains a trusted first-time Destroyed signal; the first
sub-surface transition logs the recording id, terminalUT, body, altitude, and
threshold once, while later cache refreshes emit a per-recording
`VerboseRateLimited` skip diagnostic and an INFO refresh summary with
`recordingsExamined`, `alreadyClassified`, and `newlyClassified`.

**Status:** CLOSED 2026-04-25. Fixed for v0.8.3.

---

## ~~575. PatchedSnapshot: MissingPatchBody warning floor of 153/session for the same recordings~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
153 occurrences each of:

- `[Parsek][WARN][PatchedSnapshot] SnapshotPatchedConicChain: vessel=Kerbal X patchIndex=<N> body=(missing-reference-body); aborting predicted snapshot capture`
- `[Parsek][WARN][Extrapolator] TryFinalizeRecording: patched-conic snapshot failed for '<HEX>' with MissingPatchBody; falling back to live orbit state`
- `[Parsek][VERBOSE][Extrapolator] TryFinalizeRecording: skipping live-orbit fallback for '<HEX>' because patched-conic failure MissingPatchBody indicates transient early-ascent state, not a destroyed vessel`

The verbose comment immediately following each pair claims the failure is
*"transient early-ascent state, not a destroyed vessel"* — i.e. expected.
But "transient" is supposed to mean "a few frames", not 153 paired warnings
per recording over a single session. Either:

- The early-ascent window is being held longer than necessary;
- The "transient" classifier is firing for non-transient cases too;
- WARN is the wrong level for an expected condition that fires by design.

**Diagnosis (2026-04-25):** `PatchedConicSnapshot.SnapshotPatchedConicChain`
(`Source/Parsek/PatchedConicSnapshot.cs:151-162`) walked the stock patched-conic
chain and called `ResetFailedResult` the moment any patch returned an empty
`BodyName`, throwing away every previously-captured patch in the same chain.
In the captured log every entry was `patchIndex=1`, never `patchIndex=0` —
patch 0 was always valid, only the *next* patch occasionally had a transient
`referenceBody == null` that KSP's stock solver fixes a few frames later.
With the discard-everything policy the recording therefore had no
patched-conic augmentation, and the downstream
`IncompleteBallisticSceneExitFinalizer` (`Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:287-296`)
took the "transient early-ascent state, not a destroyed vessel" skip path on
every refresh because `appendedSegments.Count == 0`.

**Fix:** preserve the partial chain captured before the first null-body patch
when `failedPatchIndex > 0` — set `FailureReason = MissingPatchBody`,
`HasTruncatedTail = true`, log a single rate-of-context VERBOSE truncation
note ("truncated chain after N valid patch(es), keeping partial result"), and
break out of the loop with the captured segments intact. The genuine
"patch 0 has null body" case (`failedPatchIndex == 0`) keeps the original
ResetFailedResult + WARN behaviour. The downstream finalizer's existing
`snapshot.Segments.Count > 0` branch (line 250-258) appends the partial chain
naturally; the transient-ascent skip at line 287-296 only fires when no
patches at all could be captured, which is the correct intent.

This also closes part B of #571 — the user-visible "weird trajectory"
symptom that included a starved predicted tail. Recordings now retain their
predicted-tail orbit data through ascent solver hiccups instead of falling
back to a single Keplerian arc covering the entire warp window.

Regression
`PatchedConicSnapshotTests.Snapshot_MissingPatchBodyAfterValidPrefix_KeepsPartialResult`
pins the new partial-preservation behaviour;
`Snapshot_MissingPatchBody_FailsWithoutKerbinFallback` still pins the original
patch-0-null discard-and-warn case.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~576. PatchedSnapshot: 146 "solver unavailable" warnings clustered on a few vessels~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
`[Parsek][WARN][PatchedSnapshot] SnapshotPatchedConicChain: vessel=<name> solver unavailable`:

- `Kerbal X Debris` — 77
- `Ermore Kerman` — 45
- `Magdo Kerman` — 12
- `Kerbal X Probe` — 11
- `Kerbal X` — 1

Plus paired `[Parsek][WARN][Extrapolator] TryFinalizeRecording: patched-conic
snapshot failed for '<HEX>' with NullSolver; falling back to live orbit state`
(146 occurrences, same as #574's NullSolver count).

This is the same WARN level as the MissingPatchBody case in #575 but with a
different cause — the solver itself isn't reachable. Same concern: WARN floor
for an expected-on-startup or transient-during-soi-transition condition.

**Diagnosis (2026-04-25):** `IPatchedConicSnapshotSource.IsAvailable` is
`vessel != null && vessel.patchedConicSolver != null && vessel.orbit != null`
(`Source/Parsek/PatchedConicSnapshot.cs:295-297`). In stock KSP,
`Vessel.patchedConicSolver` is null **by design** for any vessel whose flight
controller does not own a piloted/probe solver: `VesselType.Debris` (no command
module — 77/77 of the `Kerbal X Debris` hits), EVA kerbals (jetpack motion
system, no solver — 45+12 hits across `Ermore Kerman` / `Magdo Kerman`), and
probe-debris that has lost its active-vessel solver state (11/11 of the
`Kerbal X Probe` hits). Only the lone `Kerbal X` hit at 13:07:48 was the
"genuine transient" case the original WARN tier was designed for. The downstream
`IncompleteBallisticSceneExitFinalizer` (`...:280-286`) explicitly documents
that NullSolver is the destroyed-vessel / no-solver-by-design fingerprint that
drives the live-orbit fallback — the WARN tier was correct as a fallback
signal but wrong for log noise of this shape.

**Fix:** swap both paired warns from `Warn` to `WarnRateLimited` with
distinguishing keys — `solver-unavailable-{vesselName}` for the
`PatchedConicSnapshot` site, and `finalize-snapshot-failed-{recordingId}-{failureReason}`
for the paired `IncompleteBallisticSceneExitFinalizer` site. The `FailureReason
= NullSolver` flow downstream is unchanged: only the level routing through the
30-second-window rate limiter changes. The first hit per key still emits at
WARN level so a fresh regression on a piloted craft mid-flight surfaces
immediately; subsequent hits within 30 s on the same key are absorbed into a
single line per window with a `suppressed=N` suffix.

Regression `PatchedConicSnapshotTests.Snapshot_NullSolver_RateLimitsRepeatsForSameVessel`
pins the per-vessel keying, the cross-vessel independence, and the
30-second-window expiry suffix. Regression
`SceneExitFinalizationIntegrationTests.TryCompleteFinalizationFromPatchedSnapshot_NullSolver_WarnRateLimitedPerRecordingAndReason`
pins the paired Extrapolator rate-limit. The pre-existing
`Snapshot_NullSolver_ReturnsEmptyList` test still pins the WARN level
(`[Parsek][WARN][PatchedSnapshot]`) of the FIRST hit per key.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~577. ReFlySession marker invalidated on the `InvokedUT` field on a fresh load~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` line 8889:

`[Parsek][WARN][ReFlySession] Marker invalid field=InvokedUT; cleared sess=sess_d67eb15f0492418aa71074c83870f867 tree=a35db8a2e78b44e1b3bd2c7ba002bcd6 active=70006fbb97c74e56bf4e5cc79165c0b8 origin=70006fbb97c74e56bf4e5cc79165c0b8 rp=rp_8eebf4aeb2de49dca41bda7ddd1473f4 invokedUT=578.13180328350882 invokedRealTime=2026-04-25T09:40:55.0225531Z`

The marker survived save/load but was wiped on the next OnLoad because
`InvokedUT` failed validation. PR #535 / #536 already addressed `MergeState`
and `ActiveReFlyRecordingId` field validation for the in-place continuation
pattern; `InvokedUT` is a separate field and may not have been covered. After
this clear, all subsequent `Marker saved/loaded: none` entries indicate the
session never re-engaged.

**Note:** the timestamp `invokedRealTime=2026-04-25T09:40:55Z` is from a
previous game session (the playtest started at 13:00:21 UTC+3 = 10:00 UTC),
so this marker was loaded from the on-disk save and immediately wiped on the
fresh start. The cause may be a stale-marker survival problem, a too-strict
`InvokedUT` validator, or both.

**Files to investigate:**

- `Source/Parsek/MarkerValidator.cs` — `Validate`, the `InvokedUT` field check
  (compare to the recently-relaxed `MergeState` / `ActiveReFlyRecordingId`
  rules on PR #535 / #536).
- `Source/Parsek/LoadTimeSweep.cs` — the validation seam that calls
  `MarkerValidator.Validate` and clears on failure.
- `Source/Parsek/ReFlySessionMarker.cs` — when `InvokedUT` is allowed to be
  null/zero and when it must be non-zero.

**Diagnosis (2026-04-25):** `InvokedUT` is Planetarium game UT; `InvokedRealTime`
is the wall-clock UTC timestamp. The marker was rejected because the pre-fix
validator compared `marker.InvokedUT > CurrentUt()`: the cited fresh
SPACECENTER load reported current UT 0 in the scenario summary, so the valid
prior-session `invokedUT=578.13180328350882` looked like a future value even
though the referenced RP was from the same UT neighborhood.

**Fix:** `InvokedUT` validation now rejects only corrupt values (NaN/Infinity,
negative, or above the `1E+15` sanity ceiling); current UT is diagnostic-only
and accept logs call out `legacyFutureUtCheck=triggered` when the old rule
would have wiped the marker. Load-time marker accept/reject logs include the
six durable fields plus the specific validation details, including `currentUT`,
`rpUT`, and deltas for `InvokedUT`. Regressions:
`MarkerValid_PriorSessionInvokedUtAfterFreshLoadUt_PreservedAndLogged`,
`MarkerInvalid_InvokedUtNaN_ClearedWithDiagnostic`,
`MarkerInvalid_InvokedUtNegative_ClearedWithDiagnostic`, and
`MarkerInvalid_InvokedUtExtremeFuture_ClearedWithDiagnostic`.

**Status:** CLOSED 2026-04-25. Fixed for v0.8.3.

---

## 578. CrewReservation: 3 orphan placements where pid AND name tiers both fail ~~done~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
3 occurrences of:

- `[Parsek][WARN][CrewReservation] Orphan placement: no matching part with free seat in active vessel for 'Magdo Kerman' → 'Herfrid Kerman' (snapshot pid=<N> name='mk1-3pod') — stand-in left in roster (attempted pidTier=yes nameTier=yes; cumulative pidHits=<N> nameHitFallbacks=<N>)`
- Same shape for `'Kathrick Kerman' → 'Lomy Kerman'`
- Same shape for `'Ermore Kerman' → 'Shepry Kerman'`

Both lookup tiers (`pidTier=yes` AND `nameTier=yes`) attempted and both
failed → stand-in is left in the roster. Three different replacement pairs
all pointing at the same `mk1-3pod` snapshot suggests the active vessel does
not actually contain that part type, or all of its mk1-3pod seats are
occupied at the time of placement.

**Files to investigate:**

- `Source/Parsek/CrewReservationManager.cs` — orphan-placement fallback path,
  the pid-tier and name-tier matchers, why neither resolved.
- `Source/Parsek/KerbalsModule.cs` — replacement dispatch.
- This subsystem is flagged as elevated-risk in
  `memory/project_post_v0_8_0_risk_areas.md` after the 6-bug PR #263 mega-fix.

**Fix:** Confirmed hypothesis (a) for the captured playtest: the orphan-placement
pass ran while the active vessel lacked the snapshot command-pod part, so the
old WARN collapsed a wrong-vessel target into a generic no-free-seat failure.
`CrewReservationManager` now classifies no-match misses with active part,
free-seat, pid-match, and name-match counters, logs the pass as deferred with
`reason=active-vessel-missing-snapshot-part`, keeps the stand-in roster entry
for a later retry, and still rejects the removed tier-3 "any free seat"
fallback. Regression coverage:
`CrewReservationNameHitFallbackTests.TryResolveActiveVesselPartForSeat_Bug578_WrongActiveVessel_DiagnosesMissingSnapshotPart`,
the miss-reason truth-table/log assertions in the same class, and the runtime
`Bug578_OrphanPlacement_NoMatchingPart_LogsDeferredReason` in-game test.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## 579. ~~LedgerOrchestrator: pending recovery-funds queue overflowed for `Kerbal X Debris`~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
1× threshold, 1× flush:

- `[Parsek][WARN][LedgerOrchestrator] OnVesselRecoveryFunds: pending queue exceeded threshold (count=<N> > <N>) — paired FundsChanged(VesselRecovery) events may be missing. Latest deferred request vessel='Kerbal X Debris' ut=<F>`
- `[Parsek][WARN][LedgerOrchestrator] FlushStalePendingRecoveryFunds (rewind end): evicting <N> unclaimed recovery request(s) that never received a paired FundsChanged(VesselRecovery) event. Entries: [vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>]`

Six unpaired recovery-funds requests for `Kerbal X Debris` evicted on rewind
end. Either KSP is not firing the matching `FundsChanged(VesselRecovery)` event
for debris recoveries, or Parsek's pairing logic is missing a non-debris-only
filter.

**Files to investigate:**

- `Source/Parsek/GameActions/LedgerOrchestrator.cs` — `OnVesselRecoveryFunds`,
  `FlushStalePendingRecoveryFunds`, the `FundsChanged(VesselRecovery)` pairing
  expectation.
- Whether `Kerbal X Debris` (vessel type Debris) is excluded from KSP's
  `onVesselRecoveryProcessing` funds events by stock; if so, debris should
  not be enqueued at all.

**Fix:** Debris recoveries now short-circuit before deferred recovery-funds
queueing when no immediate paired funds event exists: `ParsekScenario` passes the
recovered `ProtoVessel`'s `VesselType` into `LedgerOrchestrator`, and the
orchestrator defensively skips the pending queue for `VesselType.Debris`
callbacks. Stock API docs and decompilation show `onVesselRecoveryProcessing`
still fires before `onVesselRecovered`, so an already-recorded debris payout can
still pair immediately; debris-only recoveries that produce no ledger-worthy
`FundsChanged(VesselRecovery)` should not contribute pending ledger recovery
entries.

**Status:** ~~Open~~ Done.

---

## ~~580. MergeTree: 3 boundary discontinuity warnings (`unrecorded-gap`) between Background and Active sources~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
3 occurrences of:

`[Parsek][WARN][Merger] MergeTree: boundary discontinuity=<F>m at section[<N>] ut=<F> vessel='Kerbal X' prevRef=Absolute nextRef=Absolute prevSrc=Background nextSrc=Active dt=<F>s expectedFromVel=<F>m cause=unrecorded-gap`

Background-to-Active source handoff produced a position discontinuity larger
than the velocity-extrapolated bound during merge. Three occurrences for
`Kerbal X`. Either Background sampling stopped a beat too early, Active
sampling started a beat too late, or the krakensbane / frame-of-reference
correction at handoff is missing a term.

**Files to investigate:**

- `Source/Parsek/RecordingMerger.cs` (or the Merger subsystem in
  `BackgroundRecorder.cs` / `FlightRecorder.cs`) — the `MergeTree` boundary
  check, `expectedFromVel` derivation, the handoff-frame guard.
- Whether `unrecorded-gap` should heal the boundary by interpolating or just
  surface as a WARN.

**Status:** ~~Open~~ Fixed.

**Fix:** `SessionMerger.MergeTree` now heals same-reference-frame Background→Active `unrecorded-gap` seams before rebuilding the merged flat trajectory from section-authoritative payload. The merger inserts one interpolated boundary point shared by the Background tail and Active head, preserves validated flat tail data, recomputes `boundaryDiscontinuityMeters`, and logs `MergeTree: healed unrecorded-gap ... #580` instead of leaving the old WARN shape for the three cited Kerbal X boundaries: section[2] UT 193973.61, section[4] UT 193977.99, and section[1] UT 193985.09.

---

## ~~581. Diagnostics: 2 frame-budget breaches during normal flight playback~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned`:

- 2× `[Parsek][WARN][Diagnostics] Playback frame budget exceeded: <F>ms (<N> ghosts, warp: <N>x)`
- 1× `[Parsek][WARN][Diagnostics] Recording frame exceeded budget: <F>ms for vessel "Kerbal X"`
- 1× `[Parsek][WARN][Diagnostics] Playback budget breakdown (one-shot, first exceeded frame): total=<F>ms mainLoop=<F>ms spawn=<F>ms (built=<N> throttled=<N> max=<F>ms) destroy=<F>ms explosionCleanup=<F>ms deferredCreated=<F>ms (<N> evts) deferredCompleted=<F>ms (<N> evts) observabilityCapture=<F>ms trajectories=<N> ghosts=<N> warp=<N>x`

Each breach is a single frame; the budget breakdown one-shot fires for the
first exceeded frame to capture per-bucket cost. Two breaches in an hour-long
session is low frequency, but worth checking which bucket dominates the
breakdown.

**Diagnosis (2026-04-25):** the captured log has exactly one breakdown line
(`grep "budget breakdown" KSP.log.cleaned` → 1 hit, line 517040): `total=11.6ms
mainLoop=7.51ms spawn=3.44ms (built=1 throttled=0 max=3.44ms) destroy=0.00ms
explosionCleanup=0.00ms deferredCreated=0.28ms (1 evts) deferredCompleted=0.00ms
(0 evts) observabilityCapture=0.39ms trajectories=18 ghosts=0 warp=1x`. This is
a hybrid spike: partly mainLoop (7.51 ms / 65 % of frame) + partly a single
non-trivial spawn (3.44 ms / 30 %). It falls in a diagnostic gap between the
existing #450 (gate: `spawnMaxMicroseconds >= 15 ms`) and #460 (gate:
`mainLoop >= 10 ms` AND `spawn < 1 ms`) sub-breakdown latches: heaviest spawn
under #450's threshold AND mainLoop under #460's floor. `grep "mainLoop
breakdown\|spawn build breakdown" KSP.log.cleaned` confirms 0 hits — the session
captured the generic #414 breakdown but no Phase-B attribution.

The frequency itself (2 playback breaches in an hour-long session, plus 1
recording breach) is well inside expected Unity-frame jitter and does not
constitute a regression. The functional fix is therefore **none**: the budget
itself stays at 8 ms, the existing latches are unchanged.

**Fix:** add a fourth one-shot latch — "Playback hybrid breakdown" — that
fires when total > budget AND `spawnMaxMicroseconds <
BuildBreakdownMinHeaviestSpawnMicroseconds` AND `mainLoopMicroseconds <
MainLoopBreakdownMinMainLoopMicroseconds`. Reuses the existing
`PlaybackBudgetPhases` field set; adds mainLoop / spawn percent-of-frame
fractions so a future hybrid breach reports a Phase-B-actionable per-bucket
itemisation rather than just the generic #414 breakdown that the gap-shaped
spike in the captured log received.

The new latch is independent of #414 / #450 / #460 (matches the precedent set
by #460 itself): a session that already burned the prior three latches on
bigger spikes can still capture the next gap-shaped breach. Test seam
`SetBug460BreakdownLatchFiredForTesting` lets the independence pair-test
between #460 and #581 land without reaching for `ResetForTesting`.

Regression `Bug581HybridBreakdownTests` (8 cases: captured-log-shape format,
one-shot latch, two negative-gate cases asserting the latch declines on
#450-shape and #460-shape spikes, two latch-independence cases for #450 and
#460, total-below-budget defensive case, zero-total degenerate-case
non-throwing).

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0 — observability-only, no
functional change to the budget itself.

---

## ~~582. RELATIVE-frame trajectory points carry anchor-local metres in lat/lon/alt fields — recorder data is correct, contract was under-documented~~

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. *"the ghost icon started
going inside the planet while following the trajectory points (icon only ghost,
not proto-vessel ghost with trajectory line); please check the recordings
trajectory points, something might be wrong there."*

The user pointed at the recorded data because the first `TRACK_SECTION` of
`Recordings/b85acd51ea7f4005bb5d879207749e8c.prec.txt` (ref=Relative,
UT=1658.96-1668.14, anchorPid=95506284) stores values that look obviously
wrong if read as body-fixed lat/lon/alt:

```
POINT { ut=1658.96 lat=-270.69 lon=-149.22 alt=-0.089 ... }
POINT { ut=1668.14 lat=-376.49 lon=-186.21 alt=-0.114 ... }
```

`lat=-270.69` is outside the legitimate `[-90, 90]` range; `alt=-0.089`
is sub-surface; vessel velocity (~2920 m/s world-frame) implies ~26 km of
displacement over the 9.18 s section, but the field deltas are metre-scale.

**Diagnosis (2026-04-25):** the recorder data is correct under the documented
format-v6 RELATIVE-frame contract. The fields are NOT body-fixed lat/lon/alt
in v6 RELATIVE sections — they store the anchor-local Cartesian offset in
metres, computed as `Inverse(anchor.rotation) * (focusWorldPos - anchorWorldPos)`.

**Evidence chain:**

- `FlightRecorder.cs:5502-5543` (`ApplyRelativeOffset`) overrides the
  `BuildTrajectoryPoint`-seeded body-fixed lat/lon/alt with anchor-local
  Cartesian dx/dy/dz when `isRelativeMode == true` and the recording's
  format version reports `UsesRelativeLocalFrameContract`. The dx/dy/dz
  are written into `point.latitude`, `point.longitude`, `point.altitude`
  (lines 5533-5535), with a verbose log
  `RELATIVE sample: contract=anchor-local version=6 dx=… dy=… dz=… anchorPid=… |offset|=…m`.
- `TrajectoryMath.ComputeRelativeLocalOffset` (recorder side) and
  `TrajectoryMath.ApplyRelativeLocalOffset` /
  `TrajectoryMath.ResolveRelativePlaybackPosition` (playback side) use the
  symmetric pair: rotate the world-frame separation into the anchor's local
  frame for storage; rotate it back for replay.
- The captured KSP.log shows the recorder logging consistent metres-scale
  offsets through the relative section, e.g.
  `RELATIVE sample: contract=anchor-local version=6 dx=-111.11 dy=-43.38 dz=-0.27 anchorPid=95506284 |offset|=119.28m`
  (line 11083). The recorded vessel (focus pid=2708531065) and anchor
  (pid=95506284) both came from a controllable split at UT=1627.16
  (line 10866 — `CreateBreakupChildRecording: pid=95506284`), so they
  co-orbit at nearly identical world velocity; the anchor-local offset
  stays small (a few hundred metres) even while world-frame velocity is
  ~2920 m/s. That matches the file values.
- The dual-write at `FlightRecorder.cs:5584` + `:5596` puts the same
  modified point into both the flat `Recording.Points` list and the
  current `TrackSection.frames`. The flat list is therefore frame-blind:
  any caller iterating `Recording.Points` MUST also resolve the
  enclosing `TrackSection.referenceFrame` for that UT before interpreting
  `point.latitude/longitude/altitude` — calling
  `body.GetWorldSurfacePosition(point.lat, point.lon, point.alt)` on a
  RELATIVE-frame flat point places the icon deep underground because
  metre-scale dx/dy/dz are interpreted as degrees-of-latitude plus
  metres-of-altitude.
- Playback resolution is consistent: `ParsekFlight.TryResolvePlaybackWorldPosition`
  (line 13432) checks the section's reference frame at line 13446 and
  dispatches to `TryResolveRelativeWorldPosition` /
  `TryResolveRelativeOffsetWorldPosition` (line 13604-13685), which
  correctly call `ResolveRelativePlaybackPosition` with the anchor's
  current world position and rotation. The flat-point bypass paths in
  `GhostMapPresence.cs` (lines 1979 and 2047) protect themselves with
  `IsInRelativeFrame` guards at lines 1495 and 1733 before calling
  `body.GetWorldSurfacePosition`. The state-vector frame-blindness fix
  the sibling agent in `Parsek-fix-ghostmap-relative-frame` is shipping
  hardens additional flat-point read sites that were missed.

**Outcome (A) — not a recorder bug, contract documentation gap.** The
icon-going-inside-planet symptom belongs to a downstream playback path,
not to the recorded data. The recorded values match the format-v6 contract
exactly; any path that misreads them is a frame-blindness bug at the
read site.

**Fix:** documentation + regression tests pinning the v6 RELATIVE position
contract.

- `.claude/CLAUDE.md` "Rotation / world frame" section now covers the
  POSITION contract alongside the existing rotation note: anchor-local
  Cartesian metres in `latitude`/`longitude`/`altitude`, with explicit
  warning that any flat-`Recording.Points` reader must dispatch on
  `TrackSection.referenceFrame` before calling
  `body.GetWorldSurfacePosition`.
- `Source/Parsek.Tests/RelativeRecordingTests.cs` regressions:
  - `RecorderContract_V6RelativeStoresAnchorLocalOffset_ReplaysToFocusWorldPos`
    pins the round-trip recorder→storage→playback path.
  - `RecorderContract_V6RelativeOffsetIndependentOfAnchorWorldVelocity`
    pins the "anchor world position must not leak into the stored offset"
    property — a moving anchor at orbital velocity must produce the same
    stored value for the same anchor->focus relative displacement.
  - `RecorderContract_V6RelativeFieldsAreNotBodyFixedLatLonAlt` is a
    tripwire that pins values commonly fall outside `[-90, 90]` for
    legitimate RELATIVE-mode separations, so any future code that treats
    a flat-point's `latitude` as degrees will fail loudly.

**Status:** CLOSED 2026-04-25 (data correct; contract now documented; the
playback-side icon-underground symptom is a separate frame-blindness bug
being addressed by the sibling
`fix/ghostmap-state-vector-relative-frame` branch).

---

## ~~583. Map-view state-vector ghost creation still skips when activation first lands inside a Relative-frame section~~

**Source:** PR #547 review follow-up — out-of-scope note attached to the
P1 fix that landed in commit `57aec636` on
`fix/ghostmap-state-vector-relative-frame` (now merged into main as
`8eaebfbb`). Distinct from PR #547's P1 review item ("flight-scene update
path thresholds dz as altitude") which covered the *"ghost already
exists, then enters Relative section"* case.

**Concern:** the create / pending-create resolver path still treats a
Relative-frame current UT as "no map-visible source" and returns
`TrackingStationGhostSource.None`. After the PR #547 P1 follow-up, an
existing map ghost survives a Relative section and stays attached to its
anchor through `UpdateGhostOrbitFromStateVectors`'s Relative branch. But
the symmetric "no ghost exists yet, and the first map-visible source is
inside a Relative section" case still produces missing map presence —
the resolver never picks `StateVector` for a Relative-frame point, so
`CreateGhostVesselFromStateVectors` (which already has a working
Relative branch since PR #547) never gets called for that path.

This is a missing-ghost defect, not a wrong-position one — the #584 fix
(merged via PR #547) already prevents the icon-deep-inside-planet
outcome for ghosts that get created. Likeliest player-visible scenario:
a docking / rendezvous recording whose first map-visible UT is inside
the docking-relative section never gets a map vessel until the
trajectory crosses out of the Relative section (e.g., undock + re-enter
Absolute or OrbitalCheckpoint frame).

**Files to investigate:**

- `Source/Parsek/GhostMapPresence.cs` — `ResolveMapPresenceGhostSource`
  (line 1619). Specifically the `if (!traj.HasOrbitSegments)` branch
  around line 1741 that gates state-vector resolution: a Relative-frame
  current UT short-circuits there because the trajectory may have
  OrbitalCheckpoint segments elsewhere. Decide whether to: (a) extend
  the state-vector branch to fire when the current UT is in a Relative
  section regardless of `HasOrbitSegments`, gated on anchor-resolvability;
  or (b) introduce a new `TrackingStationGhostSource.Relative` source
  kind that flows through to `CreateGhostVesselFromStateVectors`'s
  existing Relative branch.
- `Source/Parsek/ParsekPlaybackPolicy.cs:795-870` — `CheckPendingMapVessels`
  pending-create flow. Whatever the resolver returns must dispatch
  cleanly through `CreateGhostVesselFromSource`.
- `Source/Parsek/GhostMapPresence.cs:2861` —
  `CreateGhostVesselFromStateVectors`. Already dispatches on
  `referenceFrame` (#584). Verify it handles the "anchor unresolvable
  at create-time" case sensibly (defer? skip with VERBOSE? warn?).

**Design questions to answer in the implementing PR:**

- When the anchor vessel for a Relative-frame point is not resolvable
  at create-time (e.g., anchor not yet in `FlightGlobals.Vessels`), what
  should happen? Re-defer until the next tick? Skip silently? Skip with
  WARN? Fall back to terminal-orbit if available?
- If the recording is mid-section and the anchor disappears mid-flight
  (vessel destroyed), should the existing ghost stay parked at last
  known position, or be removed? Today
  `UpdateGhostOrbitFromStateVectors`'s Relative branch presumably
  already handles this — the create-side decision needs to match.

**Coordination note:** PR #547 (which closed #584) is already merged
into `main`. The two `CHANGELOG.md` lines under v0.9.0 Bug Fixes that
prefix `#584` describe the existing-ghost path: *"a ghost that
traverses a Relative-frame docking/rendezvous segment stays attached
to its anchor vessel"* and *"a ghost in a docking/rendezvous Relative
section is no longer wrongly removed and re-deferred"*. Both are
existing-ghost claims, not "all map creation" claims, so neither
overclaims relative to the resolver gap left here. When the
implementing PR for #583 lands, add a sibling `#583` line under v0.9.0
Bug Fixes naming the creation-side fix.

**Status:** ~~Open~~ Fixed.

**Fix:** `GhostMapPresence.ResolveMapPresenceGhostSource` now considers
state-vector resolution when the current UT lies inside a Relative-frame
section even if the trajectory has `OrbitSegments` elsewhere (the gate
widens from `!HasOrbitSegments` to `!HasOrbitSegments || IsInRelativeFrame`).
`TryResolveStateVectorMapPoint` was rewritten as a pure helper
(`TryResolveStateVectorMapPointPure`) that takes a `Func<uint,bool>`
anchor-resolvability lookup, so xUnit can exercise both branches without
KSP's `FlightGlobals`. In the Relative branch the helper bypasses the
`ShouldCreateStateVectorOrbit` altitude/speed threshold (mirroring the
PR #547 P1 update-path gate, since `point.altitude` is the anchor-local
dz offset, not geographic altitude) and gates creation on
`FlightRecorder.FindVesselByPid(anchorVesselId) != null`. When the
anchor isn't yet loaded, the resolver returns `None` with a new
dedicated skip reason `relative-anchor-unresolved`; the existing
`pendingMapVessels` retry loop in `ParsekPlaybackPolicy.CheckPendingMapVessels`
re-resolves on the next tick. Sections without an anchor id keep the
legacy `relative-frame` skip wording so that subset is observably
distinct from "anchor present but not yet resolvable". Five regression
tests (`ResolveMapPresenceGhostSource_RelativeFrame_AnchorResolvable_*`,
`_AnchorUnresolvable_*`, `_NoAnchorId_*`, `_DzBelowAltitudeThreshold_*`,
`_WithOrbitSegmentsElsewhere_*`) pin the new contract.

PR #556 review follow-up (P2): `RefreshTrackingStationGhosts` used to
expire any state-vector ghost whose currentUT was inside a Relative
section (`if (!pt.HasValue || IsInRelativeFrame(rec, currentUT))` →
`tracking-station-state-vector-expired`). After widening the resolver,
that path tore the ghost down every refresh tick while the create path
re-added it next tick — flicker. The refresh path now mirrors the
flight-scene gate in `ParsekPlaybackPolicy.CheckPendingMapVessels`:
remove on `!pt.HasValue` only, then skip the
`ShouldRemoveStateVectorOrbit` threshold for Relative-frame points and
hand off to `UpdateGhostOrbitFromStateVectors` (which already dispatches
on `referenceFrame`). Two more tripwires
(`TrackingStationRefresh_RelativeFrameStateVector_WouldTripRemovalWithoutGate`
and `_AbsoluteFrameStateVector_StillEvaluatesThreshold`) document the
joint precondition the gate suppresses, mirroring the existing
flight-scene `RuntimePolicyTests.RelativeFrameGuard_*` pair.

---

## ~~584. State-vector ghost map paths fed RELATIVE-frame anchor offsets into `body.GetWorldSurfacePosition`~~

**Source:** code-review observation while triaging #571. Latent in
`logs/2026-04-25_1314_marker-validator-fix` — every state-vector creation
attempt that session was rejected (`reason=state-vector-threshold`,
`no-state-vector-point`), so the bug did NOT fire in the captured playtest.
Visible symptom when it would fire: a ghost map vessel transitions through a
RELATIVE `TrackSection` (Phase 3b docking / rendezvous) while above the
state-vector threshold; the ghost icon snaps to the body surface at a
horizontally-meaningless lat/lon ("ghost icon goes inside the planet" —
contributes to #571's symptom family).

**Cause:** `GhostMapPresence.CreateGhostVesselFromStateVectors`
(`Source/Parsek/GhostMapPresence.cs:1979`) and
`GhostMapPresence.UpdateGhostOrbitFromStateVectors`
(`Source/Parsek/GhostMapPresence.cs:2047`) called
`body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude)`
unconditionally. The `TrajectoryPoint.latitude/longitude/altitude` fields
(`Source/Parsek/TrajectoryPoint.cs:13-15`) are reused as anchor-local XYZ
offsets when the originating section uses `ReferenceFrame.Relative`
(`Source/Parsek/TrackSection.cs:34-38`). Feeding offsets into
`GetWorldSurfacePosition` silently produces a meaningless body-surface
position. The flight-scene playback path
(`ParsekFlight.InterpolateAndPositionRelative`, line 13751) and the
diagnostic summary at `GhostPlaybackEngine.cs:3771` already honour the
contract; only these two map-presence paths skipped it.

The tracking-station orbit-update path pre-gates on `IsInRelativeFrame`
(`GhostMapPresence.cs:1733`) and therefore did not fire the bug. The
flight-scene update path in `ParsekPlaybackPolicy.cs:1019` had no such gate,
so the latent defect was actually reachable there.

**Fix:** added a pure-static helper
`GhostMapPresence.ResolveStateVectorWorldPositionPure` that branches on the
section's `referenceFrame`. Absolute keeps the surface lookup; Relative
resolves through `TrajectoryMath.ResolveRelativePlaybackPosition` (the same
contract `InterpolateAndPositionRelative` uses for flight-scene playback)
using the anchor vessel's `GetWorldPos3D()` + `transform.rotation`;
OrbitalCheckpoint and missing-anchor return an unresolved result that the
wrappers convert into a WARN log and a skip. Both call sites now log a branch
tag (`absolute` / `relative` / `orbital-checkpoint` / `no-section`) so post-hoc
audits can confirm the path that fired. `UpdateGhostOrbitFromStateVectors`
gained an `IPlaybackTrajectory traj` parameter; both call sites in
`GhostMapPresence.UpdateTrackingStationGhostLifecycle` and
`ParsekPlaybackPolicy.CheckPendingMapVessels` were updated.

**Tests:** `Source/Parsek.Tests/StateVectorWorldFrameTests.cs` covers all
four branches of the pure helper (absolute, relative v6, relative legacy v5,
orbital-checkpoint, no-section) plus an explicit discriminator test that
identical point data in Absolute vs Relative sections produces divergent
world positions. `Source/Parsek.Tests/GhostMapObservabilityTests.cs`
(32 tests) covers the structured decision-line builder, the lifecycle-summary
helper, the resolution-branch translator, and the per-branch coordinate
contract.

**Observability (post-fix logging contract):** every create / position /
update / destroy decision in `Source/Parsek/GhostMapPresence.cs` emits a
single structured line via `BuildGhostMapDecisionLine` so a future KSP.log
filtered on `[Parsek][INFO][GhostMap]` / `[Parsek][VERBOSE][GhostMap]`
reconstructs the full per-recording lifecycle without cross-file lookups.
Producers fill `GhostMapDecisionFields` (set NaN sentinels via
`NewDecisionFields(action)`) and call the builder. Standard fields always
present: `action`, `rec`, `idx`, `vessel`, `source`, `branch`, `body`,
`scene`. Optional slots appear only when set: `worldPos`, `ghostPid`,
`segmentBody / segmentUT / sma / ecc / inc / mna / epoch`,
`terminalOrbitBody / terminalSma / terminalEcc`, `stateVecAlt /
stateVecSpeed`, `anchorPid / anchorPos / localOffset`, `ut`, `reason`.

Canonical actions (use these names for new lines so existing greps keep
working): `create-segment-intent`, `create-segment-done`,
`create-terminal-orbit-intent`, `create-terminal-orbit-done`,
`create-state-vector-intent`, `create-state-vector-done`,
`create-state-vector-skip`, `create-state-vector-miss`,
`create-dispatch`, `create-chain-intent`, `create-chain-done`,
`update-segment`, `update-state-vector`, `update-state-vector-soi-change`,
`update-state-vector-skip`, `update-state-vector-miss`,
`update-terminal-orbit-fallback`, `update-chain-segment`,
`destroy`, `destroy-chain`, `source-resolve`. The branch tag uses the
capitalised forms (`Absolute` / `Relative` / `OrbitalCheckpoint` /
`no-section` / `(n/a)`) — convert from the resolver via
`MapResolutionBranch`. Per-frame update paths route through
`ParsekLog.VerboseRateLimited` keyed on `recId` (5 s window) so a long warp
pass leaves a readable trace without spam. Both lifecycle drivers
(`UpdateTrackingStationGhostLifecycle`,
`ParsekPlaybackPolicy.CheckPendingMapVessels`) call
`EmitLifecycleSummary(scope, currentUT)` once per tick, which logs
`vesselsTracked / created / destroyed / updated` and resets the per-tick
counters. Future agents extending GhostMap should pick an existing action
name when the decision shape matches, and add a new entry to the canonical
list above when adding a new decision point — duplicating the line shape is
the goal.

**Renumber note:** this entry was originally numbered `#582` while in
flight on `fix/ghostmap-state-vector-relative-frame`, but PR #546 (the
adjacent recorder-side contract documentation) merged first and took the
`#582` slot. Renumbered to `#584` during the rebase merge of `origin/main`
into this branch. CHANGELOG.md was updated to match. The follow-up entry
`#583` (Relative-frame state-vector ghost CREATION still skips for first
activation inside a Relative section) covers the remaining edge case left
open by this fix's `UPDATE`-side scope.

**Status:** Fixed in PR #547 (state-vector RELATIVE-frame contract +
structured GhostMap observability).

---

## ~~585. In-place continuation Re-Fly leaves the active tree in Limbo, the booster recording un-merged, and the merge dialog shows the recording as 0s~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — playtest where the
user re-flew the `Kerbal X Probe` booster (recording
`01384be4319544aebbc7b4a3e0fdd45c`) via the rewind dialog, flew it for ~7
minutes, then went back to the Space Center. User-visible symptoms:

- During the re-fly the recording did not appear in map view.
- The merge confirmation dialog at scene exit "said the recording was 0s".
- After clicking Merge, the booster recording never appeared in map view —
  user flagged this as game-breaking ("the recording of the flight to orbit
  of the Re-Fly booster did not appear in map view, I guess it was not
  merged").

**Diagnosis (2026-04-25):** the in-place continuation path in
`RewindInvoker` and `MarkerValidator` (#577 / #514 follow-ups) handles the
`ReFlySessionMarker` correctly — `AtomicMarkerWrite: in-place continuation
detected — marker → origin 01384be4319544aebbc7b4a3e0fdd45c (no
placeholder created)` at 19:12:24.265 is the expected log line. But the
post-load tree restore never completes and leaves the entire active tree
in Limbo for the rest of the session:

1. Rewind quicksave loads at 19:12:23.118.
   `RecordingStore.RestoreActiveTreeNode` walks all 13 tree recordings,
   the sidecar load fails twice with `Sidecar epoch mismatch for
   5294a8d9c77a4c289bcb5b0a944437e6: .sfs expects epoch 2, .prec has epoch
   6 — sidecar is stale (bug #270), skipping sidecar load (trajectory +
   snapshots)` and the same shape for `01384be4` (.sfs epoch=1, .prec
   epoch=4). The mitigation for #270 protects against corruption but
   leaves the active recording (`5294a8d9` "Kerbal X" capsule) and the
   in-place continuation target (`01384be4` "Kerbal X Probe" booster)
   in memory with empty trajectories and no snapshots.
2. `Stashed pending tree 'Kerbal X' (8 recordings, state=Limbo)` —
   the tree drops from 13 recordings to 8 in the stash and goes to
   `MergeState.Limbo` because of the `2 sidecar hydration failure(s)`
   noted on the same line.
3. `RestoreActiveTreeFromPending: waiting for vessel 'Kerbal X'
   (pid=2708531065) to load (activeRecId=5294a8d9c77a4c289bcb5b0a944437e6)`
   at 19:12:24.534. The expected vessel is the pre-rewind active vessel
   (the capsule that was on the Mun, pid=2708531065). After the strip,
   that vessel is gone — the live active vessel is the booster
   (pid=3474243253, name="Kerbal X Probe"), which is what the player
   wants to fly.
4. `RestoreActiveTreeFromPending: vessel 'Kerbal X' (and no EVA parent
   fallback) not active within 3s — leaving tree in Limbo` at
   19:12:27.525. The 3s coroutine times out without binding the live
   recorder to the in-place continuation recording.
5. The recorder, looking for a vessel to track, fires
   `Post-switch auto-record armed: vessel='Kerbal X Probe' pid=3474243253
   tracked=False reason=vessel switch to outsider while idle` at
   19:12:24.264 — it treats the booster as an "outsider", not as the
   active recording continued in place.

The downstream effect is that `01384be4` is never re-attached to the
recorder. Throughout the 7-minute booster flight no new trajectory
points or snapshot land in `01384be4`, and on scene exit the second
merge dialog at 19:19:39.944 reads:

```
BuildDefaultVesselDecisions: leaf='01384be4319544aebbc7b4a3e0fdd45c'
  vessel='Kerbal X Probe' terminal=null hasSnapshot=False canPersist=False
BuildDefaultVesselDecisions: active-nonleaf='5294a8d9c77a4c289bcb5b0a944437e6'
  vessel='Kerbal X' terminal=null hasSnapshot=False canPersist=False
Tree merge dialog: tree='Kerbal X', recordings=8, spawnable=0
```

`terminal=null hasSnapshot=False` is what the dialog renders as a 0s
duration; `spawnable=0` is why the merged recording produces no real
vessel and no trajectory lines in map view post-merge. The supersede
finalize log even confirms it took the in-place branch:
`TryCommitReFlySupersede: in-place continuation detected (provisional ==
origin == 01384be4319544aebbc7b4a3e0fdd45c); skipping supersede merge ...
and finalizing continuation`.

This is the same family as #21 ("Re-Fly session marker silently wiped
... when the active recording was a previously-promoted Unfinished
Flight") but on a different axis: #21 patched the
`MarkerValidator.MergeState` gate; this one is downstream — the marker
survives, but the tree-restore coroutine still keys on the old active
vessel name and drops the tree to Limbo when the rewind made that
vessel unreachable.

**Files to investigate:**

- `Source/Parsek/ParsekFlight.cs` — `RestoreActiveTreeFromPending`
  vessel-name match logic. For an in-place continuation Re-Fly the
  expected active vessel must be the marker's `ActiveReFlyRecordingId`
  vessel (the booster), not the tree's pre-rewind `activeRecId` vessel
  (the capsule). Probably needs an in-place-continuation carve-out that
  consults the live `ReFlySessionMarker`.
- `Source/Parsek/ParsekScenario.cs` — the tree-stash path that emits
  `stashed active tree 'Kerbal X' (8 recording(s), activeRecId=...) into
  pending-Limbo slot ... with 2 sidecar hydration failure(s)`. Decide
  whether sidecar hydration failure on the ACTIVE recording during a
  rewind quicksave load should still bind the recorder, or whether the
  Limbo stash itself should be expressed as "needs merge dialog before
  next flight".
- `Source/Parsek/RecordingStore.cs` — sidecar epoch mismatch
  short-circuit for `5294a8d9` and `01384be4`. The `.prec` was written
  with `epoch=6` after the original mission, the rewind quicksave's
  `.sfs` has `epoch=2`, mitigation drops the trajectory load. Needed:
  reconcile what the rewind quicksave should restore vs. what the
  on-disk `.prec` already encodes for an in-place continuation
  origin (drop trajectory back to the pre-rewind `epoch=2`? rebuild
  from `.prec` post-rewind UT? something else?).
- `Source/Parsek/FlightRecorder.cs` — `Post-switch auto-record armed:
  ... reason=vessel switch to outsider while idle` is the recorder's
  fallback when the tree is Limbo. For the in-place continuation case,
  the recorder should resume into `01384be4` instead of treating the
  booster as a fresh outsider.
- `Source/Parsek/MergeDialog.cs` —
  `BuildDefaultVesselDecisions` emitting `hasSnapshot=False
  canPersist=False` for the in-place continuation recording. Once the
  underlying restore is fixed the dialog should render real duration +
  spawnable count.

**Resolution (2026-04-26):** Fixed in `fix/585-inplace-continuation-limbo`
by teaching `RestoreActiveTreeFromPending` to consult the live
`ReFlySessionMarker`. When the marker is in-place continuation
(`OriginChildRecordingId == ActiveReFlyRecordingId`) and its recording
id is present in the freshly-popped tree, the coroutine swaps the
wait target to the marker's recording id, vessel name, and pid before
the 3s wait loop. The pure-static decision lives in
`ReFlySessionMarker.ResolveInPlaceContinuationTarget`. A companion
gate in `ParsekFlight.OnVesselSwitchComplete`
(`IsInPlaceContinuationArrivalForMarker`) suppresses the misleading
"vessel switch to outsider while idle" arming for the same in-place
case, so the post-switch watcher cannot race the restore coroutine.
The sidecar epoch mismatch error surface is unchanged: bug #270's
mitigation still drops the trajectory for stale sidecars, the empty
trajectory list is the resumed recording's expected pre-rewind shape,
and the recorder repopulates it on the first frame after binding;
the existing `StashActiveTreeAsPendingLimbo` null-snapshot recapture
path covers `hasSnapshot=False` at scene exit. Design note in
`docs/dev/plans/refly-inplace-continuation-tree-restore.md` (closes
#590) lays out the three contract questions and the
deferred-snapshot-only-rescue follow-up. Tests:
`Bug585InPlaceContinuationRestoreTests` (15 cases covering marker
absence, placeholder pattern, tree-id mismatch, missing-from-tree,
already-pointing-at-marker, pid-match-vs-pid-mismatch, post-fix
merge dialog rendering with `canPersist=True`).

**Review follow-ups (PR #558):** P1 review caught that the async-FLIGHT-load
path schedules the restore coroutine before `RewindInvoker.RunStripActivateMarker`
gets a chance to run `AtomicMarkerWrite` -- both are deferred to
`onFlightReady` and can race. Fixed by gating the marker read on
`RewindInvokeContext.Pending`: the restore coroutine yields until the
context clears (or 300 frames timeout) before reading
`ActiveReFlySessionMarker`, so the marker is guaranteed to be written
before the swap decision. P2 review caught that the post-swap
`tree.BackgroundMap` still contained the newly active recording, so
`EnsureBackgroundRecorderAttached` would seed the background recorder
from a map that listed the live recording as both active and background.
The swap branch now calls `tree.RebuildBackgroundMap()` after mutating
`ActiveRecordingId`, which re-runs `IsBackgroundMapEligible` against
the swapped value and excludes it from the map. Two new tests in
`Bug585InPlaceContinuationRestoreTests`
(`RebuildBackgroundMap_AfterSwapping_ActiveRecordingId_ExcludesSwappedTarget`,
`RebuildBackgroundMap_DestroyedRecording_NotInBackgroundMap`) pin the
post-swap rebuild contract.

**Status:** CLOSED 2026-04-26.

**2026-04-25_2210 follow-up:** the later `refly-bugs` bundle tightened this
conclusion. The sidecar loader still correctly rejects stale `.prec` epochs,
but an in-place Re-Fly active tree must not keep the resulting empty records
when a matching committed tree with good sidecars exists. The empty active-tree
shape is safe only transiently; if it reaches `SaveActiveTreeIfAny`, it can
overwrite the committed launch/upper-stage sidecars with zero payload and break
watch/rewind playback. Item 25 adds the committed-tree repair plus an empty
failed-hydration save skip, superseding the earlier "empty trajectory list is
expected" wording for the active in-place Re-Fly resume path.

---

## 586. Ghost map vessel "Set Target" via icon click logs success but does nothing in KSP ~~done~~

**Source:** same playtest as #585. User: "when controlling the booster, I
tried to click on Set Target on the ghost proto-vessel (the upper stage
heading to the Mun), but the button did not work."

**Suspected supporting evidence in KSP.log:**

- 19:13:25.634 `[Parsek][INFO][GhostMap] Ghost 'Ghost: Kerbal X' set as
  target via icon click`
- 19:13:27.772 same line again — user clicked twice expecting a visible
  effect
- 19:13:30.213 `[Parsek][INFO][GhostMap] Ghost 'Ghost: Kerbal X' focused
  via menu (recIndex=1)` — user fell back to the focus menu

Parsek logs the click as if it succeeded, but the user-observable
behaviour (target marker, distance / velocity readouts on the navball,
encounter-prediction line to the ghost) never materialises.

**Root cause confirmed:**

- KSP did accept Parsek's `FlightGlobals.fetch.SetVesselTarget(...)`
  call, but Parsek had populated the ghost vessel `OrbitDriver` as if
  it were a body driver: `orbitDriver.celestialBody = Kerbin`. Stock
  `OrbitTargeter.DropInvalidTargets()` treats any target driver with a
  `celestialBody` equal to the active vessel's reference body as "the
  current main body" and clears the target. Normal vessel targets keep
  identity in `OrbitDriver.vessel` and leave `OrbitDriver.celestialBody`
  null.
- Fixed by normalizing ghost orbit-driver target identity after
  ProtoVessel load and every ghost orbit update: `OrbitDriver.vessel`
  points at the ghost vessel, `OrbitDriver.celestialBody` stays null,
  and the reference body remains on the `Orbit`.
- The Set Target menu paths now capture target state before and
  immediately after `SetVesselTarget`, then log success only after a
  delayed KSP-validation check confirms `FlightGlobals.fetch.VesselTarget`
  still resolves to the ghost vessel. Rejections log a warning with
  the final reason (`null`, current-main-body, parent-body, wrong
  vessel, wrong object) plus target type/name/body, active vessel
  `targetObject`, ghost `MapObject`, orbit-driver identity, and
  `FlightGlobals.Vessels` registration state.

**Files to investigate:**

- `Source/Parsek/GhostMapPresence.cs` — search for "set as target via
  icon click" to find the click handler. Verify the `SetVesselTarget`
  invocation actually runs and what KSP returns.
- The ghost vessel construction path in `BuildAndLoadGhostProtoVessel`
  / `CreateGhostVesselFromSegment` — confirm the resulting `Vessel`
  is a valid KSP target (correct `vesselType`, has a `MapObject`,
  has an `OrbitDriver`, is registered with `FlightGlobals`).

**Status:** Closed. Tests: `GhostMapTargetingTests` pins the verified
success/failure logging contract, and
`GhostMapVesselTargeting_SyntheticSameBodyGhost_Sticks` is an in-game
runtime canary for production `SetGhostMapNavigationTarget` acceptance
on a synthetic same-body ghost after stock validation frames.

---

## ~~587. KSP shows a phantom "Kerbin Encounter T+" prediction and limits warp to 50× during booster Re-Fly~~

**Source:** same playtest as #585. User: "the kerbalx probe booster
(when I flew the real booster after Re-Fly, in map view) had sections
when it glitched out — orbit disappeared, message in map icon saying
'Kerbin Encounter T+' (wrong), time warp limited to 50x."

50× warp limit + a flagged encounter is the KSP-stock behaviour the
patched-conic solver triggers when it predicts an SOI transition for
the active vessel within the warp horizon. For the booster on a normal
sub-orbital / orbital flight there should be no Kerbin encounter at all.

**Suspected supporting evidence in KSP.log:**

- 19:12:24.275 `[Parsek][WARN][Rewind] Strip left 1 pre-existing
  vessel(s) whose name matches a tree recording: [Kerbal X Debris] —
  not related to the re-fly, will appear as second Kerbal X-shaped
  object in scene` — the strip explicitly leaves a leftover
  `Kerbal X Debris` vessel in the scene whose orbit is independent of
  the re-fly. Stock patched conics walks every nearby vessel's orbit
  to find encounters, and a low-altitude leftover debris on a
  near-identical orbit can trip the encounter solver.
- 19:14:37.328 `[Parsek][WARN][Diagnostics] Playback frame budget
  exceeded: 9.3ms (1 ghosts, warp: 50x) | suppressed=2` — confirms
  warp was being held at 50×.

**Files to investigate:**

- `Source/Parsek/RewindInvoker.cs` /
  `Source/Parsek/Patches/...` — the strip pass that decides what to
  remove pre-rewind. The post-strip warning lists the leftover by
  name match; for in-place continuation, debris from the prior
  flight that pre-dates the rewind UT (UT=160 here) should be
  stripped, not "left alone" because it shares a tree-recording name.
- The encounter prediction itself is KSP-stock and not directly
  controllable — the fix is to remove the cause (the leftover debris).
  Once #585 is fixed, the strip pass needs to handle this corner.
- Cross-check with #573's strip-kill protection logic — that fix made
  the strip not kill the upper-stage ghost during re-fly, but did not
  rule on residual debris.

**Resolution (2026-04-26):** Fixed in `fix/585-inplace-continuation-limbo`
by adding a strip-pass supplement
(`RewindInvoker.StripPreExistingDebrisForInPlaceContinuation`) that
runs after `AtomicMarkerWrite`. For an in-place continuation
re-fly, leftover debris vessels carried in the rewind quicksave's
protoVessels (e.g., the playtest's three pre-existing
`Kerbal X Debris` instances at pids 3749279177 / 2427828411 /
526847698) get killed via `Vessel.Die()` inside a
`SuppressionGuard.Crew()` when (a) the vessel name matches a
Destroyed-terminal recording in the marker's tree and (b) the pid
is NOT in the protected set (selected slot vessel + marker's
ActiveReFlyRecordingId vessel pid). #573's strip-kill protection
is preserved by the protected-pid exclusion, and the post-strip
spawn-death short-circuit in `ParsekPlaybackPolicy.RunSpawnDeathChecks`
already skips during an active re-fly session so the new kills do
not leak into the policy as "spawned vessel died, please re-spawn".
Pure decision in
`RewindInvoker.ResolveInPlaceContinuationDebrisToKill`. Tests:
`Bug587StripPreExistingDebrisTests` (8 cases covering null-marker,
placeholder pattern, tree-id mismatch, no-Destroyed-recordings,
matching-debris-killed, name-matches-Orbiting-recording (kept
alive), protected-pid-not-killed, empty-leftAlone). The
warn-and-continue diagnostic via
`WarnOnLeftAloneNameCollisions` still fires for the
non-in-place-continuation path so the original heads-up message
about prior-career relics is preserved.

**Review follow-up (PR #558):** P2 review caught that the kill loop walked
`FlightGlobals.Vessels` while calling `Vessel.Die()`, which removes the
vessel from the live list and shifts subsequent indices -- consecutive
matching debris would be skipped, exactly the multi-debris case the PR
is supposed to fix. Fixed by snapshotting the targets before any `Die()`
runs via a new pure-static helper
`RewindInvoker.SnapshotKillTargets<T>(IList<T>, HashSet<uint>, Func<T,uint>)`
that returns a stable list of items to kill. The Die() loop then iterates
this snapshot. Six new tests in `Bug587StripPreExistingDebrisTests`
(null-source / null-killset / empty-killset / null-pidGetter / filter-and-skip-zero
/ source-mutated-during-consumption / no-matches) pin the contract;
the source-mutated case explicitly simulates Die-removes-from-live-list
and asserts both targets are still killed.

**Status:** CLOSED 2026-04-26.

---

## 588. Ghost upper stage destroyed at SOI change to Mun and never re-created — `state-vector-from-orbital-checkpoint` skip blocks the fallback

**Source:** same playtest as #585. User: "after rewind, watching the
upper stage get to the Mun — the ghost position in Mun orbit was not
right, it jumped around when warping, did not generate a proto-vessel
in a proper Mun orbit."

**Suspected supporting evidence in KSP.log:**

- 19:29:26.116 `[Parsek][INFO][GhostMap] SOI change for recording #1 —
  new body=Mun` — ghost crossed SOI into Mun.
- 19:29:31.158 `[Parsek][INFO][GhostMap] destroy: rec=37ad80001b3c4baf98056e7c64ad0910
  ... body=Mun ... ut=16510.9 ... reason=gap-between-orbit-segments`
  — the ghost was destroyed because the recording has a gap between
  the Kerbin-frame orbit segments and the next Mun-frame segment.
- 19:29:31.169 `[Parsek][WARN][GhostMap] create-state-vector-skip:
  rec=37ad80001b3c4baf98056e7c64ad0910 ... source=StateVector
  branch=OrbitalCheckpoint body=Mun stateVecAlt=47481 stateVecSpeed=515.4
  ut=21687.8 scene=FLIGHT reason=state-vector-from-orbital-checkpoint`
  — the state-vector fallback would have placed the ghost at altitude
  47.5km / speed 515 m/s above Mun (i.e. a real Mun orbit) but the
  resolver is hard-coded to refuse `StateVector` sources whose
  underlying branch is an `OrbitalCheckpoint` track section.

**Likely root cause:** the gating logic in
`Source/Parsek/GhostMapPresence.cs:2908`
(`FailureReason = "state-vector-from-orbital-checkpoint"`) was added to
prevent the recorder-side densification regression in #571 from
re-introducing wrong ghost positions. But it is over-broad: an
OrbitalCheckpoint section's per-frame state vectors are a perfectly
valid map-presence source when the only alternative is a hole between
two segments around an SOI change. The user-visible result is exactly
what the user reported — the ghost jumps between the last available
segment endpoint and nothing, never settling into a Mun orbit.

**Files to investigate:**

- `Source/Parsek/GhostMapPresence.cs:2861` —
  `CreateGhostVesselFromStateVectors`. The
  `state-vector-from-orbital-checkpoint` reject covers all
  `branch=OrbitalCheckpoint` state-vector paths. For an SOI-change
  hole where the only available data is a checkpoint frame on the
  Mun side, the reject prevents recovery.
- `Source/Parsek/GhostMapPresence.cs` — `ResolveMapPresenceGhostSource`
  segment-gap fallback. After the destroy at 19:29:31.158 the
  resolver should pick a Mun-frame segment if one exists; if it
  doesn't (recording sparse around SOI change), state-vector fallback
  is the only option.
- Cross-reference with #571 part A (recorder-side checkpoint
  densification) — the densified checkpoints around SOI change should
  produce enough Mun-frame segments to avoid hitting this path. If
  this still fires post-#571, the densifier may not be running on
  the SOI-transition window.

**Related:** #570 (real-vessel spawn at end of Mun-mission) and
#589 (real-vessel spawns at end of recordings after rewind) are
sibling symptoms in the same playtest.

**Fix:** `ResolveMapPresenceGhostSource` now treats checkpoint-derived
state vectors as a distinct `StateVectorSoiGap` source only when the
flight map lifecycle explicitly re-queued the recording after
`gap-between-orbit-segments`, no current segment source is available,
the checkpoint body matches the post-gap SOI/body, and both the current
UT and candidate state-vector UT are inside the recording playback
window. Normal `OrbitalCheckpoint` state-vector creates still reject,
and current segments still win. The structured `[GhostMap]` lines now
emit `reason=soi-gap-state-vector-fallback` for accepted recoveries and
specific reject reasons for safer segment, not-SOI-gap, body mismatch,
or outside-window cases. Covered by
`GhostMapSoiGapStateVectorTests` plus the explicit opt-in branch in
`StateVectorWorldFrameTests`.

**Status:** ~~done~~.

---

## ~~589. Real-vessel spawns at end of mission recordings never materialize after a tree-wide rewind — `SpawnSuppressedByRewind` keeps the entire tree ghost-only forever~~

**Source:** same playtest as #585. After the booster Re-Fly merge, the
user issued a second rewind from the recordings table back to
`Kerbal X` at UT 6.8 (i.e., the very beginning of the mission) at
19:21:47. User: "did not spawn the real vessels after Mun landing
(EVA kerbal, flag and lander)."

**Suspected supporting evidence in KSP.log:**

- 19:21:50.229 `[Parsek][INFO][Rewind] OnLoad: SpawnSuppressedByRewind=true
  on 13 recording(s) — chain-leaf spawns blocked for the rewound tree
  (#573)` — the flag is set on every recording in the rewound tree.
- 19:21:50.229 thirteen lines of
  `SpawnSuppressedByRewind: #N "..." id=... tree=a9391bdd... reason=tree-match`
  — covers every recording in the tree, including future-UT recordings
  whose endpoints lie far beyond the rewind UT (#12 Bob Kerman at
  Mun UT 24034, #11 Kerbal X landed, #2 Kerbal X upper stage).
- 19:22:04.007+ recurring
  `Spawn suppressed for #12 "Bob Kerman": spawn suppressed post-rewind
  (ghost-only past, #573) | suppressed=554...591` — the flag is still
  blocking the spawn 30+ minutes later when the player would have
  reached UT 24034 in real time, and would presumably continue
  blocking forever.

**Likely root cause:** the `SpawnSuppressedByRewind` flag was added by
#573 to prevent the Re-Fly's strip from triggering spawn-death respawn
of a chain-leaf vessel that the player is actively re-flying.
`reason=tree-match` is too broad: every recording sharing the rewound
tree gets the flag, including recordings whose endpoints lie ahead of
the rewind UT and whose terminal vessels (EVA kerbal Bob, Mun lander
Kerbal X, planted flag) are exactly what the spawn-at-end Phase 4
design says should materialize when ghost playback crosses the
recording's `endUT`.

The flag needs to be cleared once playback advances past the
recording's `endUT`, or scoped only to recordings whose UT ranges
overlap the rewind UT (i.e., recordings the player is actually
re-flying over), not every recording in the tree.

**Files to investigate:**

- `Source/Parsek/RewindContext.cs` /
  `Source/Parsek/Recording.cs` —
  `SpawnSuppressedByRewind` flag lifecycle. Find the set sites
  (OnLoad, "tree-match" reason) and verify there is any clear path.
- `Source/Parsek/ParsekPlaybackPolicy.cs` — `ShouldSpawnAtRecordingEnd`
  / spawn-at-end gate. Check whether the flag is consulted as an
  absolute block, or as a "block until past rewind UT" gate.
- Cross-reference with #573 — that fix's INTENT was strip-kill
  protection for the actively re-flown vessel, not "make the entire
  tree ghost-only forever". The reason-string `tree-match` should
  probably be split into `same-recording` (the actual #573 case) and
  `same-tree-future-recording` (the case that needs an `endUT >
  rewindUT` carve-out).

**Fix:** split the rewind suppression semantics. `reason=same-recording`
is the #573 strip-kill/source duplicate protection case and remains an
absolute spawn-at-end block. `reason=same-tree-future-recording` is now
logged as an intentional skip: recordings whose `StartUT` and `EndUT`
are ahead of the rewind UT are not marked ghost-only and materialize
normally when playback reaches their endpoint. New persisted metadata
(`spawnSuppressedByRewindReason`, `spawnSuppressedByRewindUT`) scopes
future saves, and the spawn gate consumes/clears legacy unscoped markers
so broken saves from the whole-tree implementation stop blocking future
terminal spawns. Diagnostics now distinguish applied #573 protection,
future same-tree skip, stale marker clear/reset, and spawn allowed despite
same-tree rewind.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~590. Tree restore Limbo + sidecar hydration failure pattern needs a unified diagnosis~~

**Source:** umbrella for the diagnoses that fed #585. Listed separately
because the underlying invariants — "tree restore from rewind quicksave
must keep the in-place continuation recording bound to the live
recorder" and "sidecar epoch mismatch must NOT silently move the tree
into Limbo" — touch
`ParsekScenario`, `RecordingStore`, `RewindInvoker`, `MergeJournal`,
and `MarkerValidator` together.

**Suggested next move:** before patching #585's symptom, read these
files together and write a short design note answering:

- What is the contract between `ReFlySessionMarker.OriginChildRecordingId`
  and `RestoreActiveTreeFromPending`'s expected-active-vessel decision?
- What is the contract between `Recording.Epoch` on the rewind
  quicksave's `.sfs` and the on-disk `.prec` for an in-place
  continuation origin, and which side is authoritative when they
  disagree?
- When sidecar hydration drops trajectory + snapshots for the active
  recording during a rewind, is the right recovery to load `.prec` and
  trim points after rewind UT, or to drop the trajectory entirely and
  re-record from rewind UT, or to refuse the rewind and surface a
  user-facing error?

**Resolution (2026-04-26):** Closed alongside #585. Design note
[`docs/dev/plans/refly-inplace-continuation-tree-restore.md`](plans/refly-inplace-continuation-tree-restore.md)
answers all three questions:

1. The marker's `ActiveReFlyRecordingId` is authoritative for the
   in-place continuation Re-Fly's expected active vessel; the rewind
   quicksave's `ActiveRecordingId` is stale. Carve-out lives in
   `ReFlySessionMarker.ResolveInPlaceContinuationTarget` and is
   consumed by `ParsekFlight.RestoreActiveTreeFromPending` before the
   3s wait loop.
2. For an in-place continuation, neither side is fully authoritative:
   the rewind quicksave's `.sfs` epoch is correct for trajectory POINTS
   (which we re-record from rewind UT anyway) and the on-disk `.prec`
   is correct for the SNAPSHOT (which the player landed/staged with
   at end of original mission). Bug #270's drop-on-mismatch stays the
   default; the fix lives in the marker-aware coroutine, not in the
   sidecar load path. The deferred snapshot-only-rescue (when
   on-disk `.prec` epoch > `.sfs` expected epoch) is filed as a
   future invariant-tightening pass — `StashActiveTreeAsPendingLimbo`
   already re-captures null-snapshot leaves at scene exit, which
   covers the playtest's `hasSnapshot=False` symptom in practice.
3. The Limbo error surface is correct as the default; the empty
   trajectory list IS the resumed recording's expected shape.
   Bug #270's safety net stays intact for non-in-place-continuation
   cases (corrupt save, half-written file, etc).

**Status:** CLOSED 2026-04-26.

---

## ~~591. Log spam: `OnVesselSwitchComplete:entry/post` RecState lines fire ~10000 times in a single session during missed-vessel-switch recovery~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 9969
occurrences of `RecState [#NNNN][OnVesselSwitchComplete:entry|post]`,
clustered in two windows:

- 19:11:28.888 → 19:12:23 (~55s, ~9300 lines): the `Bob Kerman` EVA
  vessel was destroyed (sub-surface state on Mun) and the recorder
  cleared `recorderPid=0`. `ParsekFlight.Update` (line ~6705) detects
  `activeVessel != recorderVessel` every frame and runs the recovery
  path: a single `WarnRateLimited("missed-vessel-switch-{pid}")`
  warning is suppressed (suppressed=589 etc.) so the WARN line itself
  is fine — but the recovery branch unconditionally calls
  `OnVesselSwitchComplete(activeVessel)` after the warn, and the two
  RecState dispatches inside it (`OnVesselSwitchComplete:entry` at
  `ParsekFlight.cs:1881` and `:post` at `ParsekFlight.cs:2030`) are
  not rate-limited.
- 19:11:28 → 19:11:53: at the same time, sibling
  `[WARN][Flight] Update: recovering missed vessel switch ... | suppressed=589`
  rate-limit summaries fire at 5s intervals — the WARN side
  rate-limit works, only the inner RecState logs spam.

**Why it matters:** the two RecState lines together are ~280 KB of
log spam in 55 seconds (avg ~5 KB/s), well above the project's
"log volume must stay readable" target. Every line is identical
shape, no useful per-frame state changes, on a hot path
(`Update()`).

**Files to investigate:**

- `Source/Parsek/ParsekFlight.cs:1881` and `:2030` — the
  `RecState("OnVesselSwitchComplete:entry"/":post")` calls. They
  exist for tracing legitimate vessel-switch boundaries; for the
  recovery loop they fire every frame for the same activePid.
- `Source/Parsek/ParsekFlight.cs:6700-6710` — the missed-vessel-switch
  recovery branch. The WARN is correctly rate-limited via
  `WarnRateLimited("missed-vessel-switch-{activeVesselPid}")`, but
  the subsequent `OnVesselSwitchComplete(activeVessel)` call is
  unconditional. Either gate the call on the same rate-limit key, or
  rate-limit the RecState lines on the same key, or detect
  "recoverer is firing for the same activePid as last frame" and
  short-circuit before logging.

**Status:** ~~Open.~~ Done. Fix: the recovery branch still calls
`OnVesselSwitchComplete(activeVessel)`, preserving the recovery behavior,
but passes a recovery diagnostic context so only the nested
`RecState("OnVesselSwitchComplete:entry"/":post")` lines are
rate-limited. The key includes activePid plus recorder/tracking
fingerprint (recorder pid, live/background flags, tracked/armed state,
chain-to-vessel pending flag, active recording id, and BackgroundMap
count), so repeated identical Update frames coalesce into 5s
`suppressed=N` summaries while changed state or normal non-recovery
vessel-switch boundaries emit fresh diagnostics.

---

## ~~592. Log spam: time-warp rate-change checkpoint logs fire ~3300 times per session from KSP's chatty `onTimeWarpRateChanged` GameEvent~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 1122 ×
`[BgRecorder] CheckpointAllVessels at UT=...`, 1121 ×
`[Checkpoint] Time warp rate changed to N x at UT=... — checkpointing
all background vessels`, and 1121 × `[Checkpoint] Active vessel orbit
segments handled by on-rails events`. ~3364 lines total — the single
biggest log-spam source not already in #591 / #160.

**Diagnosis (2026-04-25):** of the 1121 rate-change events, 1090 were
`1.0x` and only 248 unique UT values were seen — KSP's
`GameEvents.onTimeWarpRateChanged` re-fires aggressively at the same
rate during scene transitions, warp-to-here, and similar transients.
Three `Verbose` log lines were emitted per event with no rate-limit,
plus the underlying `CheckpointAllVessels` walk did real work each
time even though closing+reopening an orbit segment at the same UT is
idempotent.

**Fix:** all three log calls in `ParsekFlight.OnTimeWarpRateChanged`
(`ParsekFlight.cs:5889` / `:5899`) and the summary in
`BackgroundRecorder.CheckpointAllVessels`
(`BackgroundRecorder.cs:2084`) now route through
`ParsekLog.VerboseRateLimited`. The two `Checkpoint` lines are keyed
per warp-rate string (so transitions between distinct rates still log
on the first event), and the BgRecorder summary is keyed by the
`(checkpointed, skippedNotOrbital, skippedNoVessel)` shape so a
genuine count change still surfaces immediately. Regression
`BackgroundRecorderTests.CheckpointAllVessels_RepeatedCallsSameShape_RateLimitedToOneLine`
calls 50x with the same shape and asserts a single emitted summary
line.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0. Log-only fix; the
underlying "KSP fires rate-change at 1x ~4x more often than there are
real rate changes" concern was tracked separately as #597 and closed in
the v0.9.1 follow-up.

---

## ~~593. Log spam: repeatable record milestones (`RecordsSpeed`/`RecordsAltitude`/`RecordsDistance`) re-emit the same `Milestone funds` / `stays effective` / `Milestone rep at UT` line on every recalc walk~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — ~1190 lines:
- 510 × `[Milestones] Repeatable record milestone '<id>' stays
  effective at UT=...` (170 each for Speed / Altitude / Distance).
- 510 × `[Funds] Milestone funds: +N, milestoneId=Records...,
  runningBalance=...` (170 each).
- 393 × `[Reputation] Milestone rep at UT=...: milestoneId=Records...,
  ...`.

**Diagnosis (2026-04-25):** `MilestonesModule.ProcessMilestoneAchievement`
walks every committed action in the ledger on every recalc; for the
three repeatable record-milestone IDs the credit is established on
the first hit and every subsequent walk re-takes the
`isRepeatableRecordMilestone` branch with identical milestoneId,
recordingId, fundsAwarded and repAwarded, producing structurally
identical log lines. With ~57 recalcs in a 30-min session times three
record-milestones, that produces ~170 lines per branch.

**Fix:** `MilestonesModule.ProcessMilestoneAchievement` (the repeatable
"stays effective" branch), `FundsModule.ProcessMilestoneEarning`, and
`ReputationModule.ProcessMilestoneRep` now all route through
`ParsekLog.VerboseRateLimited` keyed by the stable
`GameAction.ActionId` (with a `(milestoneId, recordingId, ut, reward)`
tuple as fallback if `ActionId` is empty). The intended invariant is
"recalculating the SAME action collapses its log line"; two distinct
record-milestone hits sharing the same milestoneId+recordingId but
with different UT or reward have different `ActionId`s and still log
on their first walk. Each emitted line now includes `actionId=...` so
the identity is debuggable. Regressions
`FundsModuleTests.MilestoneEarning_SameActionRecalculated_RateLimitedToOneLine`,
`MilestonesModuleTests.RepeatableRecordMilestone_SameActionRecalculated_RateLimitedToOneLine`,
and `ReputationModuleTests.MilestoneRep_SameActionRecalculated_RateLimitedToOneLine`
re-walk a single action 100 times and assert exactly one emitted line.
Companion regressions
`*_DistinctActionsSamePair_LogSeparately` and
`*_NullRecordingId_StillKeysOnActionId` confirm distinct actions
(including null-recording standalone/KSC paths) survive the gate.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~594. Log spam: `KspStatePatcher.PatchMilestones` bare-Id fallback fires per recalc for the same `(nodeId, qualifiedId)` pair~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 221 ×
`[KspStatePatcher] PatchMilestones: bare-Id fallback match for 'Orbit'
(qualified='...' not found — old recording?)`.

**Diagnosis (2026-04-25):** the bare-Id fallback diagnostic exists to
flag old-format recordings whose milestones stored the bare body-
specific node ID (`Landing`) instead of the qualified path
(`Mun/Landing`). Once such a fallback exists, every recalc walk
re-emits the same line because the recording's milestone-credit
state is steady. Useful as a one-shot "old recording detected" hint;
useless and noisy as a per-recalc line.

**Fix:** the `Verbose` call at `KspStatePatcher.cs:988` now routes
through `VerboseRateLimited` keyed by `(nodeId, qualifiedId)` so each
distinct fallback pair logs at most once per rate-limit window; new
fallback pairs surface immediately on first match.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~595. Log spam: `OrbitalCheckpoint point playback` and `Recorder Sample skipped` rate-limit windows were too tight~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 413 ×
`[Playback] OrbitalCheckpoint point playback: rec=<HEX> currentUT=...`
and 197 × `[Recorder] Sample skipped at ut=...; waiting for motion/
attitude trigger`. Both lines were already routed through
`VerboseRateLimited`, but with custom 1.0s and 2.0s windows
respectively — tight enough that long-playing OrbitalCheckpoint
sections still emitted ~14 lines/min per `(recId, sectionIdx)` and
stationary recordings ~7 lines/min.

**Diagnosis (2026-04-25):** both lines convey steady-state telemetry
(per-section ghost playback / "still stationary, no sample taken"),
not state transitions, so the rate-limit window can safely widen to
the project default 5s without losing diagnostic value — the per-key
identity (`recId+sectionIdx` for OrbitalCheckpoint, single shared key
for Sample skipped) means new sections / new recorders still log on
their first frame.

**Fix:** both call sites (`ParsekFlight.cs:13870` and
`FlightRecorder.cs:5458`) now use the default 5s rate-limit window
inherited from `ParsekLog.DefaultRateLimitSeconds`.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~Log spam: `[GhostMap] map-presence-{pending,initial}-create` and `[Spawner] Spawn suppressed: no vessel snapshot` re-emit on every frame across stable per-frame decisions~~

**Source:** `logs/2026-04-25_2147/` (KSP.log + Player.log mirror the
same Parsek output — counts below are per-file; sum across both is
the same number doubled) — 228k Parsek lines in a 27-minute playtest,
~13,719 of which were `[GhostMap] map-presence-pending-create` (9,950)
/ `map-presence-initial-create` (3,769) verbose lines, plus 14,487
`[Spawner] Spawn suppressed for #X "Y": <reason>` lines spanning 286
distinct `(idx, vessel)` tuples. Reproduce with:

```bash
grep -c "Spawn suppressed for #" logs/2026-04-25_2147/KSP.log
grep -c "Spawn suppressed for #" logs/2026-04-25_2147/Player.log
grep -h "Spawn suppressed for #" logs/2026-04-25_2147/KSP.log \
    logs/2026-04-25_2147/Player.log \
    | grep -oE '#[0-9]+ "[^"]+"' | sort -u | wc -l
```

**Diagnosis (2026-04-25):** `GhostMapPresence.ResolveMapPresenceGhostSource`
fires from two per-frame call sites (`ParsekPlaybackPolicy.cs:867` for
the pending queue and `:758` for initial create) and emits two verbose
diagnostic lines per call. The previous wiring used
`VerboseRateLimited` with the default 5s window keyed on
`(operation, recId, source, reason)`, so a recording stuck in
`source=None reason=state-vector-threshold` for the entire session
still emitted ~one line per recording per 5 seconds (50 recordings ×
324 5-second windows ≈ 16,200 emissions). `ParsekFlight.ComputePlaybackFlags`
emitted `Spawn suppressed for #X "Y": <reason>` keyed by `idx` only
(no reason in the key), so a recording whose suppression reason
flipped between e.g. `"no vessel snapshot"` and `"chain-suppressed"`
mid-session would only surface one of them per 5s window.

**Fix:** added `ParsekLog.VerboseOnChange(subsystem, identity,
stateKey, message)` — emits only when `stateKey` flips for the given
`identity`, surfacing the suppressed count as `| suppressed=N` on the
next change. Per-frame stable streaks coalesce into a single emission
on entry plus one on exit, regardless of duration. Three call sites
now route through the helper:

- `GhostMapPresence.ResolveMapPresenceGhostSource.ReturnDecision`
  (`GhostMapPresence.cs:2357`) — identity `map-ghost-source-<op>-<recId>`,
  state key `(source, reason)`.
- `GhostMapPresence.EmitSourceResolveLine` (`GhostMapPresence.cs:2301`)
  — identity `gm-source-resolve-<op>-<recId>`, state key
  `(source, reason)`.
- `ParsekFlight.ComputePlaybackFlags` (`ParsekFlight.cs:12070`) —
  identity `spawn-suppressed|<rec.RecordingId>` (with `idx-<i>`
  fallback when RecordingId is null/empty), state key is the
  suppression reason itself. The 2026-04-25_2147 logs show index 294
  reused across recordings as discards reshuffle the committed list,
  so keying on `RecordingId` rather than the bare index keeps each
  recording's first-emission line and suppressed counter independent.
  The bare index still appears in the message body so audits can
  resolve recordings post-hoc.

**Tests:** `ParsekLogTests.VerboseOnChange_*` (8 cases covering first
emission, stable suppression, key flip with suppressed counter,
independent identities, suppressed-count reset, verbose disabled, null
state key, empty identity fallback) plus
`LogSpamRateLimitTests.GhostMapSourceResolve_*` and
`LogSpamRateLimitTests.Spawner_*` for the call-site integration. The
index-reuse-across-recordings case is pinned by
`LogSpamRateLimitTests.SpawnSuppression_IndexReuseAcrossRecordings_KeepsIndependentState`
(plus `_StableReasonStillCoalescesPerRecording` and
`_NullRecordingId_FallsBackToIndexInIdentity`).

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0. Reproduced from the
`logs/2026-04-25_2147` playtest folder.

---

## ~~596. Log spam: `KspStatePatcher.PatchFacilities` emits an INFO summary on every recalc even when there is nothing to patch~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 42 ×
`[KspStatePatcher] PatchFacilities: levels patched=0, skipped=0,
notFound=0, total=0`. Earlier playtests (the same KSP.log around lines
26791-27996) showed several thousand of these once the recalculation
engine churned the same empty `FacilitiesModule` repeatedly — the
no-op summary fires unconditionally at INFO on every PatchFacilities
call.

**Diagnosis (2026-04-25):** the summary's purpose is to make
"facility patching changed game state or hit a missing facility"
visible at INFO. `skippedCount` increments on the no-op pass (a
facility already at its target level), so a steady-state non-empty
`FacilitiesModule` would still re-emit the INFO summary on every
recalc if `skipped` counted toward the gate.

**Fix:** `KspStatePatcher.PatchFacilities` now gates the INFO summary
on `patchedCount + notFoundCount > 0`. The skipped-only steady-state
case (`patched=0, notFound=0, skipped>0`) routes through
`VerboseRateLimited` with key `patch-facilities-skipped-only`. The
empty-totals case (no tracked facilities at all) keeps its existing
`patch-facilities-empty` rate-limited Verbose path. Regressions
`KspStatePatcherTests.PatchFacilities_NotFound_LogsInfoSummary` and
`PatchFacilities_Empty_DoesNotLogInfo_UsesRateLimitedVerbose` pin
the INFO branch (notFound>0) and the empty-Verbose branch. The
skipped-only branch needs real `UpgradeableFacility` refs in
`ScenarioUpgradeableFacilities.protoUpgradeables` and is verified
in-game during the next playtest pass instead of an xUnit canary.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~605. Log spam: `HasOrbitData(IPlaybackTrajectory)` fires ~1678 times per session from per-frame map-view callers~~

**Source:** `logs/2026-04-25_2334_refly-followup-test/KSP.log:13777` and
~1677 sibling lines. Shape:

```
[Parsek][VERBOSE][GhostMap] HasOrbitData(IPlaybackTrajectory): body=Kerbin sma=742959.380465312 result=True
```

`grep -c 'HasOrbitData(IPlaybackTrajectory)'` ⇒ 1678 in a single
27-minute playtest, ~11/sec, multiple per frame.

**Cause:** `GhostMapPresence.HasOrbitData(IPlaybackTrajectory)` is
called from `ParsekPlaybackPolicy.MaybeCreateInitialGhostMap` and the
shared map-presence resolver every map-view frame. The verbose log
inside the helper was an unconditional `ParsekLog.Verbose` so each
caller flooded the log with the same `(body, sma)` decision while the
recording sat in steady state. The pre-existing `Recording`-overload
emitted the same line at the same cadence whenever a map-view caller
reached it, contributing a parallel stream.

**Resolution (2026-04-26):** Routed both `HasOrbitData` overloads
through `ParsekLog.VerboseOnChange`, identity scoped per
recording (or vessel name when `RecordingId` is empty), state key
`(body, smaBucket1km, result)`. A stable per-frame stream now emits
exactly once on entry and surfaces `| suppressed=N` on the next state
flip. Regression coverage:
`GhostMapPresenceTests.HasOrbitData_TrajectoryStableCalls_LogOnceWithSuppressedCounter`
(100 stable calls ⇒ 1 emission, 99 suppressed; state change emits with
`suppressed=99`) and
`GhostMapPresenceTests.HasOrbitData_TrajectoryDistinctRecordings_LogIndependently`
(distinct recording ids each get their own first emission).

**Status:** CLOSED 2026-04-26.

---

## ~~606. Log spam: `FinalizerCache refresh summary` Info-tier emits 156× per session for periodic no-op passes~~

**Source:** `logs/2026-04-25_2334_refly-followup-test/KSP.log:9528+`
and 155 sibling lines. Steady-state shape:

```
[Parsek][INFO][Extrapolator] FinalizerCache refresh summary: owner=ActiveRecorder reason=periodic recordingsExamined=1 alreadyClassified=0 newlyClassified=0
```

Counts:

```bash
grep -c "FinalizerCache refresh summary" KSP.log    # 156
```

Most are `alreadyClassified=0 newlyClassified=0` periodic cadence
re-runs that did no real classification work.

**Cause:** `RecordingFinalizationCacheProducer.LogRefreshSummary`
emitted every refresh at INFO regardless of whether the pass produced
a fresh classification. The diagnostic was originally meant to
surface "a recording crossed from unclassified to classified", but the
gate `recordingsExamined<=0 && alreadyClassified<=0 && newlyClassified<=0`
let `examined=1, classified=0, fresh=0` through every refresh.

**Resolution (2026-04-26):** Demoted no-delta passes
(`newlyClassified == 0`) to Verbose; passes that produced a fresh
classification (`newlyClassified > 0`) keep INFO. Added a backstop:
every 64th consecutive no-delta pass still emits at INFO with
`backstop=every64thNoDeltaPass suppressedNoDeltaPasses=N` so a long
session retains discoverable INFO markers in case the diagnostic is
needed post-hoc. Tests:
`RecordingFinalizationCacheProducerTests.LogRefreshSummary_NoDeltaPass_RoutesThroughVerbose`,
`LogRefreshSummary_FreshClassification_StaysInfo`,
`LogRefreshSummary_NoDeltaBackstop_PromotesEvery64thPassToInfo`.
Existing `BackgroundRecorderTests.RefreshOnRailsFinalizationCache_AlreadyDestroyedSkipRefreshesOnCadence`
+ `RecordingFinalizationCacheProducerTests.TryBuildAlreadyClassifiedDestroyedSkip_LiveVesselSecondCall_ReusesExistingFailureCacheAndUpdatesObservation`
flipped to assert Verbose for `alreadyClassified=1, newlyClassified=0`.

**Status:** CLOSED 2026-04-26.

---

## ~~624. Log spam: ChainWalker emits ~30 verbose lines per frame from `ParsekTrackingStation.RefreshGhostActionCache` while a ghost is selected~~

**Source:** `logs/2026-04-26_2301_tracking-station-ui-and-esc-menu/KSP.log`.
KSP.log was 26 MB / 160,108 lines, of which 134,509 (84%) were
`[ChainWalker]` verbose lines. Steady-state shape, repeated every
Update tick over ~3 minutes:

```
[Parsek][VERBOSE][ChainWalker] HasGhostingTriggerEvents: rec=… found=True (scanned 8 part events, 0 segment events)
[Parsek][VERBOSE][ChainWalker] Vessel PID=… claimed by tree=… via BACKGROUND_EVENT at UT=21.8
[Parsek][VERBOSE][ChainWalker] WalkToLeaf: reached leaf=… after 0 steps from start=…
[Parsek][VERBOSE][ChainWalker] ResolveTermination: vessel=… tip=… terminalState=Destroyed — marked terminated
[Parsek][VERBOSE][ChainWalker] Chain built: vessel=… links=1 tip=… spawnUT=… terminated=True
[Parsek][VERBOSE][ChainWalker] Found claims for 6 vessel(s) across 1 committed trees
```

Counts (single playtest):

```bash
grep -c "\[ChainWalker\]" KSP.log                 # 134509
grep -c "WalkToLeaf: reached leaf="  KSP.log      # 26034 (×6 vessels)
grep -c "Found claims for"           KSP.log      # 4339 (calls/sec ≈ 22 = per-frame)
```

**Cause:** `ParsekTrackingStation.Update` calls
`RefreshGhostActionCache()` every frame while a ghost is selected
(`ParsekTrackingStation.cs:259`). The cache itself is `Time.frameCount`
gated to one rebuild per frame, but the rebuild dispatches to
`GhostChainWalker.ComputeAllGhostChains`, which used raw
`ParsekLog.Verbose` for six per-vessel/per-recording diagnostic lines
(`HasGhostingTriggerEvents` in `GhostingTriggerClassifier.cs:175`,
`Vessel PID claimed` × 2 in `GhostChainWalker.cs:256`/`:317`,
`WalkToLeaf reached leaf` at `:620`, `ResolveTermination` at `:660`,
`Chain built` at `:111`, plus the `Found claims` summary at `:78`).
With ~6 vessels in claims, a single per-frame call fanned out to ~30
verbose lines, dominating every tracking-station session that had a
selected ghost.

**Resolution (2026-04-26):** Routed every diagnostic in the chain-walk
hot path through `ParsekLog.VerboseOnChange` with state keys that
capture the decision tuple — including the no-claim and skipped-tree
summaries that fire once per call regardless of whether any claims
were found:

- `HasGhostingTriggerEvents` — identity `trigger|<recId>`,
  state `(found, partCount, segCount)`.
- `Vessel PID claimed via {Dock|Board|Undock|EVA|JointBreak}` —
  identity `claim|<pid>|<treeId>|<bp.Id>`, state `(interactionType, bp.UT)`.
  The bp.Id is in the identity (not the state key) so multiple claims
  for the same `(pid, treeId)` pair don't share a single VerboseOnChange
  slot and ping-pong their differing state keys on every frame.
- `Vessel PID claimed via BACKGROUND_EVENT` — identity
  `claim-bg|<pid>|<treeId>|<rec.RecordingId>`, state `rec.StartUT`.
  Same disambiguation reason — multiple background recordings of the
  same `(pid, treeId)` would otherwise oscillate.
- `WalkToLeaf: step N: rec=… → child=…` — identity `walk-step|<startId>|<step>`,
  state `(currentId, childId, bpId)`. Per-step diagnostic gets one
  identity slot per step so a stable multi-step walk emits each step
  exactly once, then stays silent.
- `WalkToLeaf reached leaf` — identity `walk|<startId>`,
  state `(leafId, steps)`.
- `ResolveTermination marked terminated` — identity `terminate|<pid>`,
  state `(tipId, terminalState)`.
- `Chain built` — identity `chain|<pid>`,
  state `(links, tip, spawnUT, terminated)`.
- `Cross-tree link: vessel=… → merged with chain for vessel=…` —
  identity `cross-tree-link|<originPid>|<tipVesselPid>`,
  state `chain.TipRecordingId`.
- `MergeCrossTreeLinks: absorbed N chain(s)` — identity
  `cross-tree-merge-summary`, state `N`.
- Mutually-exclusive summary lines (`No committed trees`,
  `No claims found in N committed trees`,
  `Found claims for N vessel(s) across N committed trees`) all share
  identity `claims-summary` with state-key prefixes `no-trees` /
  `no-claims|<N>` / `found|<Nvessels>|<Ntrees>` so flipping between
  the variants is a real state change but stable repeats inside a
  variant coalesce.
- `Skipped N fully-terminated tree(s)` — identity `skipped-terminated`,
  state `N`.
- `Skipped N claim(s) from session-suppressed recording(s)` — identity
  `skipped-session-suppressed`, state `N`.

Stable per-frame rebuilds (the dominant case in the tracking station,
including the no-claim case for sandbox saves) now emit each diagnostic
exactly once and surface `| suppressed=N` on the next genuine state
flip.

Cold paths (`Warn` for missing parents, `Verbose` for null-tree /
unfound-child fallbacks) remain raw `Verbose` since they are decision
flips rather than per-frame stable repeats. Regression coverage:

- `GhostChainWalkerTests.RepeatedCalls_StableInput_DoNotRespamDiagnostics`
  — multi-step chain (R1 → R1-mid → R1-leaf) so `WalkToLeaf: step …`
  is exercised; counts every flavor of ChainWalker line and asserts 0
  new lines after warm-up.
- `GhostChainWalkerTests.RepeatedCalls_NoClaims_DoNotRespamSummary` —
  covers the per-frame no-claim path that a fresh sandbox with a
  selected ghost would hit, plus the empty-trees variant.
- `GhostChainWalkerTests.RepeatedCalls_MultipleClaimsSamePidSameTree_DoNotRespam`
  — two Dock BPs claiming the same PID in the same tree; pins the
  identity-disambiguation against the bp.Id-in-identity choice.
- `GhostChainWalkerTests.RepeatedCalls_CrossTreeLinkedChain_DoNotRespamMergeLines`
  — two trees both claim PID 100 (the `CrossTree_TwoLinks_ChainsExtend`
  shape); pins the cross-tree merge path against per-frame re-emission
  of the merge line and the absorbed-N summary.
- `ChainSaveLoadTests.DeterministicReDerivation_StableInput_ReturnsIdenticalChains`
  — flipped from log-count assertion to chain-result equivalence so
  the determinism guarantee survives the coalescing change.

**Status:** CLOSED 2026-04-26.

---

## ~~625. Log spam: `Blocked GoOffRails for ghost vessel` Harmony prefix fires ~117 Hz per ghost ProtoVessel in physics range~~

**Source:** `logs/2026-04-26_2357_newest/KSP.log`. 2,941 lines for a
single ghost PID `940887686` over 25 seconds (23:51:57-23:52:22), at
~117 Hz (FixedUpdate × 2.3). Shape:

```
[Parsek][VERBOSE][GhostMap] Blocked GoOffRails for ghost vessel 'Ghost: Kerbal X' pid=940887686
```

Per-second distribution: `109, 118, 116, 118, 118, 117, 117, 115, …`

**Cause:** `GhostVesselLoadPatch.Prefix` (Harmony prefix on
`Vessel.GoOffRails`) returns `false` to keep ghost ProtoVessels on
rails. KSP retries the off-rails transition every FixedUpdate while
the ghost is inside physics range, so the prefix fires per physics
tick and emits a raw `ParsekLog.Verbose` every time. With one ghost
in range it floods at FixedUpdate × 2.3.

**Resolution (2026-04-27):** Extracted the log into
`GhostVesselLoadPatch.LogBlockedOffRails(uint pid, string vesselName)`
and routed it through `ParsekLog.VerboseOnChange` with identity
`block-offrails|<pid>` and stateKey `<vesselName>`. Each ghost gets its
own VerboseOnChange slot, so two distinct ghosts each emit on first
block while per-PID repeats coalesce silently. The next genuine state
flip (a vessel rename, vanishingly rare for ghosts) surfaces
`| suppressed=N`.

Regression coverage:
- `GhostVesselLoadPatchTests.LogBlockedOffRails_RepeatedCallsSamePid_EmitOnceForStableName`
  — 100 repeat calls after first block emit zero new lines.
- `GhostVesselLoadPatchTests.LogBlockedOffRails_DistinctPids_EachEmitsOnceAndStaysSilent`
  — two PIDs each emit on first block, then 50 alternating calls
  (which would ping-pong a shared identity slot) emit zero new lines.

**Status:** CLOSED 2026-04-27.

---

## ~~607. Misleading `Strip left N pre-existing vessel(s)` WARN reports stale, deduped count after post-supplement kill~~

**Source:** `logs/2026-04-25_2334_refly-followup-test/KSP.log:12906-12907`:

```
[WARN][Rewind] Strip post-supplement: killed 3 pre-existing debris vessel(s) for in-place continuation re-fly: [Kerbal X Debris, Kerbal X Debris, Kerbal X Debris] (...)
[WARN][Rewind] Strip left 1 pre-existing vessel(s) whose name matches a tree recording: [Kerbal X Debris] — not related to the re-fly...
```

Same playtest: 3 actual `Kerbal X Debris` instances were in
`stripResult.LeftAloneNames`; `StripPreExistingDebrisForInPlaceContinuation`
killed all 3; the WARN still fired and reported `1` (the deduped
unique-name count) — wrong on both counts (1 vs the original 3,
0 vs the post-kill survivors).

**Cause:** `RewindInvoker.WarnOnLeftAloneNameCollisions` formatted
`collisions.Count` as the vessel-instance count. `collisions` came
from `PostLoadStripper.FindTreeNameCollisions`, which dedupes by
name. And the WARN ran AFTER the post-supplement kill but read from
the original pre-kill list, so it could fire even when no colliding
vessel survived.

**Resolution (2026-04-26):** Re-survey
`FlightGlobals.Vessels` at warn time; intersect with the colliding
name set; report `vessels=N collidingNames=M` separately so the two
counts cannot be conflated; suppress the WARN entirely (Verbose-only
diagnostic) when the colliding set has been fully drained. Helpers
`CountLiveCollidingVessels` (pure) and `EmitStripLeftAloneWarn`
(pure) extracted so unit tests can exercise the format without
`FlightGlobals` wiring. Tests in
`Bug587StripPreExistingDebrisTests`:
`CountLiveCollidingVessels_AllInstancesKilled_ReturnsZero`,
`CountLiveCollidingVessels_MultipleInstancesSameName_CountsInstancesNotNames`,
`EmitStripLeftAloneWarn_AllKilled_LogsVerboseAndNoWarn`,
`EmitStripLeftAloneWarn_LiveVesselsRemain_LogsSeparateInstanceAndNameCounts`,
`EmitStripLeftAloneWarn_PartialKill_ReportsSurvivors`,
`EmitStripLeftAloneWarn_NoCollidingNames_NoLog`.

**P2 review follow-up (PR #577):** the original re-survey walked
every live vessel name and counted every match — including the
actively re-flown vessel (`SelectedPid`), GhostMap ProtoVessels,
freshly stripped pids whose `Die()` event hadn't drained from the
live list, and any other legitimate same-name vessel from a parallel
flight. Any of those producing a name match would re-emit the
misleading WARN this fix was supposed to suppress.

**Resolution (2026-04-25):** Renamed
`PostLoadStripResult.LeftAloneNames` (`List<string>`) →
`LeftAlonePidNames` (`List<(uint pid, string name)>`) so the strip
phase persists the pid alongside the name. The new pure helper
`RewindInvoker.SurveyLiveLeftAloneCollisions` walks ONLY the
`LeftAlonePidNames` set, defensively drops entries matching
`SelectedPid` / `StrippedPids` / `GhostMapPresence.IsGhostMapVessel`,
verifies pid liveness against a `FlightGlobals.Vessels` snapshot,
and only then counts collisions. The `liveVesselCount` is now
bounded by the pre-strip leftAlone set, so a same-name active vessel
or a ghost ProtoVessel can no longer be miscounted as a "leftover".
The structured WARN payload gained `leftAlonePidsAlive=N
excludedSelected=N excludedStripped=N excludedGhostMap=N` so the
exclusion path is post-mortem visible.

New tests in `Bug587StripPreExistingDebrisTests` (replacing the
`CountLiveCollidingVessels_*` cases):
`SurveyLiveLeftAloneCollisions_AllLeftAlonePidsKilled_ReturnsZero`,
`SurveyLiveLeftAloneCollisions_PartialKill_ReportsSurvivorInstanceAndNameCount`,
`SurveyLiveLeftAloneCollisions_MultipleInstancesSameName_CountsInstancesNotNames`,
`SurveyLiveLeftAloneCollisions_SelectedPidExcluded_DoesNotCountAsLeftover`,
`SurveyLiveLeftAloneCollisions_StrippedPidExcluded_DoesNotCountAsLeftover`,
`SurveyLiveLeftAloneCollisions_GhostMapPidExcluded_DoesNotCountAsLeftover`,
`SurveyLiveLeftAloneCollisions_AllExcludedAndAllKilled_ReturnsZero`,
`SurveyLiveLeftAloneCollisions_NullInputs_AreDefensive`,
plus `EmitStripLeftAloneWarn_AllExcluded_LogsVerboseWithExclusionCounters`
to pin the structured-log shape.
`PostLoadStripperTests.Strip_CapturesLeftAlonePidNamesForCollisionDetection`
flipped to assert `(pid, name)` tuples are captured.

**Status:** CLOSED 2026-04-25 (PR #577 P2 follow-up).

---

## 608. Crew-reservation orphan placement deferred for original-crew kerbals after Re-Fly merge

**Source:** `logs/2026-04-25_2334_refly-followup-test/KSP.log:13401-13404`
and `Player.log:73024+`. After the in-place continuation Re-Fly
re-loaded into FLIGHT, the orphan-placement pass deferred all three
original-crew kerbals (Jeb / Bill / Bob) to their stand-ins (Lola /
Milfrey / Jesrick); the booster was the active vessel post-Re-Fly,
not the capsule that has them:

```
[WARN][CrewReservation] Orphan placement deferred: no matching part with free seat in active vessel for 'Jebediah Kerman' → 'Lola Kerman' (snapshot pid=10668187 name='mk1-3pod') — stand-in kept in roster; reason=active-vessel-missing-snapshot-part activeParts=13 freeSeatParts=0 pidMatches=0 pidFreeSeats=0 nameMatches=0 nameFreeSeats=0 (attempted pidTier=yes nameTier=yes; cumulative pidHits=0 nameHitFallbacks=0)
```

Same shape repeats for Bill→Milfrey and Bob→Jesrick. The
single-pass summary follows at line 13404:

```
[INFO][CrewReservation] Orphan placement pass: orphans=3 placed=0 ... skippedNoMatchingPart=3
```

KSP-stock then warns 60+ seconds later that the original kerbals are
still assigned-but-no-vessels-references-them and flips them to
"missing":

```
[Player.log:73024] [ProtoCrewMember Warning]: Crewmember Jebediah Kerman found assigned but no vessels reference him. Last vessel: ID 0. ProtoCrewMember set as missing.
```

(Same warn for Bill and Bob.)

**Files to investigate:**

- `Source/Parsek/CrewReservationManager.cs` — orphan placement pass,
  the `active-vessel-missing-snapshot-part` deferral path, and
  whether end-of-recording capsule respawn (or the FLIGHT→TRACKSTATION
  trip that follows merge) re-runs the placement against the right
  vessel.
- `Source/Parsek/KerbalsModule.cs` — replacement dispatch.
- Cross-reference `#578` (recent crew-orphan-placement fix) — the
  `active-vessel-missing-snapshot-part` reason was the diagnostic
  that closure introduced. The deferral itself worked as intended in
  this playtest (stand-ins kept in roster, skipped fallback), but the
  player's expectation is that the original crew eventually get
  re-placed once the capsule re-spawns at end-of-recording.

**Open questions:**

1. Is this expected post-Re-Fly behaviour? The booster is the active
   vessel during the re-fly continuation; the capsule has its own
   recording that materializes later. The original 3 crew are
   reservation-bound to the capsule's command-pod pid=10668187, but
   that pid is not in scene at orphan-placement time.
2. Or is the orphan-placement deferral missing a path to re-run when
   the capsule re-spawns at end-of-recording (or the player switches
   to it)? `activeParts=13 freeSeatParts=0` suggests the booster's
   13 parts genuinely have no command-pod free seats, so the pass
   correctly defers. The question is whether there is a follow-up
   trigger.
3. Are the KSP-stock "set as missing" warns a downstream effect we
   need to handle? If Parsek's crew-reservation reset path eventually
   clears this state when the capsule re-spawns, the missing-flag
   should be reversible. If it does not, the original crew remain
   "missing" and are unrecoverable without manual save editing — a
   silent data-loss bug.

**Reproduction:** From the playtest, an in-place continuation Re-Fly
where the active-on-load vessel is the booster (not the capsule
holding the original crew). Active recording ends with the booster's
own destroyed terminal; the capsule's recording is still live in the
tree.

**Status:** Open — needs investigation. Not fixed in PR #(this).

**Spawner-side downstream:** see `#609` for the related fix that
prevents the deferred state from cascading into a permanent
`Spawn ABANDONED — all crew dead/missing` for the capsule recording.
The orphan-placement-deferred state itself is still as-designed; the
follow-up only removes the spawn-time abandon trap that turned it
into a hard data-loss bug.

---

## ~~609. Spawner abandons capsule recording forever when reserved crew is Missing post-Re-Fly~~

**Source:** `logs/2026-04-26_0118_refly-postfix-still-broken/KSP.log:14414`
(plus the `#608` deferred-orphan-placement preamble at line 13539-13541).
After the orphan-placement pass for a Re-Fly stripped capsule deferred
all three reserved kerbals (Jeb / Bill / Bob) — booster was the active
vessel, no command-pod free seats — the spawner's end-of-recording
spawn for the capsule (`#1 "Kerbal X"`) abandoned permanently:

```
[WARN][Spawner] Spawn ABANDONED for #1 (Kerbal X): all 3 crew are dead/missing — [Jebediah Kerman, Bill Kerman, Bob Kerman]
[VERBOSE][Spawner] Spawn suppressed for #1 "Kerbal X": already spawned (VesselSpawned=true)
```

KSP's natural respawn timer flipped the originals back to Available
~33 s later (log lines 14863-14874 `Crewmember X has respawned!`),
but the recording was already in the abandoned terminal state
(`VesselSpawned=true SpawnAbandoned=true`) and never re-attempted —
the post-Re-Fly continued timeline never appeared in the scene.

**Root cause:** `VesselSpawner.BuildDeadCrewSet` used
`IsCrewDeadInRoster` (Dead OR Missing) for every snapshot crew name,
asymmetric with `RemoveDeadCrewFromSnapshot`'s reserved-kerbal
carve-out (`reserved.ContainsKey(name) && !isStrictlyDead → keep`).
A reserved kerbal who was Missing because their original vessel had
just been Re-Fly-stripped therefore counted as dead in the all-dead
spawn-block guard, even though the spawn pipeline would have kept them
in the snapshot if the guard had let them through.

**Fix:**

- `BuildDeadCrewSet` now applies the same reservation carve-out:
  reserved-and-not-strictly-Dead crew are excluded from the dead set,
  and `ShouldBlockSpawnForDeadCrewInSnapshot` allows the spawn.
- New `RescueReservedMissingCrewInSnapshot` pre-spawn step
  (called from both `RespawnVessel` and `SpawnAtPosition`) flips
  reserved+Missing kerbals back to Available before the snapshot is
  loaded by `ProtoVessel.Load`, mirroring the existing rescue branches
  in `CrewReservationManager.ReserveCrewIn` and
  `PlaceOrphanedReplacements`.
- The `Spawn ABANDONED` WARN at `VesselSpawner.cs:1218` and
  `ParsekKSC.cs:1393` now reports a per-category breakdown
  (`total=N strictlyDead=N missingNotReserved=N reservedMissing=N alive=N [name: classification, …]`)
  instead of a flat name list, so future regressions can be diagnosed
  from the abandon line alone.
- New `[Verbose][Spawner] Spawn-block carve-out applied (#608/#609)` log
  fires whenever the carve-out turns a previously-abandoned scenario
  into a successful spawn — playtest logs make the recovery visible.

Regression coverage: `Bug609Tests.cs` (xUnit — pure pieces:
`ClassifySnapshotCrew` degraded path,
`FormatSpawnableClassificationSummary` shape, post-carve-out
`ShouldBlockSpawnForDeadCrew` decisions); in-game test
`Bug609_ReservedMissingCrewIsSpawnableAndRescued` in
`InGameTests/RuntimeTests.cs` exercises the live-roster path
(reservation registration → rosterStatus = Missing → classification +
spawn-block check + rescue, with full rollback).

**Known limitation (deliberate, not a bug):** saves authored before
this fix that already have a recording stuck at
`VesselSpawned=true SpawnAbandoned=true` from this exact path will
not auto-recover. A retroactive sweep would risk un-sticking
recordings that were correctly abandoned, so the fix is
forward-only — affected players need to re-trigger via save edit or
discard the abandoned recording. (Q1 design decision logged 2026-04-26.)

**Reproduction:** From `logs/2026-04-26_0118_refly-postfix-still-broken`,
an in-place continuation Re-Fly where the active-on-load vessel is the
booster (no command pod, no free seats) and the capsule's recording is
still live in the tree. KSP marks the original crew Missing on Re-Fly
strip; the orphan-placement pass defers the stand-ins (per `#608`); the
end-of-recording capsule spawn fires while the originals are still
Missing.

**Status:** CLOSED 2026-04-26 (PR #(this)). Cross-reference `#608`,
which still describes the orphan-placement-deferred preamble.

---

## ~~615. Re-Fly post-spawn churns crew stand-ins after rescue restored the originals — `Recreated stand-in` re-fires per recalc walk and the next ghost spawn's `Crew dedup` WARN catches the doubled-original~~

**Source:** `logs/2026-04-26_1025_3bugs-refly/KSP.log`. The same Jeb / Bill / Bob slot churns through generate -> rescue+remove -> recreate -> rescue+remove on every ghost spawn after the in-place Re-Fly:

- Lines 13883-13886 — initial stand-ins generated for the three reservation slots: `Erilan`, `Debgas`, `Rodbro`.
- Lines 14404-14407 — orphan placement deferred for the same three (no matching part with free seat in the active booster vessel) as expected post-#608.
- Lines 15374-15386 — first ghost spawn for the capsule recording: `Spawn-block carve-out applied (#608/#609)` rescues the three Reserved+Missing originals to `Available`, snapshot loads them onto the spawned vessel, then `Removed replacement '...' (was unused)` removes each historical stand-in from the roster.
- Lines 16101-16109 — recalculation walk runs after commit, rebuilds the reservations from the recording's `KerbalAssignment` actions (still in ELS), `EnsureChainDepth` re-uses the persisted `slot.Chain[0]` names, and `ApplyToRoster` step 1 sees `slot.Chain[i]` not in the roster (just removed) and fires `Recreated stand-in 'Erilan Kerman'` -> `Debgas` -> `Rodbro`. The replacement dictionary is repopulated in step 3 of the same call.
- Lines 18766-18769 — second ghost spawn for a sibling recording: same rescue+remove cycle, then `Crew dedup: 'Jebediah Kerman' already on a vessel in the scene — removed from spawn snapshot` WARN fires because the snapshot still carries Jeb but the active scene already has him on the first spawned vessel.

The `Crew dedup` WARN is defense-in-depth — it correctly prevents the original from being placed on two vessels — but it should not be exercised in the happy path. The root cause is the redundant `ApplyToRoster` recreate, which mints a brand-new ProtoCrewMember with the historical stand-in's name immediately after the spawn-side `UnreserveCrewInSnapshot` deleted it.

**Lifecycle:** the reservation derives from the recording's `KerbalAssignment` (Aboard / Recovered) action and is rebuilt every recalculation walk. The slot chain (`KerbalsModule.KerbalSlot.Chain`) is module-level state that persists across walks via `LoadSlots`, so the historical stand-in name survives. After the rescue places the original on the spawned vessel, the slot's "active occupant" is conceptually the original — but the chain still names the stand-in and the recreate-if-missing path runs unconditionally.

**Fix:** Added a rescue-completion guard at `KerbalsModule.ApplyToRoster` step 1. **P1 review revisions** (after the initial commit, then a second pass, then a third pass after a deeper user review):

The guard's predicate combines TWO signals — the rescue-specific marker set by `CrewReservationManager.MarkRescuePlaced` from the `VesselSpawner.RescueReservedMissingCrewInSnapshot` path (#608/#609) AND a live-vessel check on the same name. The "person being replaced at depth `i`" is `slot.OwnerName` at depth 0 and `slot.Chain[i-1]` at deeper levels. The first review iteration used only the live-vessel check, but the reviewer pointed out a fresh reservation where the kerbal sits on the **active player vessel** without ever passing through the rescue path would have hit the guard, the create path would have been skipped, the chain entry would have stayed null, step 3's `SetReplacement` would have emitted no mapping, and `SwapReservedCrewInFlight` would have had nothing to swap with — silent regression on every legitimate fresh launch. The combined predicate is rescue-specific:

- "on a live vessel" alone fires for fresh reservations on the active player vessel — wrong.
- "rescue-placed marker" alone fires after the rescued vessel was destroyed (kerbal back to Missing) — wrong.
- Combined fires only when the rescue path actually placed this kerbal AND they are still on a loaded non-ghost vessel — exactly the bug-repro happy path.

**P1 review (second pass):** the first revision installed `CleanUpReplacement` as the per-name marker-clearing site so a future fresh reservation of the same name would not see a stale signal. That broke the production lifecycle: both spawn paths (`VesselSpawner.RespawnVessel` and `VesselSpawner.SpawnAtPosition`) call `RescueReservedMissingCrewInSnapshot(spawnNode)` (sets the marker) and IMMEDIATELY follow with `CrewReservationManager.UnreserveCrewInSnapshot(spawnNode)` on the SAME snapshot, which loops every reserved kerbal in the snapshot through `CleanUpReplacement` — the marker was wiped before the next `ApplyToRoster` walk could read it. The guard read `IsRescuePlaced=false`, fell through to the recreate path, and the original churning the user reported was preserved (the defense-in-depth `Crew dedup` WARN at the downstream ghost spawn was the only thing keeping the kerbal off two vessels). The first-pass xUnit tests passed because they called `MarkRescuePlaced` directly and never exercised the `Rescue -> Unreserve -> ApplyToRoster` sequence — false green.

The second-pass fix decoupled the marker lifecycle from the per-name unreserve and one-shot consumed the marker on guard fire (via a new `CrewReservationManager.ConsumeRescuePlaced` API).

**P1 review (third pass):** the second-pass one-shot-consume design was also broken. The reservation slot is rebuilt on every `LedgerOrchestrator.RecalculateAndPatch` walk while the historical chain entry survives in `slot.Chain`. `RecalculateAndPatch` is invoked from 14+ call sites — every recording commit, KSC spending event (`OnKscSpending`, `OnVesselRolloutSpending`), vessel recovery (`OnVesselRecoveryFunds`), warp exit (`ParsekFlight.cs:6366`, `Warp exit detected — recalculating ledger`), scene transition (`ParsekScenario.cs:4301/4340/4394`), and save load. The merge-tail walk consumed the marker on first fire; the very next trigger saw `IsRescuePlaced=false`, took the legitimate "live-but-no-marker" branch, regenerated the stand-in, and the original lines 16106-16109 symptom returned. The second-pass tests only called `ApplyToRoster` once and missed this regression — the false-confidence test pattern.

The third-pass fix makes the marker PERSISTENT across `ApplyToRoster` walks:

- `CleanUpReplacement` does NOT clear the marker (unchanged from second pass — the marker survives the spawn pipeline's `UnreserveCrewInSnapshot` step).
- The `ApplyToRoster` guard does NOT consume the marker on fire. Every recalc walk for the lifetime of the rescue observes the same marker and skips the stand-in. The `ConsumeRescuePlaced` API was removed (no remaining callers).
- Bulk lifecycle paths (`LoadCrewReplacements`, `RestoreReplacements`, `ClearReplacements`, `ResetReplacementsForTesting`) are the only in-process clear sites — they wipe the marker set on session / rewind / wipe-all boundaries.

Within a session the marker accumulates harmlessly: the third-pass combined predicate's second clause required `IsKerbalOnLiveVessel`, so a stale-true marker for a kerbal who was no longer on a vessel fell through to the legitimate-recreate path. (No per-vessel-destruction clear hook is implemented — would need a `GameEvents.onVesselDestroy` subscription with a per-vessel kerbal walk.)

**P1 review (fourth pass):** the third-pass design failed on a stronger stale-marker scenario. The marker was scoped only by kerbal name and only cleared by bulk lifecycle paths. After a rescue placed Jeb on an early Re-Fly, the rescued vessel could be destroyed / recovered while the marker stayed set; later, a NEW unrelated reservation for Jeb (e.g. fresh contract / mission) could be created, and Jeb might happen to be on the active player vessel during this fresh reservation. The third-pass predicate `IsRescuePlaced(name) AND IsKerbalOnLiveVessel(name)` would then evaluate (true, true), the guard would fire, stand-in generation would be skipped, and `SwapReservedCrewInFlight` would have no stand-in to swap — recreating the original P1 failure mode (live-but-no-rescue treated as rescue, fresh reservation broken silently). The third-pass `MultipleApplyToRosterPasses_StaleMarker_VesselDestroyed_NextPassRecreatesStandIn` regression only covered the no-live-vessel case; it did NOT cover stale marker plus unrelated live vessel.

The fourth-pass fix scopes the marker to the vessel pid where the rescue placed the kerbal:

- The marker is now `Dictionary<string, ulong>` (kerbal name -> rescued vessel persistentId) on `CrewReservationManager`.
- `MarkRescuePlaced(name, vesselPid)` is the only mark API; the rescue spawn paths in `VesselSpawner.RespawnVessel` and `VesselSpawner.SpawnAtPosition` collect the rescued names through a new overload `RescueReservedMissingCrewInSnapshot(snapshot, rescuedNames)` and call `MarkRescuePlaced(name, pv.vesselRef.persistentId)` AFTER `ProtoVessel.Load` assigns the runtime pid (the snapshot's `persistentId` field is zeroed by `RegenerateVesselIdentity` before load, so the new pid is only available post-load).
- `TryGetRescuePlacedVessel(name, out vesselPid)` is the new accessor the guard uses.
- `IKerbalRosterFacade.IsKerbalOnVesselWithPid(name, pid)` is a new interface method whose production implementation walks `FlightGlobals.Vessels`, matches `vessel.persistentId == pid`, skips ghost-map vessels, and checks the named kerbal's `GetVesselCrew()`. The legacy `IsKerbalOnLiveVessel` is retained for diagnostic logging only (the declined-branch `legitimate fresh reservation` and `marker without live vessel` lines).
- The `ApplyToRoster` guard predicate is now `if (TryGetRescuePlacedVessel(replacedName, out rescuedVesselPid) AND roster.IsKerbalOnVesselWithPid(replacedName, rescuedVesselPid))`. If the kerbal moved off the rescued vessel, `IsKerbalOnVesselWithPid` returns false; the guard declines; the legitimate-recreate path runs.

Re-marking the same kerbal with a different pid OVERWRITES the prior pid (a later rescue supersedes the earlier one). The marker stays persistent within a session and is cleared only by the bulk lifecycle paths (`LoadCrewReplacements`, `RestoreReplacements`, `ClearReplacements`, `ResetReplacementsForTesting`). Stale entries with a now-invalid pid simply never match a live vessel — the predicate naturally returns false and the legitimate-recreate path runs. No per-vessel-destruction invalidation hook is needed.

When the guard fires, the chain entry stays in the slot as historical metadata so a future rewind / re-fly that re-reserves the original deterministically reuses the same stand-in name. Logging:

- `Marked rescue-placed: '<name>' vesselPid=<N>` (Verbose) at the spawn-side mark site, including the pid.
- `Re-marked rescue-placed: '<name>' vesselPid=<N> (superseding prior pid=<M>)` (Verbose) when a later rescue replaces an earlier pid for the same kerbal.
- `Rescue-completion guard:` (Verbose) per skipped depth, with `rescuePlacedPid=<N>` `onRescuedVessel=true` and the `marker persistent — not consumed on fire; pid-scoped` payload.
- `Rescue-completion guard declined:` (Verbose) when the kerbal is on a live vessel but not rescue-placed — diagnoses the legitimate fresh reservation case.
- `Stand-in recreate: rescue marker stale (kerbal '<name>' moved off rescued vessel pid=<N>)` (Info) — fourth-pass NEW log, fires on the bug-repro of this round (kerbal on a different live vessel from where the rescue placed them).
- `Stand-in recreate:` (Verbose) pins the legitimate-recreate fall-through with `rescuePlaced=<bool> onLiveVessel=<bool> onRescuedVessel=<bool> rescuedVesselPid=<N>` so the decision is auditable.
- `Rescue-completion guard fired: skipped N stand-in create/recreate(s)` (Info) once per `ApplyToRoster` walk summary.
- `Rescue-completion guard summary: fired=N (marker persistent — preserved across walks; pid-scoped) declinedLiveButNoMarker=X declinedMarkerButNotLive=Y declinedMarkerStalePid=Z` (Info) — fourth-pass aggregate visibility, with the new pid-stale bucket.
- `Cleared rescue-placed marker: '<name>' vesselPid=<N> (bulk lifecycle)` (Verbose).

The production `KerbalRosterFacade.IsKerbalOnLiveVessel` walks `FlightGlobals.Vessels` (skipping ghost-map ProtoVessels) and catches `TypeInitializationException` so headless xUnit tests calling `ApplyToRoster(KerbalRoster)` still work; the new `IsKerbalOnVesselWithPid` overload uses the same defensive try/catch.

**Files:** `Source/Parsek/CrewReservationManager.cs` (rescue-placed marker map keyed by name -> pid + `MarkRescuePlaced(name, pid)` / `IsRescuePlaced` / `TryGetRescuePlacedVessel` / `ClearRescuePlaced` API; `CleanUpReplacement` does not clear the marker; `SeedReplacementForTesting` + `CleanUpReplacementForTesting` seams), `Source/Parsek/VesselSpawner.cs` (`RescueReservedMissingCrewInSnapshot(snapshot, rescuedNames)` overload collects names; both `RespawnVessel` and `SpawnAtPosition` call `MarkRescuePlaced(name, pv.vesselRef.persistentId)` AFTER `ProtoVessel.Load`), `Source/Parsek/KerbalsModule.cs` (interface extension `IsKerbalOnVesselWithPid` + facade implementation + `ApplyToRoster` step 1 pid-scoped predicate, persistent marker — guard fires on every walk, fourth-pass summary log with `declinedMarkerStalePid`), `Source/Parsek.Tests/KerbalLoadDiagnosticsTests.cs` (`FakeRoster` interface conformance with no-match `IsKerbalOnVesselWithPid`), `Source/Parsek.Tests/RescueCompletionGuardTests.cs` (xUnit cases including the fourth-pass pid-scoping regressions `StaleNameMarker_KerbalOnUnrelatedActiveVessel_GuardDeclines_StandInGenerated`, `MarkerScopedByPid_KerbalOnDifferentVessel_GuardDeclines`, `MarkerScopedByPid_KerbalOnRescuedVessel_GuardFires`; lifecycle pins `MarkRescuePlaced_RemarkDifferentPidOverwrites`, `RescuePlacedMarker_BulkClearWipesPidEntries`; existing tests updated to use the pid-scoped fixture `GuardFakeRoster.MarkOnVessel(name, pid)` + the constants `RescuedVesselPid` / `UnrelatedVesselPid`), `Source/Parsek/InGameTests/RuntimeTests.cs` (in-game `RescueCompletionGuard_RescueThenUnreserveThenApplyToRoster_MarkerSurvives` updated for the fourth-pass pid-scoping contract: uses the new `RescueReservedMissingCrewInSnapshot(snapshot, rescuedNames)` overload, calls `MarkRescuePlaced(name, syntheticTestPid)`, and asserts pid-scoping survives across walks). The defense-in-depth `Crew dedup` WARN at `VesselSpawner.cs:2335` is preserved.

**Status:** CLOSED. Fixed for v0.9.0.

---

## ~~598. #526 follow-up: pad-vessel time-jump canaries observed `skipCount=0` — suppression armed but `[INFO][Flight] suppressing time-jump transient` never fired~~

**Source:** `logs/2026-04-25_2147/KSP.log` lines 75858 (`Time-jump launch auto-record suppression armed: jump=epoch-shift`) and 76410 (`FAILED: FlightIntegrationTests.RealSpawnControl_WarpToRecordingEnd_OnPad_DoesNotAutoStartLaunchRecording — Real Spawn Control pad canary should exercise the time-jump transient suppression path`); same shape for `TimelineFastForward_OnPad_DoesNotAutoStartLaunchRecording` at lines 81376 / 81654.

**Cause:** `EvaluateAutoRecordLaunchDecision` checked `!isActiveVessel -> SkipInactiveVessel` *before* `suppressForTimeJumpTransient -> SkipTimeJumpTransient`. During a Real Spawn Control / Timeline FF pad jump, the only `OnVesselSituationChange` events fired in the window are for the synthetic spawn vessel (`0 -> ORBITING`); the real pad vessel itself stays in `PRELAUNCH`. The non-active flickers were attributed to `SkipInactiveVessel` and only logged at `[VERBOSE][Flight] OnVesselSituationChange: ignoring non-active vessel`, so the in-game canaries never observed the suppression skip log they assert on.

**Fix:** Reordered `EvaluateAutoRecordLaunchDecision` so the time-jump transient check fires before the active-vessel check. Synthetic-vessel flickers during a jump window now route to `SkipTimeJumpTransient` and emit the canonical `[INFO][Flight] OnVesselSituationChange: suppressing time-jump transient (...)` line. The auto-record START decision for the real pad vessel is unchanged because that vessel still has no situation change in the window. New xUnit coverage in `Source/Parsek.Tests/AutoRecordDecisionTests.cs` pins the new ordering for the non-active-vessel-during-suppression case and for the `isRecording` precedence guard.

**Files:** `Source/Parsek/ParsekFlight.cs` (`EvaluateAutoRecordLaunchDecision` decision order), `Source/Parsek.Tests/AutoRecordDecisionTests.cs` (two new ordering regressions).

**Status:** CLOSED. Fixed for v0.9.0.

---

## ~~599. In-game test: `SaveLoadTests.CurrentFormatTrajectorySidecarsProbeAsBinary` failed for legacy-loop-migrated recording in every scene~~

**Source:** `logs/2026-04-25_2147` playtest. The in-game test failed for
recording `1bbb50cf98654a23a60b3248848b0301` ("Learstar A1") in EDITOR /
FLIGHT / MAINMENU / SPACECENTER / TRACKSTATION with
`Current-format recording '…' should keep its on-disk format version`.

**Diagnosis (2026-04-25):** the recording loaded at `formatVersion=3`
(`Player.log:31418`). `RecordingStore.MigrateLegacyLoopIntervalAfterHydration`
then ran (`Player.log:31487`) and called
`NormalizeRecordingFormatVersionAfterLegacyLoopMigration`, which bumps the
in-memory `RecordingFormatVersion` to v4 (the launch-to-launch loop interval
semantic). The `.prec` sidecar stayed at `BinaryV3 version=3` because v4 was
metadata-only — the binary layout is byte-identical to v3 and no rewrite was
required. The test asserted `AreEqual(rec.RecordingFormatVersion,
probe.FormatVersion)`, treating two distinct concepts (on-disk binary-encoding
version vs. in-memory semantic version) as the same number. They diverge by
design at v4 and above whenever a legacy save loads.

**Fix:** `RuntimeTests.CurrentFormatTrajectorySidecarsProbeAsBinary` now
asserts a narrow contract via the new
`RecordingStore.IsAcceptableSidecarVersionLag(probe, rec)` predicate:
equality is the ordinary case, and the only allowed lag is v3 sidecar with
v4 recording (the documented metadata-only legacy-loop migration). Every
other lag fails the assertion. v5 added serialized
`OrbitSegment.isPredicted` and v6 changed RELATIVE TrackSection point
semantics, so a v3-or-older sidecar paired with a v5 / v6 recording would
mean the binary on disk predates a contract change and the trajectory
data is genuinely stale. The first PR #567 relaxation to the broad
asymmetric `probe <= rec` would have hidden that case; review note P2
narrowed the exception. The runtime test also now explicitly asserts
`probe.Supported`. The production read path in `TrajectorySidecarBinary.Read`
already uses promote-only (`if (rec.RecordingFormatVersion <
probe.FormatVersion)`), so no production runtime code change was needed
beyond exposing the predicate. xUnit
`TrajectorySidecarProbeVersionContractTests` covers v3 / v3 (equality),
v3 / v4 (allowed legacy-loop migration), v3 / v5, v3 / v6, v4 / v6, and
probe&gt;rec (all rejected), plus the freshly-written-at-current-version
case that reduces to equality.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~601. Re-Fly load drops post-RP merge tree mutations (atmo/exo split halves vanish after Re-Fly)~~

**Source:** `logs/2026-04-25_2334_refly-followup-test/KSP.log`. User report: "the map view trajectory of the upper stage capsule was completely wrong, position around Kerbin was wrong, also there was only an in-atmo recording of the launch, but no exo recording of the upper stage — something definitely went wrong".

**Reproduction:**

1. RP `rp_129e07d7553e4695bf22541bec581f8a` was authored at booster decoupling (UT=159.24, `KSP.log:10777-10853`). The frozen `.sfs` snapshots the tree state at this point — 8 recordings.
2. The user committed the regular tree-merge dialog at 23:28:30. `RecordingOptimizer.SplitAtSection` ran on the capsule recording (`66be32fa...` -> atmo + new `b66cc068b7f84469b0cbb2d7a3960f6e` exo half) and the booster recording (`5ef9ecb7...` -> atmo + new `1c999fccf63b49edacc531473672ccca` exo half). Tree size grew from 8 to 10 recordings; both new halves got their own `.prec` sidecars on disk (`KSP.log:11865-11876`).
3. User invoked Re-Fly at 23:28:42 (`KSP.log:12544`). Re-Fly load read the RP's frozen `.sfs`, which still listed only the pre-split 8 recordings. The 2 post-split exo halves were absent from the loaded `RECORDING_TREE` ConfigNode.
4. `[Parsek][INFO][RecordingStore] TryRestoreActiveTreeNode: removed committed tree 'Kerbal X' (id=4dd8eafbdfa4406fb051966c8c59c863, 10 recording(s))` (`KSP.log:12662`) — the in-memory copy with all 10 recordings was destroyed. The replacement Limbo tree had 8 (`KSP.log:12663`). The capsule's exo half was never re-recorded post-Re-Fly (the user only re-flew the booster), so the upper-stage exo trajectory is lost.

**Cause:** the RP's `.sfs` is a static snapshot of the tree at RP authoring time. Any post-RP tree-shape mutation (`SplitAtSection`, supersede, branch-point edit) updates `RecordingStore.CommittedTrees` and writes new `.prec` sidecars but does NOT rewrite the historical RP `.sfs`. When Re-Fly later loads that `.sfs`, the loaded tree is the pre-mutation shape; `TryRestoreActiveTreeNode` then calls `RemoveCommittedTreeById`, dropping the only in-memory record of the post-mutation recordings.

**Fix:** Inserted `ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree` between the pending-tree salvage step and `RemoveCommittedTreeById` in `TryRestoreActiveTreeNode`. The helper looks up the in-memory committed tree by id, deep-clones any recording whose id is missing from the loaded tree (calling `MarkFilesDirty()` so the next `OnSave` rewrites the `.sfs` + advances the `.prec` sidecar epoch in lockstep), copies any committed-only `BranchPoint`, and overwrites `ParentRecordingIds` / `ChildRecordingIds` for any BranchPoint whose id matches but whose parent/child id lists diverge (the case where `SplitAtSection` rewrote the parent-id-list of an existing BP). Structured `[Scenario][INFO]` log line reports `loadedBefore=N committed=N after=N splicedRecordings=N refreshedRecordings=N splicedBranchPoints=N updatedBranchPoints=N source=committed-tree-in-memory`. Regression coverage in `Bug601ReFlyPostMergeSplitPreservationTests.cs` (7 tests). Design note: `docs/dev/plans/refly-rp-predates-merge-split-fix.md`.

**Review follow-up (PR #575):** P1 review caught that the initial splice loop only imported recordings whose ids were absent from the loaded tree, but `SplitAtSection` mutates the *original* recording in place — it truncates trajectory, moves terminal payload to the new second half, and reassigns `ChildBranchPointId` to the second half — before adding the second half to the tree. With the previous skip, a restored pre-split tree kept the stale full-trajectory original AND its old `ChildBranchPointId` link while the BP-update branch (already present) overwrote the parent BP's `ParentRecordingIds` to name the new second half, leaving the tree internally inconsistent. Fixed by adding a same-ID refresh path in the recording loop: when the committed copy diverges from the loaded copy on split-relevant structural fields (Points count, last-point UT, OrbitSegments / TrackSections counts, `ChildBranchPointId`, `TerminalStateValue`, `TerminalOrbitBody`), the helper now overwrites the loaded copy from the committed copy via `ApplyPersistenceArtifactsFrom` while preserving identity (RecordingId, TreeId, MergeState, etc.). The structured log line gains a `refreshedRecordings` field and the verbose ID list now distinguishes `splicedIds` from `refreshedIds`.

**Review follow-up #2 (PR #575 P1):** The first follow-up excluded the active recording from the same-ID refresh ("recorder is live-updating it"), but the reviewer pointed out that production calls the helper with `tree.ActiveRecordingId` and `SplitAtSection` keeps the original id on the truncated first half — so the active first half IS the post-split atmo half and was the recording most likely to be left stale. Load-order analysis: at splice time the recorder has not yet bound to the active recording (rebind fires on the deferred `onFlightReady` callback after the splice), so there is no in-flight payload state on the loaded copy. The active recording now also gets the structural refresh, but in a recorder-state-preserving mode that snapshots and restores the `[NonSerialized]` mitigation flags any earlier load-time code path may have set: `FilesDirty`, `SidecarLoadFailed`, `SidecarLoadFailureReason`, `ContinuationBoundaryIndex`, `PreContinuationVesselSnapshot`, `PreContinuationGhostSnapshot`. The `activeRecordingId` parameter still exists, but its semantic flipped from "skip" to "treat specially". The structured log line now reports the refresh count split as `refreshedRecordings=N (full=N1 recorderStatePreserved=N2)`. Tests rewritten: `Splice_ActiveStaleFirstHalfAfterSplit_RefreshesAndPreservesRecorderOwnedState`, `Splice_NonActiveStaleFirstHalfAfterSplit_RefreshesInFullMode`, `Splice_RecorderOwnedFlagsPreservedOnActiveRefresh`, `Splice_ActiveRecordingAlreadyMatchesCommitted_NoRefreshNoFlagChurn` (the previous follow-up's `Splice_TreeWithStaleFirstHalfAfterSplit_RefreshesFirstHalfFromCommitted` and `Splice_RefreshDoesNotClobberActiveRecording` were retired — the first exercised a non-active path production never used and the second asserted the now-rejected skip semantics).

**Status:** CLOSED. Fixed for v0.9.0.

---

## ~~600. Stationary surface ghosts are hidden above 50x warp even though their mesh does not need per-frame motion~~

**Source:** User investigation request on 2026-04-25: high-warp playback hides all ghost vessels in KSC and flight view for performance, but vessels that are actually standing still should remain visible at any warp speed.

**Cause:** The high-warp mesh suppression policy was a global `warpRate > WarpThresholds.GhostHide` decision. It ran before the per-trajectory render path in flight and before the per-recording playback path in KSC, so it could not distinguish a moving trajectory from a `SurfaceStationary` section.

**Fix:** Added `GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp`, which preserves the old threshold for moving ghosts but exempts the current playback UT when its `TrackSection.environment` is `SurfaceStationary` (plus surface-only static trajectories). Flight playback now applies the decision per trajectory/loop UT, and KSC playback no longer returns early for all ghosts during high warp. FX suppression is unchanged, and overlap clones remain culled during high warp so the exemption only keeps the newest stationary primary mesh visible. Regression coverage lives in `Bug290_WarpSuppressionMapViewTests`.

**Status:** CLOSED. Fixed for v0.9.0.

---

## ~~597. Underlying logic: KSP's `onTimeWarpRateChanged` GameEvent fires at 1x roughly 4x more often than there are real rate changes, and `OnTimeWarpRateChanged` always re-runs `CheckpointAllVessels`~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 1090 of 1121
events were `1.0x`, but only 248 unique UT values appeared, and many
of those 248 had multiple sub-second-apart 1.0x events (e.g.
`19:08:26.557` and `19:08:26.567` both at UT≈21526.10). KSP fires the
event spuriously across scene transitions, warp-to-here, save/load
boundaries, and similar transients.

**Follow-up evidence (2026-04-28):** newest retained bundles
`logs/2026-04-27_2157_stage-separation-bugs` and
`logs/2026-04-27_1902` no longer show the original log volume after
#592's rate-limiting, and a sidecar scan found no zero/negative orbit
segments (`566` `.prec` files, `30` top-level orbit segments, `8`
track-section checkpoints in the newest bundle; `22` `.prec` files,
`10` top-level orbit segments, `10` track-section checkpoints in the
older bundle). The code still had a direct duplicate-boundary risk:
`CheckpointAllVessels` closed the current open orbit segment and opened
a replacement at the checkpoint UT, so an exact duplicate same-UT event
could append a zero-length segment before later cleanup.

**Why it matters:** Bug #592 only addresses the LOG noise. The
underlying `BackgroundRecorder.CheckpointAllVessels` call still runs
every event, closing and re-opening the same orbit segment at the
same UT for every background vessel. The work is idempotent (same
UT → identical segment shape → no observable behaviour change), so
it has not produced a known correctness defect, but it is wasted
work scaling with `backgroundVesselCount × eventCount` and could
mask a real correctness regression in the future ("why is this orbit
segment getting reopened mid-flight?").

**Fix:** `ParsekFlight.OnTimeWarpRateChanged` now lets the warp-start /
warp-end ledger/facility path run first, then skips only exact duplicate
checkpoint work scoped to the same active tree, same warp rate, and same
UT. `BackgroundRecorder.CheckpointAllVessels` is also idempotent at the
same orbit-segment boundary, so a direct duplicate call no longer closes
and appends a zero-length segment. Regression coverage:
`ParsekFlightWarpCheckpointTests` pins the duplicate-event predicate and
`BackgroundRecorderTests.CheckpointAllVessels_DuplicateBoundary_DoesNotAppendZeroLengthSegment`
pins the recorder-level guard.

**Status:** CLOSED 2026-04-28. Fixed for v0.9.1. No retained-log
correctness defect found; fixed the remaining performance / hygiene and
duplicate-boundary hazard.

---

## ~~525-followup. In-game terrain-clearance regression `ExplosionAnchorPosition_BelowTerrain_ClampsBeforeWatchHold` failed at the cycle-boundary camera-event count assertion~~

**Source:** `logs/2026-04-25_2147/parsek-test-results.txt`,
`logs/2026-04-25_2147/Player.log:137286-137304`. The original `#525`
fix added an in-game pin that drives a Destroyed loop recording across
a cycle boundary and asserts (a) the explosion anchor is terrain-clamped
before any watch hold reads it, and (b) the engine emits exactly one
`OnLoopCameraAction` event for the cycle boundary. The pin was authored
in commit `814fbf53` (2026-04-22) but only ran for the first time on the
2026-04-25_2147 playtest, where it failed:

```
[VERBOSE][TerrainCorrect] Explosion anchor clamp #525 ("Bug525ExplosionAnchor"): alt=63.8 terrain=64.8 -> 65.3 (clearance=0.5m) cycle=0
[VERBOSE][ExplosionFx] Stock FXMonger.Explode for ghost #525 ...
[VERBOSE][Engine] Ghost #525 parts hidden after explosion
[VERBOSE][Engine] Ghost #525 "Bug525ExplosionAnchor" ghost reused across loop cycle: from cycle=0 to cycle=1
[WARN][TestRunner] FAILED: ... - Loop cycle-change explosion should emit exactly one camera hold event
```

**Cause:** `GhostPlaybackEngine.UpdateLoopingPlayback`'s loaded-visuals
loop-cycle branch fired `OnLoopCameraAction(ExplosionHoldStart)`
correctly, then called `ReusePrimaryGhostAcrossCycle` which fired a
second `OnLoopCameraAction(RetargetToNewGhost)` at the end. The watch
handler ignored the second event because `ExplosionHoldStart` had set
`watchedOverlapCycleIndex = -2` and `RetargetToNewGhost` is a no-op in
that state — so production behaviour was correct — but the API noise
broke the "exactly one camera hold event per cycle boundary" contract
that the regression test exercises.

**Fix:** Added an `emitRetargetEvent = true` parameter to
`ReusePrimaryGhostAcrossCycle`. The destroyed loop-cycle path now passes
`emitRetargetEvent: !needsExplosion` so the redundant retarget event is
suppressed only when an explosion was just emitted. The non-destroyed
boundary (`ExplosionHoldEnd`) still fires `RetargetToNewGhost` because
the watch handler genuinely needs it to swap the bridge anchor for the
new cycle's pivot. xUnit pins in
`Source/Parsek.Tests/Bug406GhostReuseLoopCycleTests.cs`
(`ReusePrimaryGhostAcrossCycle_NullGhost_EmitRetargetEventFalse_StillNoEvent`,
`ReusePrimaryGhostAcrossCycle_NullGhost_EmitRetargetEventTrue_NoEvent`)
fence the parameter API surface; the original
`ExplosionAnchorPosition_BelowTerrain_ClampsBeforeWatchHold` in-game
test now passes against a real ghost in flight.

**Status:** CLOSED 2026-04-25. Fixed on `fix/explosion-camera-hold-event`.

---

## ~~585-followup. Stale-sidecar Re-Fly load destroys sibling tree recordings on the next OnSave (data loss)~~

**Source:** `logs/2026-04-25_2210_refly-bugs/KSP.log`. The user did
a Re-Fly of the booster (`50f91cc6`) on a tree whose tree-recording
sidecar epochs had advanced past the `RewindPoint`'s `.sfs` epoch by
the time the player triggered the rewind. Two recordings hit bug
`#270`'s stale-sidecar mitigation on load:

```
[WARN][RecordingStore] Sidecar epoch mismatch for 22c28f04…: .sfs expects epoch 2, .prec has epoch 6 — sidecar is stale (bug #270), skipping sidecar load (trajectory + snapshots)
[WARN][RecordingStore] Sidecar epoch mismatch for 50f91cc6…: .sfs expects epoch 1, .prec has epoch 3 — sidecar is stale (bug #270), skipping sidecar load (trajectory + snapshots)
```

PR `#558` (bug `#585`) handled the active recording (`50f91cc6`):
the in-place-continuation marker swap rebinds the recorder, so
the empty in-memory state gets repopulated by the live re-fly.
But the OTHER recording (`22c28f04` — the launch / pre-decouple
"Kerbal X") had no recorder bound to it. Its in-memory state stayed
empty all session, and on scene exit at `21:59:55.747`:

```
[VERBOSE][RecordingStore] WriteBinaryTrajectoryFile: recording=22c28f04… points=0 orbitSegments=0 trackSections=0 sparsePointLists=0
[VERBOSE][RecordingStore] SaveRecordingFiles: id=22c28f04… wroteVessel=False wroteGhost=False
```

The previous good write of this recording at `21:57:40` carried
`points=400 orbitSegs=6 trackSections=32`. The save clobbered it
with empty data; the `.prec` on disk now reads `points=0`, the
launch row vanishes from the post-rewind timeline (`Recording
collector: 2 entries from 7 recordings`), the launch ghost button
is `disabled (no ghost)`, and the user's original launch trajectory
is permanently destroyed.

This is the same family as `#585` (bug `#270` mitigation +
in-place continuation Re-Fly) but on a different axis: `#585`
patched the active recording's restore; this one is the SAVE side
of the same shape — the empty in-memory state of any non-active
hydration-failed recording gets written through to disk.

**Files investigated:**

- `Source/Parsek/RecordingStore.cs` — `SaveRecordingFilesToPathsInternal`
  unconditionally writes the trajectory sidecar from in-memory state.
  The `Recording.SidecarLoadFailed` flag is set during load
  (`MarkSidecarLoadFailure` at the stale-sidecar-epoch path) but
  never consulted at save time.
- `Source/Parsek/Recording.cs` — `[NonSerialized] internal bool
  SidecarLoadFailed` exists already; its companion
  `SidecarLoadFailureReason` carries `"stale-sidecar-epoch"` for
  this case.
- `Source/Parsek/ParsekScenario.cs` — the existing
  `RestoreHydrationFailedRecordingsFromPendingTree` salvage path
  does clear the flag for recordings it can recover from a
  matching `pendingTree`, but it requires the pending tree to
  exist + match by id, which doesn't always hold under Re-Fly load.

**Resolution (2026-04-26 / 2026-04-27 integration):** Fixed in
`fix/585-587-followup` (PR #572) which integrates parallel work from
`fix/refly-bugs-2210` to provide two complementary layers of
protection:

1. **Repair-from-committed-tree (caller-side, in-session recovery)** —
   `ParsekScenario.SaveActiveTreeIfAny` now invokes
   `RestoreHydrationFailedRecordingsFromCommittedTree` before
   iterating dirty recordings. For each active-tree record whose
   `SidecarLoadFailed=true` (excluding snapshot-only failures and
   the marker's active recording itself), the helper finds the
   matching committed tree by id and copies trajectory data
   (`Points`, `OrbitSegments`, `TrackSections`, `PartEvents`,
   `FlagEvents`, `SegmentEvents`, `Controllers`, `SidecarEpoch`,
   start-location, vessel name, persistence artifacts) over the
   empty active-tree record while preserving Re-Fly identity fields
   (`RecordingId`, `TreeId`, `MergeState`, `CreatingSessionId`,
   `SupersedeTargetId`, `ProvisionalForRpId`). The flag is cleared
   and the record becomes playable in-session.
2. **Skip-empty-overwrite (caller-side, defense-in-depth)** —
   `SaveActiveTreeIfAny` then skips any remaining
   `SidecarLoadFailed`+empty record via
   `ShouldSkipActiveTreeEmptySidecarOverwrite`, logging the
   structured `SaveActiveTreeIfAny: skipped empty sidecar
   overwrite` line and bumping the `skippedDegraded` counter on
   the iteration summary.
3. **Skip-empty-overwrite (callee-side, all save paths)** —
   `RecordingStore.SaveRecordingFilesToPathsInternal` is gated on a
   new pure-static helper
   `RecordingStore.ShouldSkipSaveToPreserveStaleSidecar(rec)` that
   returns true iff `rec.SidecarLoadFailed` is true AND the
   in-memory state is effectively empty (no `Points`,
   `OrbitSegments`, `TrackSections`, `PartEvents`, `FlagEvents`,
   `SegmentEvents`, `VesselSnapshot`, or `GhostVisualSnapshot`).
   The gate covers BgRecorder out-of-band writes and scene-exit
   force-writes that bypass `SaveActiveTreeIfAny`. When the gate
   triggers, the saver returns success without touching any
   sidecar file, without incrementing the epoch, and emits a
   `SaveRecordingFiles: skipping write … preserving on-disk .prec`
   WARN. The active-recording case from PR #558 is unaffected:
   as soon as the recorder rebinds and adds any trajectory data,
   both gates evaluate to false and the save proceeds normally.

Tests: `Bug585FollowupSaveSkipTests` (10 cases — 8 predicate cases
covering null rec, flag false, each individual data field present,
and all-empty + flag set; 2 end-to-end cases pinning that the
on-disk `.prec` is byte-for-byte unchanged after a stale-flag save
attempt and that the active-recovered recording still writes new
data); plus integrated `QuickloadResumeTests.RestoreHydrationFailedRecordingsFromCommittedTree_RestoresFailedActiveTreeMatches`
and `RestoreHydrationFailedRecordingsFromCommittedTree_SkipsActiveRecording`
from the parallel branch pinning the repair contract.

**Companion fixes integrated from `fix/refly-bugs-2210` for the same
playtest's downstream cascades:** `MarkerValidator` accepts a marker
whose `TreeId` resolves through `RecordingStore.PendingTree` (not just
`CommittedTrees`), preventing the playtest's `21:59:57` "Marker invalid
field=TreeId" event when the active tree is in pending-Limbo;
`RewindPointReaper.ReapOrphanedRPs` preserves any RP referenced by the
live `ActiveReFlySessionMarker.RewindPointId`, preventing the playtest's
`22:07:14` "Marker invalid field=RewindPointId" event after a reap pass
deletes the marker's own RP; `ParsekFlight.UpdateTimelinePlaybackViaEngine`
gates timeline-ghost spawn/positioning while
`RewindInvokeContext.Pending` is true, closing a frame race between the
post-load coroutine and the strip/activate/atomic-marker-write critical
section. Test pins:
`LoadTimeSweepTests.MarkerValid_TreeExistsOnlyAsPendingTree_Preserved`,
`LoadTimeSweepTests.Reaper_PreservesEligibleRpReferencedByActiveMarker`,
`RewindTimelineTests.ShouldSkipTimelinePlaybackForPendingReFlyInvoke_ReturnsPendingState`.

**Status:** CLOSED 2026-04-27.

---

## ~~572-followup. FinalizeTreeRecordings clobbers a Re-Fly-stripped recording's terminal state on scene exit~~

**Source:** `logs/2026-04-25_2334_refly-followup-test/KSP.log:15829`.
Second-order data-loss companion to PR #572's
`RestoreHydrationFailedRecordingsFromCommittedTree`. After the
in-place-continuation Re-Fly's strip killed the capsule
(`Strip stripped=[2708531065]` at line 12821), `SaveActiveTreeIfAny`
on the next scene exit ran `RestoreHydrationFailedRecordingsFromCommittedTree`
to repair the capsule's active-tree record from the committed tree
(line 15767, restored 1 recording). The very next `FinalizeTreeRecordings`
pass (line 15828-15832) saw that the capsule's live pid was no longer
in `FlightGlobals.Vessels`, fell into the
`vessel pid=… not found on scene exit … inferred Landed from trajectory
(vessel was alive when unloaded)` branch in
`ParsekFlight.FinalizeIndividualRecording`, looked at the recording's
last trajectory point at altitude=10.6m (an early atmospheric
ascent point — the post-orbit data was pruned at chain segmentation
when the new probe child was created), and stamped `terminal=Landed`
onto the just-restored recording. The repair at line 15767 was
silently bulldozed.

User-visible symptom from the playtest: "the map view trajectory of
the upper stage capsule was completely wrong, position around Kerbin
was wrong" — `terminal=Landed` plus a populated `TerminalPosition`
caused the spawn-at-end safety net to materialize a vessel at
launch-site coordinates instead of leaving the orbit to play back as
a ghost.

**Cause:** the surface inference's working assumption is "vessel was
alive when unloaded" (player flew to Space Center while the vessel
remained in physics range). For a Re-Fly strip casualty whose
recording was repaired from a committed copy that was committed
mid-flight without a terminal state, that assumption is wrong — the
missing live pid is a deliberate kill, and the trajectory's last
point is whatever the committed copy carried, not a current "where
the vessel is right now" hint.

**Resolution:** added a transient
`[NonSerialized] Recording.RestoredFromCommittedTreeThisFrame`
flag set by `ParsekScenario.RestoreCommittedSidecarPayloadIntoActiveTreeRecording`
on every record it repairs from the committed tree. New
`ParsekFlight.ShouldSkipSceneExitSurfaceInferenceForRestoredRecording`
helper consults the flag in both
`FinalizeIndividualRecording`'s leaf scene-exit branch and
`EnsureActiveRecordingTerminalState`'s active-non-leaf scene-exit
branch; when the flag is set, the inference is skipped, the flag is
cleared on read, the existing
`PopulateTerminalOrbitFromLastSegment` orbit-metadata recovery still
runs (so the orbit fingerprint survives for ghost-map playback), and a
structured `FinalizeTreeRecordings: skipping Landed/Splashed inference
for '<id>' (vessel pid=<pid>) — repaired from committed tree this frame
… (lastPtAlt=<alt>m maxDist=<m> orbitSegs=<n>)` INFO line is emitted.
The orbit-then-land legitimate Landed-inference path
(`Bug278FinalizeLimboTests.EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory`,
`SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog`)
is unchanged because the flag is only set by the repair helper.

Tests: `Bug572FollowupFinalizeRestoredTests` (5 cases — leaf strip
casualty, active-non-leaf strip casualty, normal unloaded landing
regression guard, orbit-then-land regression guard, and an
end-to-end pin that the flag is cleared on read so a downstream
finalize call cannot accidentally re-trigger).

Plan in
`docs/dev/plans/refly-finalize-stripped-vessel-landed-fix.md`.

**Status:** Open until merged.

---

## ~~587-followup. Re-Fly post-supplement strip leaves non-Destroyed phantom vessels in scene~~

**Source:** `logs/2026-04-25_2210_refly-bugs/KSP.log:13590`. After
the in-place-continuation Re-Fly's
`StripPreExistingDebrisForInPlaceContinuation` ran, the diagnostic
`WarnOnLeftAloneNameCollisions` still tripped:

```
[WARN][Rewind] Strip left 1 pre-existing vessel(s) whose name matches a tree recording: [Kerbal X Debris] — not related to the re-fly, will appear as second Kerbal X-shaped object in scene
```

The user reported this as a long-standing visible bug: "I can STILL
see the upper stage doubled — both the ghost and a real vessel copy
(clickable) of it in front." Even after `#587`'s post-supplement
killed three of four matching debris in the playtest, a fourth
remained — its matching tree recording's `TerminalState` was not
`Destroyed`, so the `#587` predicate let it through.

**Cause:** `RewindInvoker.ResolveInPlaceContinuationDebrisToKill`
filtered the kill set on `rec.TerminalStateValue == TerminalState.Destroyed`
only. For an in-place-continuation Re-Fly, the SCOPE of "this
vessel is being superseded" is the session-suppressed subtree
(`EffectiveState.ComputeSessionSuppressedSubtree(marker)`),
which already includes non-Destroyed children of the origin
recording. Pre-existing real vessels matching one of those
recordings' names are phantoms from the old timeline that the
re-fly is overwriting — they should be killed too.

**Resolution (2026-04-26):** Fixed in `fix/585-587-followup` by
adding an optional `IReadOnlyCollection<string>
sessionSuppressedRecordingIds` parameter to
`ResolveInPlaceContinuationDebrisToKill`. The kill-eligible-name set
is now the UNION of (a) recordings with `TerminalState.Destroyed`
and (b) recordings whose `RecordingId` is in the suppressed-subtree
closure, while still excluding the active Re-Fly target's own vessel
name (so a duplicate-name vessel cannot bypass the protected-pid
gate). The production caller
(`StripPreExistingDebrisForInPlaceContinuation`) calls
`EffectiveState.ComputeSessionSuppressedSubtree(marker)` and passes
its result; the predicate is otherwise unchanged. A structured
VERBOSE log line breaks down `destroyedTerminal` / `suppressedSubtree`
counts so playtest logs can confirm which path matched. Tests:
five new cases in `Bug587StripPreExistingDebrisTests`
(`InPlaceMarker_KillsNameMatchingSuppressedSubtreeRec`,
`NullSuppressedSubtree_FallsBackToDestroyedTerminalOnly` (backward
compat), `SuppressedSubtreeAndDestroyedRecsBoth_KillsAllMatching`
(union), `SuppressedSubtreeKill_RespectsProtectedPids` (`#573`
contract), and `LogsKillEligibleCounters_WhenMatchesFound`
(structured log assertion)).

**Follow-up (2026-04-26, `logs/2026-04-26_1923_refly-upper-stage-still-broken`):** the rewind quicksave `parsek_rw_63bd3f.sfs` contained two full `VESSEL` blocks named `Kerbal X`: an old orbiting upper-stage vessel (`persistentId=3130558916`, `lastUT=264.30`) and the launch/start vessel (`persistentId=2708531065`). The post-load supplement killed three matching `Kerbal X Debris` vessels, then explicitly warned `Strip left vessels=1 ... [Kerbal X]`, which matches the user's clickable-parts copy. Technical conclusion: a Re-Fly invocation must load a slot-local save view where the selected slot is the only real KSP vessel. Fixed by scrubbing the root-level temp SFS copy before `GamePersistence.LoadGame`, keyed by the selected slot's vessel/root-part ids and leaving the original RP save plus `persistent.sfs` untouched. The strict `PostLoadStripper` path and active-parent-chain kill-eligible name expansion remain defense-in-depth after load. Regression coverage: `ReFlySaveScrubTests.ScrubQuicksaveToSelectedSlot_RemovesEveryNonSelectedVessel`, `PostLoadStripperTests.StrictStrip_StripsUnmatchedVessels`, and `Bug587StripPreExistingDebrisTests.ResolveDebris_InPlaceMarker_KillsNameMatchingParentChainRec`.

**Follow-up (2026-04-26, `logs/2026-04-26_2258_refly-upper-stage-instability`):** the slot-scrubbed temp save still carried old `sidecarEpoch` metadata from the Rewind Point quicksave while the committed `.prec` sidecars had newer epochs from post-RP optimizer/merge writes. On FLIGHT load, `RecordingStore` therefore skipped the active upper-stage/root sidecars as epoch mismatches, forcing bad state-vector fallbacks (`stateVecAlt=0`) and making the upper-stage orbit/trajectory unstable. The same package showed the replacement upper-stage tip had no explicit start/end metadata after optimizer split/trim, and the old probe-booster row was correctly superseded in ERS but still visible through raw-index consumers. The final-state complaint was not optimizer damage: the upper-stage exo half had a sub-surface periapsis, so it was a ballistic arc even though KSP reported `ORBITING` at scene exit. Fixed by refreshing recording `sidecarEpoch` values in the temp save from current sidecars before `GamePersistence.LoadGame` without downgrading when the temp save already has a newer epoch, stamping exact explicit bounds after optimizer split/trim, classifying live `ORBITING` vessels with sub-surface periapsis or unbound eccentricity as `SubOrbital`, and applying explicit supersede-relation suppression in the raw-index Recordings table, KSC playback, and Tracking Station map-ghost surfaces. The raw-index Recordings table now reuses one frame-local supersede relation list through group/block/row rendering instead of re-reading it per row. Regression coverage: `ReFlySaveScrubTests.ScrubQuicksaveToSelectedSlot_RefreshesRecordingSidecarEpochsFromCurrentSidecar`, `ReFlySaveScrubTests.ScrubQuicksaveToSelectedSlot_DoesNotDowngradeNewerSfsSidecarEpoch`, `RecordingOptimizerTests.SplitAtSection_BothHalvesHaveValidUTRanges`, `RecordingOptimizerTests.TrimBoringTail_ClampsExplicitEndUTToTrimmedBounds`, `TreeCommitTests.DetermineTerminalStateFromOrbitEvidence_OrbitingSubSurfacePeriapsis_ReturnsSubOrbital`, `TreeCommitTests.DetermineTerminalStateFromOrbitEvidence_OrbitingUnbound_ReturnsSubOrbital`, `GhostMapPresenceTests.ChainAware_SupersedeRelationSuppressesOldRecording`, `KscGhostPlaybackTests.ShouldShowInKSC_SupersededRelation_ReturnsFalse`, `RecordingsTableUITests.BuildGroupTreeData_SupersededRelation_HidesOldRecording`, and `EffectiveStateTests.IsSupersededByRelation_NotCommittedSuperseded_True`. Separate follow-ups remain for non-scrub Rewind Point quicksave loads, short warp-window state-vector/orbit-segment source flicker, and Tracking Station `relative-anchor-unresolved` handling when the absolute-shadow fallback is available but not wired into that path.

**Follow-up (2026-04-29, `logs/2026-04-29_2112_reflight-supersede-investigation`):** the Kerbal X Probe booster re-flight wrote the expected supersede row (`9e2308... -> 4fb8...`) and ERS skipped one superseded recording, but FLIGHT still fed the raw committed list into `GhostPlaybackEngine`, so the old one-point Destroyed booster recording spawned as a ghost. The same save exposed two cleanup gaps: stale auto-group hierarchy entries (`Kerbal X (2) / Debris -> Kerbal X (2)`) survived after their recordings were wiped/superseded and rendered as 0-recording UI groups, and a fully orphaned supersede row (`3e278... -> 478b...`) warned on every load even though neither endpoint existed. Fixed by adding a shared relation-superseded recording-id set for raw-index compatibility paths (flight flags, deferred spawns, held ghost retries, spawn-death checks, Tracking Station spawn handoff, Tracking Station action-context materialization), pruning hierarchy entries from display-effective group membership on load/save/wipe, and removing only fully orphaned supersede rows during load-time sweep while preserving one-sided orphans. Regression coverage: `FlightPlaybackExplainabilityTests.ComputePlaybackFlags_SupersededByRelation_SkipsGhostAndSpawn`, `DeferredSpawnTests.FlushDeferredSpawns_PurgesSupersededRelationQueues`, `DeferredSpawnTests.RetryHeldGhostSpawns_ReleasesSupersededRelationWithoutRetry`, `DeferredSpawnTests.RunSpawnDeathChecks_SkipsSupersededRelationRecording`, `TrackingStationSpawnTests.ShouldSpawnAtTrackingStationEnd_SupersededByRelation_ReturnsFalse`, `TrackingStationSpawnTests.TryRunTrackingStationSpawnHandoffForIndex_SupersededRelation_DoesNotMaterialize`, `GhostTrackingStationPatchTests.BuildActionContext_SupersededRelation_DisablesMaterialize`, `GroupManagementTests.PruneUnusedHierarchyEntries_KeepsLiveAncestorsAndRemovesStaleAutoGroups`, `GroupManagementTests.ClearCommitted_PrunesGroupHierarchy`, and `LoadTimeSweepTests.FullyOrphanSupersede_RemovedAtLoadTime`.

**Status:** CLOSED 2026-04-29.

---

## doubled-upper-stage-ghostmap-protovessel. Re-Fly creates a real registered "Ghost: <name>" Vessel colocated with the active vessel

**Source:** `logs/2026-04-25_2334_refly-followup-test/KSP.log:13201`.
After the in-place-continuation Re-Fly of the booster, with the
`#587` and `#587 follow-up` strip fixes already applied, the user
still reported seeing a clickable, real vessel of the upper-stage
("Kerbal X") a few metres from the booster. The user clarified
that this is a *separate* vessel from the legitimate
`GhostPlaybackEngine` ghost (Ghost #0, 35 parts, audio, full
visuals) which they want to keep — the bug is that a real
KSP `Vessel` is also being created for the same recording.

```
[INFO][GhostMap] Created ghost vessel 'Ghost: Kerbal X' ghostPid=1130130569
type=Ship body=Kerbin sma=2 for recording #0 (state vectors alt=0 spd=2185.7
frame=relative) orbitSource=state-vector-fallback
stateBody=Kerbin stateUT=159.5 stateAlt=0 stateSpeed=2185.7 frame=relative
anchorPid=2676381515 ... orbit=True mapObj=True orbitRenderer=True registered=True
```

The capsule recording (`66be32fafffe49a7a4bc82467c5ee600`) is the
parent of the active Re-Fly recording in the same tree, with its
current `TrackSection` in `ReferenceFrame.Relative` anchored to
the live booster's pid (= the active Re-Fly target). The
state-vector-fallback path resolves a world position by
multiplying the anchor's transform by the recording's
anchor-local offset — placing the synthesised ProtoVessel right
next to the booster. The orbit synthesised at altitude=0
atmospheric ascent state is also degenerate (`sma=2 ecc=0.999999`).

**Why this is the third facet of `#587`:** the previous two
fixes targeted the *strip* side — pre-existing in-scene vessels
that survived `PostLoadStripper`. This one is on the
**`GhostMapPresence` create side**: the `Ghost: <name>`
ProtoVessel is *born* during Re-Fly (after strip ran), so the
strip-side guards never see it. The capsule recording is the
parent of the Re-Fly origin, so it is *outside*
`EffectiveState.ComputeSessionSuppressedSubtree`'s child-ward
closure and outside `IsSuppressedByActiveSession`'s gate too.

**Resolution (2026-04-25):** added the pure predicate
`GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFly`
and gated the create site in
`GhostMapPresence.CreateGhostVesselFromStateVectors` on it. The
predicate suppresses when (a) a `ReFlySessionMarker` is active,
(b) the marker is in the in-place-continuation pattern
(`origin == active`, mirroring `#587`'s placeholder carve-out),
(c) the resolution branch is `relative`, (d) the resolution's
`anchorPid` matches the active Re-Fly target's
`Recording.VesselPersistentId`, and (e) the recording being
mapped is in the active Re-Fly recording's parent chain (per
PR #574 review P2: scope to the parent BranchPoint topology so
legitimate `#583` / `#584` docking/rendezvous map ghosts whose
anchor happens to be the active vessel are NOT suppressed). The
predicate also signals retry-later semantics back through the
new `out bool retryLater` parameter on
`CreateGhostVesselFromStateVectors` /
`CreateGhostVesselFromSource`; the flight-scene caller
`ParsekPlaybackPolicy.CheckPendingMapVessels` keeps its
pending-map entry alive when this gate fires, so a recording
that is mid-Relative-section during Re-Fly is retried next
tick rather than permanently dropped from the queue. A new
structured INFO log line
`create-state-vector-suppressed: ... reason=refly-relative-anchor=active relationship=parent ... retryLater=true`
gives playtest logs a unique grep target. The
`GhostPlaybackEngine` in-physics-zone ghost is untouched —
exactly the one the user wants kept. Tests:
`Bug587ThirdFacetDoubledGhostMapTests` covers the user's exact
case (parent capsule recording during in-place Re-Fly of
booster) plus 17 defensive negatives — no-marker, placeholder
pattern, absolute branch, anchor-is-different-vessel,
zero-anchor, missing/zero pid in committed list, null
committed list, empty marker fields, `no-section` and
`orbital-checkpoint` branches, structured-log-line shape,
docking-target sibling (PR #574 P2 not-parent gate),
multi-hop grandparent walk, victim-is-active idempotency,
null/missing tree topology, null victim id, and direct
`IsRecordingInParentChainOfActiveReFly` coverage including
the BP-cycle bail.

**Status:** Open until merged.

---

## ~~570. Warp-deferred survivor spawn stayed queued outside the active vessel's physics bubble~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log`. Recording #15
`"Kerbal X"` was queued by `Deferred spawn during warp` at line 537676, then
`FlushDeferredSpawns` kept it queued outside the active vessel's physics bubble
1815 times through line 541400.

**Cause:** the deferred spawn queue correctly waits while warp is active, but the
post-warp flush reused active-vessel physics-bubble scoping. That is wrong for a
finished terminal survivor: once warp is inactive, the materialization path can
place the real vessel at its recorded endpoint. Keeping the spawn queued only
made it wait forever unless the active vessel moved within 2.3 km.

**Fix:** `ParsekPlaybackPolicy.FlushDeferredSpawns` now executes pending spawn
items once warp is inactive instead of re-checking the active-vessel
physics-bubble distance. Failed flag replays remain queued as before. Regression
`DeferredSpawnTests.FlushDeferredSpawns_SpawnsQueuedSplashedSurvivorAfterWarpEnds`
pins the splashed-survivor case from the log.

Post-warp flush can materialize every pending spawn in the first non-warp frame.
That is intentional for terminal survivors; expected pending counts are small.
If runtime evidence shows large batches hitch, throttle post-warp materialization
as separate performance work instead of reintroducing distance gating.

The regression uses the policy spawn override, matching existing headless
`DeferredSpawnTests` coverage. A future in-game canary should cover the live
`SpawnVesselOrChainTipFromPolicy` branch if this path regresses in KSP runtime.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

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
