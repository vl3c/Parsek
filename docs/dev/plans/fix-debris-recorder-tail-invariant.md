# Fix Plan: Parent-Anchored Debris Recorder Tail Invariant

Date: 2026-05-10

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-plan-debris-recorder-tail-invariant`

Branch: `plan-debris-recorder-tail-invariant`

Base: `fix/debris-relative-tail-retirement` at `1dc35700` (`Address debris tail review follow-ups`)

## Goal

PR #811 makes stale v12 parent-anchored debris tails harmless at playback: the ghost retires when neither the child Relative `frames` nor its `absoluteFrames` shadow covers the playback UT, even if `TrackSection.startUT/endUT` still claims coverage.

This follow-up prevents newly recorded sidecars from writing that bad shape in the first place. The recorder must not persist a v12 parent-anchored debris Relative section whose metadata outlives all authored rendering surfaces.

Keep PR #811's playback guard. Existing saves and retained user recordings can already carry stale tails, and load-time mutation is not part of this follow-up.

## Evidence To Preserve

Retained bundle in this workspace: `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-10_1713`.

This path is outside the plan worktree. It is the retained log bundle already present in the local workspace, not the `../logs/` output path that `scripts/collect-logs.py` uses for new captures.

The affected recordings are all v12 debris and all carry `DebrisParentRecordingId = 1354326de29a4af2891c52f151995c79`:

| Recording | Section start | Section end | Relative frames | Absolute shadow | Tail with no authored surface |
| --- | ---: | ---: | ---: | ---: | ---: |
| `a14b621b...` | 333.0519 | 376.2919 | 333.0519..344.8919 | 333.6119..344.8919 | 31.4000s |
| `e2b6aa4d...` | 333.0519 | 376.3319 | 333.0519..344.8919 | 333.6119..344.8919 | 31.4400s |
| `e71f0322...` | 351.2519 | 389.5319 | 351.2519..361.1719 | 351.8119..361.1719 | 28.3600s |
| `9e7b0faf...` | 351.2519 | 389.5319 | 351.2519..361.1719 | 351.8119..361.1719 | 28.3600s |

The `.prec.txt` sidecars show the bad producer shape directly:

```text
TRACK_SECTION
{
    ref = 1
    startUT = 333.05190452576693
    endUT = 376.29190452578905
    anchorRecordingId = 1354326de29a4af2891c52f151995c79
    POINT ... last relative ut = 344.891904525773
    ABSOLUTE_POINT ... last shadow ut = 344.891904525773
}
```

The renderer warning was a symptom of this stored shape. The follow-up bug is in the writer/finalizer pipeline.

## Current Suspect Path

The strongest current culprit is loaded-background debris finalization:

1. `BackgroundRecorder.EndDebrisRecording` sets `rec.ExplicitEndUT = endUT`.
2. It calls `OnVesselRemovedFromBackground`.
3. `OnVesselRemovedFromBackground` closes loaded state with `CloseBackgroundTrackSection(loadedState, Planetarium.GetUniversalTime())`.
4. `CloseBackgroundTrackSection` assigns `state.currentTrackSection.endUT = ut` without checking the last authored frame/shadow.
5. `FlushTrackSectionsToRecording` appends that section to `rec.TrackSections`.
6. `PersistFinalizedRecording` writes the overlong section immediately.

Other sites that close loaded background sections with a wall-clock UT must share the same invariant:

| Site | Risk | Expected follow-up action |
| --- | --- | --- |
| `EndDebrisRecording -> OnVesselRemovedFromBackground` | High. This matches TTL/destroyed/out-of-bubble debris termination. | Normalize before finalizer cache apply and before `PersistFinalizedRecording`. |
| `OnBackgroundVesselWillDestroy` | High. Destruction path closes and persists immediately. | Same flush-time normalization. |
| `Shutdown` | Medium. Scene teardown closes all loaded states at current UT. | Same flush-time normalization. |
| `FinalizeAllForCommit` | Medium. It already has `ResolveBackgroundCommitTrackCloseUT` for missing vessels, but later sets every background `ExplicitEndUT = commitUT`. | Normalize sections and clamp parent-anchored debris explicit end after all background flushes. |
| `FlushLoadedStateForOnRailsTransition` | Medium. It closes loaded Relative state at transition UT and sets `flushRec.ExplicitEndUT = ut`. | Normalize before appending boundary points and avoid extending parent-anchored debris Relative coverage without a terminal sample. |
| Environment / anchor transitions inside loaded background sampling | Lower. They close on the same physics-frame UT that is near an authored sample, but still use the common close helper. | Rely on the shared normalizer rather than per-site special cases. |

On-rails background state is not a producer for this specific stale Relative-tail shape because on-rails vessels do not emit env-classified Relative `TrackSection` payloads. The invariant still needs to protect transition and finalization paths that move from loaded Relative state into on-rails or commit/end metadata.

There is also a finalizer-side amplifier:

`RecordingFinalizationCacheApplier.TryGetLastAuthoredUT` currently treats `section.endUT` as authored when the section has payload. For stale v12 Relative debris, that is exactly the false premise. If a bad section reaches the applier before normalization, terminal acceptance can be based on metadata rather than authored renderable data. The follow-up should harden this helper or ensure every applier caller runs the normalizer first.

## Invariant

For a recording where:

```text
recording.IsDebris == true
recording.DebrisParentRecordingId != null
recording.LoopAnchorVesselId == 0
section.referenceFrame == Relative
```

the persisted Relative section end must be covered by an authored rendering surface:

```text
section.frames covers section.endUT using recorder-persistence coverage; or
section.absoluteFrames covers section.endUT using shadow-renderer coverage.
```

If neither covers the requested section end:

1. If a trustworthy terminal pose is available at that UT, append a real Relative sample and matching absolute shadow sample, then close at that UT.
2. Otherwise clamp `section.endUT` to the last UT covered by either renderable surface.

Do not duplicate the last local offset at a later UT just to satisfy the invariant. That recreates the stale-tail visual bug with cleaner-looking metadata.

Single-frame rule: keep PR #811's playback compatibility contract separate from the recorder persistence contract. Playback can still treat one Relative frame as covering a section for old data, but the recorder must not use one seed-only Relative frame to persist a long fresh debris section unless it also records an explicit static-hold contract and tests that shape. In the initial implementation, a single Relative frame without shadow should cover only its own UT for persistence. A single absolute shadow point is not section-wide coverage because `TryPositionFromRelativeAbsoluteShadow` interpolates between two samples.

Flat-point rule: flat `Recording.Points` are not a renderable terminal surface for v12 parent-anchored debris after a Relative section runs out of Relative/shadow coverage. A flat boundary point appended after section flush must either be represented as a real terminal Relative+shadow sample, or live inside a deliberate non-relative section that playback can render without the parent-relative contract. Otherwise it must not extend `TrackSection.endUT`, `ExplicitEndUT`, or endpoint decisions.

Fail-closed empty case: a v12 parent-anchored debris Relative section with no Relative frames and no absolute shadow frames has no renderable payload. Drop it during normalization, or clamp it to a zero-length section only if an existing downstream contract requires the section marker. Prefer dropping and logging unless a test proves zero-length retention is required.

## Proposed Implementation

### Phase 0: One-Time Retained-Data Verification

The producer path is already strongly identified above. Before changing behavior, optionally run a throwaway local audit helper against the four retained `.prec.txt` files to confirm the exact sidecar rows:

```text
recordingId
sectionIndex
section.startUT
section.endUT
relative.firstUT
relative.lastUT
absolute.firstUT
absolute.lastUT
explicitEndUT
terminalState
terminalUT when present
```

Do not land the retained log files or the throwaway audit as permanent test input unless a reviewer explicitly asks for it. The permanent CI regression should be synthetic and compact. The audit is verification evidence, not a prerequisite design step.

Expected output for the four known recordings: one Relative section each, `section.endUT` later than both frame tails by about 28-31s.

### Phase 1: Extract A Shared Authored-Coverage Helper

Add shared frame-list primitives, then keep recorder and playback faces separate so the engine does not gain `Recording` references:

```csharp
internal enum DebrisRelativeCoverageMode
{
    PlaybackCompatible,
    RecorderPersistable
}

internal static class DebrisRelativeCoveragePrimitives
{
    internal static bool RelativeFramesCoverUT(
        IList<TrajectoryPoint> frames,
        double sectionStartUT,
        double sectionEndUT,
        double targetUT,
        DebrisRelativeCoverageMode mode);

    internal static bool AbsoluteShadowFramesCoverUT(
        IList<TrajectoryPoint> frames,
        double targetUT);

    internal static bool TryGetCoverageEndUT(
        IList<TrajectoryPoint> relativeFrames,
        IList<TrajectoryPoint> absoluteFrames,
        double sectionStartUT,
        double sectionEndUT,
        DebrisRelativeCoverageMode mode,
        out double coverageEndUT,
        out string reason);
}

internal static class DebrisRelativeRecorderPolicy
{
    internal static bool IsParentAnchoredDebris(Recording rec);

    internal static ParentAnchoredDebrisTailNormalizationResult
        NormalizeParentAnchoredRelativeSections(
            Recording rec,
            string context);
}
```

`DebrisRelativePlaybackPolicy` should remain the `IPlaybackTrajectory`-based playback face and delegate only to the primitive list/range helpers where useful. `GhostPlaybackEngine` must stay `Recording`-free.

Implementation details:

- Gate on `IsDebris && DebrisParentRecordingId != null`; legacy v11 debris (`null`) and non-debris stay untouched.
- Skip live-loop debris (`LoopAnchorVesselId != 0u`) if that field is reachable on `Recording`.
- Expose separate modes for playback-compatible coverage and recorder-persistable coverage if sharing code with PR #811. Playback mode may preserve `SingleFrameCoversUT`; recorder mode must require a real endpoint sample unless an explicit static-hold shape is introduced.
- For multi-frame Relative `section.frames`, recorder-persistable coverage ends at the last relative frame UT.
- For single-frame Relative `section.frames`, recorder-persistable coverage ends at the frame UT unless the implementation introduces an explicit static-hold marker/contract and tests it.
- For `section.absoluteFrames`, renderable coverage ends at the last shadow frame UT only when `Count >= 2`.
- Do not use `TrackSection.endUT` as evidence that a surface exists.
- For the recorder-side normalizer, prefer `section.frames` and `section.absoluteFrames`. Flat `Recording.Points` projection can remain a playback compatibility fallback, but the current writer must not use flat projection to justify a parent-anchored debris Relative section it is about to persist.
- Recalculate `sampleRateHz` after clamping using the existing convention.
- Because `TrackSection` is a struct, write any mutated section back into the list by index.
- Mark files dirty, refresh cached stats, and rerun `RecordingEndpointResolver.RefreshEndpointDecision(...)` only when a mutation occurs.
- Emit one aggregate `ParsekLog.Warn("BgRecorder", ...)` per recording/context when normalization mutates anything. Include `recordingId`, `context`, `clampedSections`, `droppedSections`, old/new section end, relative range, shadow range, and `DebrisParentRecordingId`.
- Use this canonical warning shape so log tests and live grep agree:

```text
ParentAnchoredDebrisTailNormalize rec={recordingId} context={context} clamped={clampedSections} dropped={droppedSections} oldEnd={oldEnd:R} newEnd={newEnd:R} relTail={relativeTail:R} shadowTail={shadowTail:R} parentRec={DebrisParentRecordingId}
```

If PR #811 has already landed, consider moving its private frame coverage functions into this shared helper and making `DebrisRelativePlaybackPolicy` delegate to it. Avoid letting playback and recorder use slightly different definitions of "authored coverage."

Do not fold `RelativeAnchorResolver.PointListCoversUT` into this helper. The resolver answers a different question: whether a child relative pose is resolvable at a UT. This plan's helper answers whether a recording has a renderable surface or a recorder-persistable section end. Similar single-frame mechanics are intentional but should stay separately named.

### Phase 2: Apply At Recorder Choke Points

Normalize as close as possible to the place where sections leave mutable background state:

1. In `FlushTrackSectionsToRecording`, normalize `state.trackSections` before appending them to `treeRec.TrackSections`. This catches most loaded-background close sites without changing every caller.
2. After the append, normalize `treeRec.TrackSections` defensively before `RecordingStore.AppendPointsFromTrackSections`. This catches any already-queued stale sections and keeps saved metadata consistent.
3. In `EndDebrisRecording`, stop setting `rec.ExplicitEndUT = endUT` as a final truth before the flush. Set it only after normalization, using the last renderable UT when this is a parent-anchored debris recording with no later valid non-relative payload.
4. In `FlushLoadedStateForOnRailsTransition`, do not let `flushRec.ExplicitEndUT = ut` extend a parent-anchored debris Relative section past its authored coverage unless a real renderable boundary sample was appended at `ut`. A flat-only boundary point is not enough; it must be a Relative+shadow terminal sample or a deliberate non-relative section that playback can render.
5. In `FinalizeAllForCommit`, after all loaded states are flushed and before the "Update ExplicitEndUT on all background recordings" loop finishes, clamp parent-anchored debris explicit ends to the latest renderable payload if no later valid orbit/checkpoint/absolute payload exists.
6. In `PersistFinalizedRecording`, optionally run an assertion-style validation before writing. If a stale v12 Relative tail is still found here, log a WARN with `reason=parent-anchored-debris-stale-relative-tail-at-persist` and normalize before save.
7. If persist-time validation mutates the recording after a finalizer cache or endpoint resolver already ran, immediately rerun `RecordingEndpointResolver.RefreshEndpointDecision(...)` so persisted endpoint metadata matches the normalized trajectory.

Explicit end rule:

- Clamp `Recording.ExplicitEndUT` only when the stale Relative section is the latest renderable payload and there is no later valid non-relative section, checkpoint, or accepted orbit tail.
- Do not treat flat-only `Recording.Points` as later valid payload for v12 parent-anchored debris once the active Relative/shadow coverage has ended. If such a point is intended to keep the debris visible, first convert it into an explicit renderable surface.
- Preserve `TerminalStateValue` and terminal metadata as outcome metadata. The ghost should disappear at the last trustworthy pose if there is no terminal pose sample.
- The recordings table should continue using terminal state for the outcome badge while the span/end UT reflects the last renderable pose. Destroyed FX for a no-pose late terminal should be anchored to the last renderable pose or suppressed by retirement, not played at an unreachable metadata-only UT.
- If a terminal event must visually occur at a later UT, the recorder must append a real terminal pose instead of stretching metadata.

### Phase 3: Harden Finalizer Last-Authored UT

Update `RecordingFinalizationCacheApplier.TryGetLastAuthoredUT` so v12 parent-anchored debris Relative sections do not contribute `section.endUT` as authored data unless the shared authored-coverage helper says the section end is covered.

Recommended behavior:

- For non-relative sections, keep the existing behavior.
- For legacy debris and non-debris, keep existing behavior unless tests show a broader bug.
- For v12 parent-anchored debris Relative sections:
  - consider every actual `section.frames` point UT;
  - consider every actual `absoluteFrames` point UT;
  - consider `section.endUT` only when Relative or shadow coverage accepts `section.endUT`.

This prevents the finalizer from accepting or rejecting terminal caches based on stale metadata if any caller misses the normalizer.

### Phase 4: Tests

Add focused xUnit tests first. Extend existing files where they already cover the path; add a new file only for the shared coverage helper:

- Add `Source/Parsek.Tests/DebrisRelativeSectionCoverageTests.cs` or similarly named helper tests.
- Extend `Source/Parsek.Tests/BackgroundTrackSectionTests.cs`.
- Extend `Source/Parsek.Tests/RecordingFinalizationCacheTests.cs`.
- Extend `Source/Parsek.Tests/RecordingStorageRoundTripTests.cs`.

Coverage checklist:

1. v12 parent-anchored debris Relative section `[333.0519, 376.2919]` with Relative/shadow frames ending `344.8919` clamps to `344.8919`.
2. Same fixture updates `sampleRateHz`, marks the recording dirty, invalidates cached stats, refreshes endpoint decisions, and logs one aggregate warning.
3. Shadow extends later than Relative frames: clamp to shadow tail when `absoluteFrames.Count >= 2`.
4. Relative frames extend later than shadow: clamp to relative tail.
5. Single Relative frame without shadow does not preserve a long freshly recorded section by default; it clamps to the frame UT unless an explicit static-hold contract is added.
6. Single absolute shadow frame alone does not justify a long section.
7. Empty Relative section with no shadow is dropped or zero-length according to the final implementation choice, with a WARN.
8. Legacy debris (`DebrisParentRecordingId == null`) is untouched.
9. Non-debris Relative recording is untouched.
10. Live-loop debris is untouched if `LoopAnchorVesselId != 0u` applies.
11. `FlushTrackSectionsToRecording` normalizes stale queued sections before they reach `treeRec.TrackSections`.
12. `EndDebrisRecording` no longer leaves `ExplicitEndUT` later than the last renderable payload when no terminal pose exists.
13. `FlushLoadedStateForOnRailsTransition` does not extend `ExplicitEndUT` for a flat-only boundary point after parent-anchored Relative/shadow coverage ended.
14. `FinalizeAllForCommit` does not overwrite a clamped parent-anchored debris end with raw `commitUT`.
15. `RecordingFinalizationCacheApplier.TryGetLastAuthoredUT` ignores stale v12 Relative `section.endUT`.
16. A synthetic sidecar round-trip preserves the normalized section end and does not regenerate the stale tail.
17. A log-capture test asserts the new WARN reason and one-line aggregate format.
18. Persist-time fallback normalization after finalizer cache application reruns `RecordingEndpointResolver.RefreshEndpointDecision(...)`.

Do not add the full retained `.prec.txt` files as giant fixtures unless reviewers explicitly want them. A compact synthetic fixture that matches the four-row evidence table is enough for CI.

### Phase 5: Runtime Validation

Headless:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
```

If local testhost/NuGet environment fails, use the repo fallback:

```powershell
dotnet build Source/Parsek.Tests/Parsek.Tests.csproj --no-restore
```

Runtime, if the user is available to run KSP:

1. Build from the worktree root.
2. Reproduce a Kerbal X breakup that creates v12 parent-anchored debris.
3. Collect logs with `python scripts/collect-logs.py debris-recorder-tail-invariant`.
4. Inspect new `.prec.txt` sidecars and verify no v12 parent-anchored Relative section has `section.endUT` past both renderable frame tails.
5. Verify `KSP.log` has zero `[RelativeAnchorResolver] relative-anchor-unresolved` entries from the stale child-tail shape.
6. Verify any retirement warnings are playback compatibility only for old retained recordings, not new sidecars.

Do not close or restart KSP from the agent session. If KSP locks copied DLLs, logs, or sidecars, report the blocker and ask the user to close/restart manually.

## Documentation Changes For The Implementation PR

The implementation PR should update docs in the same behavior-changing commit:

- `CHANGELOG.md`: add a concise bullet that new v12 parent-anchored debris recordings no longer persist Relative section metadata beyond authored Relative/shadow payload.
- `docs/dev/todo-and-known-bugs.md`: update the existing closed "v12 parent-anchored debris section metadata outlived authored frames" entry under the same `## Done` header with a follow-up paragraph explaining that PR #811 fixed playback and this PR fixes the recorder-side producer invariant. Do not create a new open entry or reopen the closed history.
- `.claude/CLAUDE.md` and `AGENTS.md`: add one recorder gotcha: when closing/persisting v12 parent-anchored debris Relative sections, section end must be derived from authored Relative/shadow coverage unless a real terminal pose sample is appended.

## Out Of Scope

- Do not loosen `RelativeAnchorResolver.PointListCoversUT`.
- Do not mute or rate-limit away the resolver warning.
- Do not remove PR #811's playback retirement guard.
- Do not mutate existing user saves on load.
- Do not synthesize duplicated endpoint samples at later UTs.
- Do not solve the separate wide-debris-bracket optimization from the always-shadow follow-up (`docs/dev/plans/debris-always-shadow.md`) unless the same helper naturally exposes a low-risk cleanup.

## Risk Notes

Clamping can make new debris disappear earlier than the current stale metadata says. That is correct when no pose was authored for the later interval. If the desired terminal event timing is later, the fix is to author a terminal pose, not to keep a metadata tail.

The riskiest edge is `ExplicitEndUT`: it is shared by UI status, endpoint decisions, cleanup, and playback span. Clamp it only when the recording has no later renderable payload. Preserve terminal state separately so the outcome remains visible in the UI.

The second riskiest edge is drift between playback and recorder coverage definitions. Extracting shared coverage primitives from PR #811 is preferable to reimplementing the same rules in `BackgroundRecorder`.

## Expected Result

Fresh v12 parent-anchored debris sidecars should either:

- close their Relative section at the last authored Relative/shadow coverage UT; or
- contain a real terminal Relative plus absolute-shadow sample at the later close UT.

Playback should still retire old stale-tail sidecars through PR #811, but new recordings should stop producing the stale-tail shape that caused the thousands of resolver warnings.
