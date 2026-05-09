# Implementation Plan v4: Anchor world-position discontinuity at Re-Fly post-load settle complete

> **Plan v4** — fixes v3 review findings: patch the actual KSP `FloatingOrigin.setOffset(Vector3d refPos, Vector3d nonFrame)` seam instead of the public wrapper; remove the nonexistent `Krakensbane.SetFrameVelocity` hook; make the stability hold cover the same frame settle clears; place the new flag assignment/gate at compile-correct sites; and avoid test-project-only builders in in-game tests.
>
> **v2 RESOLVED from v1:** speculative diagnosis (added diagnostic logging); dead-code step (a) removed; engine ↔ FlightRecorder boundary preserved (intent — see v3 BLOCKER fix); SetActive vs DestroyGhost asymmetry resolved (HIDE-ONLY); orbital-anchor path covered via `DebrisParentRecordingId` gate.

## Bug summary

When the player Re-Flies a vessel that has parent-anchored debris ghosts, the moment KSP completes the post-load placement of the live vessel (signaled by `[Recorder] Re-Fly post-load settle complete`), every ghost anchored to the parent recording teleports ~270m in world space simultaneously. Visually: every ghost in the scene jumps in the same direction by the same amount, in a single frame.

Evidence — `logs/2026-05-09_1845_refly-debris-chaotic-movement/KSP.log`, frame 10859→10860 wall-clock 18:43:30.103→18:43:30.128:

| ghost | rec | anchorPos pre→post | dM |
|---|---|---|---|
| #1 (Debris) | 67821a39 | parent-anchor shift below | 271.08 |
| #2-#6 (Debris) | a118a1dc, a4c7b20b, 1eb107c1, 24c0a291, 181d2905 | same parent-anchor shift | 269-271 |
| #7 (Probe) | 913516b6 | same parent-anchor shift | 267.46 |

Parent's anchorPos shifts from `(-181.12, -2.01, -188.55)` to `(2.16, 0.08, 6.15)` at the same single physics frame. **All 7 ghosts anchored to the parent shift by ~267m in the same vector direction — the parent's anchor pose is the shared dependency.**

## Diagnosis status — partially uncertain

The 267m anchor world-position shift is real and visible in the trace. The mechanism is almost certainly KSP's FloatingOrigin / Krakensbane stabilizing as the live Re-Fly vessel finishes physics-unpacking. **But the question of whether the live active vessel shifts in lockstep with the ghosts is unverified by the bundle data** — the bundle has no log of `FlightGlobals.ActiveVessel.transform.position` around the seam.

Two hypotheses:
- **H1 (asymmetric shift):** active vessel does NOT shift in lockstep with ghosts at the seam. Camera follows active vessel. Relative to camera, ghosts appear to teleport 267m.
- **H2 (frame-ordering race):** active vessel and ghosts both shift 267m, but Parsek's per-frame ghost-positioning runs at a different point in Unity's update order than KSP's FloatingOrigin shift. The trace shows the COMPUTED post-shift position; the actual `transform.position` after KSP's late updates may differ.

Both produce the same observable symptom and are addressable by the same fix shape: **defer ghost rendering until KSP's coordinate frame is fully stable.**

v4 ships diagnostic logging in the SAME PR as the fix (no two-stage split) so the verbose logs prove which mechanism fired without a separate PR cycle.

## 1. Fix shape

### 1.1 Always-on diagnostic logs (in the same PR as the fix)

Active during the Re-Fly post-load settle window AND for the gate's stability extension (see §1.3). Verbose level so they auto-suppress in normal play.

a) Active-vessel pose tracking — log `FlightGlobals.ActiveVessel.transform.position` at TWO phases per physics frame (per the v2 review's IMPORTANT item):
- In `UpdateTimelinePlaybackViaEngine`, immediately before `engine.UpdatePlayback(...)`: `[Verbose][ReFlySettle] activeVesselPos.update=(x,y,z) frame=N`. Gate this log on the same Re-Fly settle stability tracker/window, not on reaching `IGhostPositioner.InterpolateAndPositionRelative`: the new unstable-anchor gate intentionally continues before any positioner call, so logging inside the positioner would miss the exact hidden frames.
- **In the existing `LateUpdate` method on `ParsekFlight` at `ParsekFlight.cs:1167`** (used today for ghost FloatingOrigin re-positioning): `[Verbose][ReFlySettle] activeVesselPos.late=(x,y,z) frame=N`. Extend the existing method, do NOT add a second `LateUpdate()` (would be a compile error).
This distinguishes H1 from H2: if the two values differ within a single frame, H2 is in play; if they're identical but ghost-relative-to-vessel jumps, H1 is in play.

b) Harmony postfix on the protected instance method `FloatingOrigin.setOffset(Vector3d refPos, Vector3d nonFrame)` in new `Source/Parsek/Patches/FloatingOriginSetOffsetPatch.cs`. Do **not** patch only the public static `FloatingOrigin.SetOffset(Vector3d refPos)` wrapper: automatic flight-scene recenters call the protected `setOffset` path, and KSPCommunityFixes' `FloatingOriginPerf` patch also targets that seam. Because `setOffset` is protected in the local KSP assembly, use a string/`AccessTools.DeclaredMethod` target such as `[HarmonyPatch(typeof(FloatingOrigin), "setOffset", new Type[] { typeof(Vector3d), typeof(Vector3d) })]` or an explicit `TargetMethod()`; do not rely on `nameof(FloatingOrigin.setOffset)` compiling in this assembly. Wrap the postfix body in `try/catch` per the existing `Patches/PhysicsFramePatch.cs` pattern; emit `[Info][ReFlySettle] FloatingOrigin.setOffset refPos=(x,y,z) nonFrame=(x,y,z) offsetNonKrakensbane=(x,y,z) magnitude=N at wallclock=T frame=N`. Record the exact `Time.frameCount`, `refPos`, and `nonFrame` through an extracted internal helper such as `ReFlySettleStabilityTracker.RecordFloatingOriginShift(refPos, nonFrame, frame)`. The postfix delegates to this helper; xUnit drives the helper directly instead of invoking Harmony or a KSP protected method.

c) No `Krakensbane.SetFrameVelocity` patch. The local KSP `Krakensbane` type exposes `AddFrameVelocity(Vector3d)`, `ResetVelocityFrame(bool)`, `Zero()`, and `GetFrameVelocity()`, but not `SetFrameVelocity`. For this PR, keep Krakensbane diagnostics to the active-vessel Update/LateUpdate pose logs, which can include `Krakensbane.GetFrameVelocity()` if useful. If later evidence proves a separate frame-velocity transition hook is needed, add a follow-up plan around the actual exposed methods.

The diagnostic/fix split from v2 is collapsed into a single PR per the v2 review's recommendation — diagnostic logs are verbose-level so they're cheap, and they already prove the mechanism while the gate is doing its job.

### 1.2 Engine-side gate via existing `TrajectoryPlaybackFlags[]` seam

(v2's BLOCKER fix — replaces the proposed `IPlaybackTrajectory` property.)

Add ONE field to `Source/Parsek/GhostPlaybackEvents.cs:97` `TrajectoryPlaybackFlags`:

```csharp
/// <summary>Hide ghost this frame: anchor pose is in the Re-Fly post-load
/// stability window where KSP's coordinate frame is still settling and
/// rendered world coords would teleport at the next FloatingOrigin shift.</summary>
public bool anchorReFlyUnstable;
```

The flags array is already built per-frame by `ParsekFlight.ComputePlaybackFlags` at `Source/Parsek/ParsekFlight.cs:14833` and consumed by the engine via `engine.UpdatePlayback(... cachedFlags ...)` at `:14969` / `:14741`. The 28-property data-only `IPlaybackTrajectory` interface stays clean; the transient host-policy bit lives where every other transient host-policy bit already lives.

In `ComputePlaybackFlags`, after the existing chain/skip computation and before the `flags[i] = new TrajectoryPlaybackFlags { ... }` initializer at `:14910`, compute a local:

```csharp
bool anchorReFlyUnstable = ResolveReFlySettleStability(committed[i]);
```

Then include the field inside the initializer:

```csharp
flags[i] = new TrajectoryPlaybackFlags
{
    ...
    anchorReFlyUnstable = anchorReFlyUnstable,
};
```

Do not assign `flags[i].anchorReFlyUnstable` before the initializer — `TrajectoryPlaybackFlags` is a struct and the initializer would overwrite the earlier write.

Add one shared predicate, preferably in `FlightRecorder`, so both playback and background sampling use the same stability window:

```csharp
internal static bool IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording(
    string recordingId,
    int frame = -1)
{
    int effectiveFrame = frame >= 0 ? frame : Time.frameCount;
    return IsReFlyPostLoadSettleActiveForRecording(recordingId)
        || ReFlySettleStabilityTracker.IsHoldActiveForRecording(recordingId, effectiveFrame);
}
```

`ResolveReFlySettleStability(Recording rec) → bool` is a new helper in `ParsekFlight` returning true when ANY of:
- `rec.IsDebris && rec.DebrisParentRecordingId != null && FlightRecorder.IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording(rec.DebrisParentRecordingId)` — the parent-anchored debris case, including the active settle window, same-frame clear hold, and FloatingOrigin extension.
- `FlightRecorder.IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording(rec.RecordingId)` — defensive: covers an edge case where a parent recording's own ghost is rendered during settle/hold (e.g., loop overlap scenarios or non-Re-Fly settle re-arming). During a normal Re-Fly the player vessel IS the parent recording, so its "ghost" doesn't render — but keeping this condition closes a future-proofing hole at zero cost.

Unit tests should pass the explicit `frame` parameter (or use a `ForTesting` wrapper around it) so they do not depend on Unity's global `Time.frameCount`.

Also update the two recorder-side parent-debris gates in `Source/Parsek/BackgroundRecorder.cs`:
- periodic RELATIVE sample suppression at `BackgroundRecorder.cs:1837-1838`
- structural-event snapshot suppression at `BackgroundRecorder.cs:6361-6363`

Both currently call `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(...)`. Route both through `IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording(...)` so background samples/snapshots do not resume during the same clear-hold/extension window where playback is hiding anchored ghosts as unstable.

In `Source/Parsek/GhostPlaybackEngine.cs` per-ghost dispatch, add a new gate after both the `SessionSuppressionState` block and the current `state` / `ghostActive` locals at `GhostPlaybackEngine.cs:588-591`. The gate needs those locals, so do not insert it above their declaration.

```csharp
if (f.anchorReFlyUnstable)
{
    if (ghostActive && state.ghost.activeSelf)
    {
        state.ghost.SetActive(false);
        ResetGhostAppearanceTracking(state);
    }
    SetOverlapGhostsActive(i, false); // new helper; SetActive only, no DestroyGhost
    CountFrameSkip(GhostPlaybackSkipReason.AnchorReFlyUnstable);
    GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "anchor-refly-unstable");
    continue;  // HIDE-ONLY, no DestroyGhost/DestroyAllOverlapGhosts call
}
```

Do not write `state.suppressedThisFrame`: `GhostPlaybackState` has no such field. The skip counter and trace emission are the observability path.

`ResetGhostAppearanceTracking(state)` should run only on a visibility transition (`activeSelf == true` before hiding), not every gated frame. The clear-hold window can last ~60 Update frames; repeated appearance resets are unnecessary churn. Apply the same transition-only rule inside `SetOverlapGhostsActive`.

`SetOverlapGhostsActive(int recIdx, bool active)` implementation sketch: look up `overlapGhosts.TryGetValue(recIdx, out list)`, iterate the list, skip null states/ghosts, and call `state.ghost.SetActive(active)` only when `state.ghost.activeSelf != active`. When hiding, call `ResetGhostAppearanceTracking(state)` on that transitioned overlap state. Do not remove entries from `overlapGhosts`, do not call `DestroyAllOverlapGhosts`, and do not call any overlap-destroy helper.

Reactivation is intentionally implicit: when `anchorReFlyUnstable` flips false, the normal in-range render path resumes, and existing `RenderInRangeGhost` logic re-shows an inactive ghost with `state.ghost.SetActive(true)`. No bespoke reactivation branch is needed.

`GhostPlaybackSkipReason.AnchorReFlyUnstable` is added to `GhostPlaybackEvents.cs` enum with `ToLogToken` mapping `"anchor-refly-unstable"`.

The engine never imports `FlightRecorder` or the tracker — the host computed everything before calling `UpdatePlayback`. The standalone-mod boundary is preserved.

### 1.3 Event-driven stability hold and extension (replaces v2's fixed-frame timer)

(v2 review IMPORTANT: 3 frames is a guess; FloatingOrigin shifts can fire later than the recorder's settle-clear frame.)

The `FloatingOriginSetOffsetPatch` (§1.1.b) records the wallclock and frame of each origin shift. Add a small `internal static class ReFlySettleStabilityTracker` in the `Parsek` namespace (prefer a new `Source/Parsek/ReFlySettleStabilityTracker.cs` file). It must be accessible from `FlightRecorder`, `ParsekFlight`, `BackgroundRecorder`, `Parsek.Patches.FloatingOriginSetOffsetPatch`, and `Source/Parsek.Tests`; do not make it a private helper inside `ParsekFlight`. It owns:

- `lastFloatingOriginShiftFrame`, `lastFloatingOriginShiftRefPos`, `lastFloatingOriginShiftNonFrame`
- `lastSettleActiveRecordingId`, `lastSettleActiveFrame` (for reason/logging and shift attribution while settle was recently active; the live active predicate still comes from `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording`)
- `lastSettleClearedRecordingId`, `lastSettleClearedFrame`

`FlightRecorder.ShouldHoldReFlyPostLoadSettle` must notify the tracker before resetting its private settle fields:

- while `decision.Hold`, call `RecordSettleActive(reFlyPostLoadSettleRecordingId, Time.frameCount)`
- immediately before `ResetReFlyPostLoadSettle()` on `decision.Clear`, call `RecordSettleCleared(reFlyPostLoadSettleRecordingId, Time.frameCount)`

This explicit clear notification is required. If the recorder clears settle before playback flags are computed, polling `IsReFlyPostLoadSettleActiveForRecording()` alone would miss the exact frame where the live vessel can finish settling and FloatingOrigin can still shift before render.

`ResolveReFlySettleStability` returns true when:

```
settle predicate currently true for this recording or its parent recording
OR the relevant settle target cleared within StabilitySettleClearHoldFrames
OR (frame - lastFloatingOriginShiftFrame <= StabilityExtensionFramesAfterShift)
   AND the relevant settle target was active or cleared within StabilitySettleClearHoldFrames
```

Concretely: the gate is already engaged on the settle-clear frame, so a same-frame `FloatingOrigin.setOffset` that fires after playback Update cannot render a one-frame jump. The gate remains engaged for a bounded clear-hold window, then extends only for a few frames after any FloatingOrigin shift tied to that recent settle target. This avoids holding indefinitely after unrelated post-launch origin shifts.

Constants in `Source/Parsek/FlightRecorder.cs` near `attitudeSampleThresholdDegrees`:
- `StabilityExtensionFramesAfterShift = 2` (cleared 2 frames after the LAST shift)
- `StabilitySettleClearHoldFrames = 60` (hold anchored ghosts for about 1.0s at a typical 60 Hz Update cadence after settle clears; shifts inside this window are considered related to the settle seam). **Tag with `// TODO: retune from evidence in <log>` comment** so future readers don't treat the value as load-bearing — initial value is a guess pending playtest data.

Both internal const for unit-test pinning. After playtest, retune with evidence.

**Tracker static lifecycle:** reset all tracker fields in `ParsekFlight.OnEnable`, which fires when the flight-scene addon attaches at FLIGHT entry. This avoids stale frame counters tripping the gate on first flight frame after re-entering FLIGHT from SPACECENTER/TRACKSTATION without double-resetting from multiple scene hooks.

## 2. Risk surface

- **Loop playback:** loop ghosts use `LoopAnchorVesselId` (live PID), not parent recording id. The new flag is keyed on `DebrisParentRecordingId` and `RecordingId`. Loop path unaffected.
- **Watch mode:** `Source/Parsek/WatchModeController.cs` checks `state.ghost == null` for watched-ghost validity, NOT `state.ghost.activeSelf` — verified by reading the file. Hide-only for the ~1s clear-hold plus short extension does not exit watch.
- **GhostMapPresence:** map ghosts have their own positioning; do not call `RelativeAnchorResolver` per frame. Unaffected.
- **Orbital anchor path** (`TryResolveFlightOrbitalAnchorPose` at `ParsekFlight.cs:15195-15248`): orbital-anchor recordings still set `DebrisParentRecordingId` for v12 debris (verified by reading `BackgroundRecorder.RegisterChildRecordingsFromSplit` and `BuildBackgroundSplitBranchData`). Engine gate covers them.
- **Spawning during settle:** `deferredSpawnQueue` flushes spawn at the engine loop. The first frame after spawn already runs through the gate. Mesh-build at spawn time uses GhostVisualBuilder; mesh's local space is independent of world position. No bake-in.
- **Background-parent debris:** debris whose parent is a background recording is not gated by this fix. `IsReFlyPostLoadSettleActiveForRecording` intentionally consults only `PhysicsFramePatch.ActiveRecorder`; this bug is the focus Re-Fly vessel's FloatingOrigin/settle seam, not background-parent anchoring.
- **Multiple Re-Fly sessions / Gloops parents:** Gloops recordings always return false from `IsReFlyPostLoadSettleActiveForRecording` (per XML doc at `FlightRecorder.cs:5879-5885`). Acceptable — Gloops parents in Re-Fly are explicit non-goal noted in CHANGELOG.
- **FloatingOrigin patch** must NOT throw. Wrap in try/catch with rate-limited error log.
- **PR #787 (sampling cap):** unaffected — gate runs on the playback side after sample writes.
- **PR 3a/3b/3c (parent-anchor contract):** the new gate uses `DebrisParentRecordingId` exactly as the contract intends. No conflict.
- **PR #785 (radial-debris init):** seed-bridge code runs in `BackgroundRecorder.InitializeLoadedState`, not the per-frame playback gate. Unaffected.
- **Bug587StripPreExistingDebris / ERS / ELS audit:** read-only flag; no mutations.

## 3. Test plan

Headless xUnit in `Source/Parsek.Tests/`:

- `FloatingOriginSetOffsetPatchTests.cs` (new, `[Collection("Sequential")]` — ParsekLog shared): exercise the extracted `RecordFloatingOriginShift(refPos, nonFrame, frame)` helper with synthetic arguments; assert log line emitted with correct subsystem tag, method name, frame, and both vectors. Confirm try/catch wraps the Harmony postfix by keeping the postfix body to a small guarded delegate call.

- `ResolveReFlySettleStabilityTests.cs` (new, `[Collection("Sequential")]`):
  - `True_WhenSettleArmedForParentRecording`: arm settle on synthetic recorder with parent rec X; build v12 debris recording with `DebrisParentRecordingId = X`; assert true.
  - `True_WhenSettleArmedForOwnRecording`: parent's own ghost during its settle; assert true (probe-style case).
  - `True_OnSameFrameSettleClears`: record settle cleared for X at frame N; assert the parent-anchored debris gate is true at frame N even though `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(X)` is now false.
  - `True_DuringBoundedClearHold`: clear settle; assert true through `StabilitySettleClearHoldFrames`; assert false after hold expires when no shift occurs.
  - `True_WhenShiftExtendsRecentClearHold`: fire `RecordFloatingOriginShift` near the end of the clear-hold window; assert true for `StabilityExtensionFramesAfterShift` frames after the shift.
  - `False_WhenShiftFiresAfterClearHoldWindow`: clear settle, advance beyond `StabilitySettleClearHoldFrames`, then fire shift; assert false (shift unrelated to recent settle).
  - `False_ForLegacyV11Debris`: `DebrisParentRecordingId == null`; assert false (legacy debris uses `LegacyDebrisShadowGate` for its own concerns).
  - `False_ForGloopsRecording`: per docs, gloops always returns false.

- `BackgroundRecorderReFlySettleStabilityTests.cs` (new or extend existing background recorder tests): assert both parent-debris suppression call sites use the unified settle/hold predicate. Keep this as pure as possible by extracting tiny testable helpers for the periodic and structural-event predicates if direct driving is too heavy.

- `GhostPlaybackEngineAnchorReFlyUnstableTests.cs` (new xUnit scope): avoid live Unity `GameObject.SetActive` assertions here. Drive the engine with no pre-existing `ghostStates` and `flags.anchorReFlyUnstable = true`; assert the engine skips before calling the stub positioner, increments `AnchorReFlyUnstable`, and the frame summary includes the new counter. This is xUnit-safe because it does not require `new GameObject` or `activeSelf`.

- `FlightPlaybackExplainabilityTests.cs` (extend existing file; local tests already cover skip tokens and frame summaries here): add `AnchorReFlyUnstable` enum value + `ToLogToken` mapping `"anchor-refly-unstable"` and add `anchorReFlyUnstable=N` coverage in `FrameSummary_IncludesAggregateSkipCounters` / `ShouldEmitFrameSummary`.

In-game runtime test:

- In-game test (`Source/Parsek/InGameTests/RuntimeTests.cs`) `[InGameTest(Category = "ReFlySettle", Scene = GameScenes.FLIGHT)]` `ReFlyPostLoadSettle_GhostMeshHiddenDuringWindow`: construct the minimal synthetic v12 debris `Recording` and snapshot with production types inside `RuntimeTests.cs`; do **not** use `Source/Parsek.Tests/Generators/RecordingBuilder`, because the in-game test assembly cannot reference the xUnit test project. Install the synthetic recording into `RecordingStore`, let the engine build a real ghost, then drive the visibility assertions:
  - save and restore `Patches.PhysicsFramePatch.ActiveRecorder`
  - arm the **instance** recorder via `FlightRecorder.ActivateReFlyPostLoadSettleForTesting(string sessionId, string recordingId)` and assign that recorder to `PhysicsFramePatch.ActiveRecorder`, because `IsReFlyPostLoadSettleActiveForRecording` reads the static active-recorder slot
  - explicitly drive/simulate the clear lifecycle through tracker test hooks (`RecordSettleClearedForTesting`) or by invoking the recorder path that calls `ShouldHoldReFlyPostLoadSettle`; arming alone does not notify clear-hold
  - assert the ghost is hidden while active settle / clear-hold is true
  - drive `RecordFloatingOriginShiftForTesting` inside the clear-hold window and assert the extension branch keeps the ghost hidden for `StabilityExtensionFramesAfterShift`
  - wait past `StabilitySettleClearHoldFrames + StabilityExtensionFramesAfterShift` (not just 5 frames), then assert the ghost can reactivate
  - assert active-recorder and committed-recording state are restored in `finally`

- **Existing test touch-up survey** (per v3 review): four tests in `Source/Parsek.Tests/FlightPlaybackExplainabilityTests.cs` (`SupersededByRelation`, `LogsGhostSkipReason*`, `RewindRetired`, `RewindRetiredRelativeAnchorChain`) call `ComputePlaybackFlagsForTesting` via reflection. The new `ResolveReFlySettleStability` reads `FlightRecorder` static state, so these tests must remain `[Collection("Sequential")]` (they already are). Add one assertion per existing test that `flags[i].anchorReFlyUnstable == false` for non-debris recordings to lock the default.

## 4. Logging additions (per CLAUDE.md state-transition rule)

- `[ReFlySettle]` (new tag): all diagnostic logs (FloatingOrigin `setOffset` patch, Re-Fly settle stability tracker transitions, active-vessel transform tracking before engine Update + LateUpdate phases).
- `[Engine]` per-frame skip-summary line at `GhostPlaybackEngine.cs:330-355`: add `anchorReFlyUnstable=N` field.
- `[ReFlySettle]` or `[Playback]` Info one-shot from `ParsekFlight` while computing flags on first activation: `Anchor-pose Re-Fly stability hold engaged: rec=... ghosts=N anchorRec=... reason=settle-active|clear-hold|extension-window`. Keep this outside `GhostPlaybackEngine`; the engine only receives a bool flag and must not import recorder/tracker state.
- `[ReFlySettle]` or `[Playback]` Info from `ParsekFlight` on transition out: `Anchor-pose Re-Fly stability hold released: rec=... ghosts=N elapsedFrames=N`.
- Per-skip emission via existing `GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "anchor-refly-unstable")`.

## 5. Documentation updates

Same commit:

- `CHANGELOG.md` under `0.9.2 / Bug Fixes` — trimmed to one bullet, ≤2 sentences per memory rule: "Parent-anchored debris ghosts no longer teleport at Re-Fly post-load settle complete; the engine hides anchored ghosts during the settle window plus a short stability extension until KSP's coordinate frame stabilizes."
- `docs/dev/todo-and-known-bugs.md`: NEW top-level `## Done - Anchor world-position discontinuity at Re-Fly post-load settle complete`. Cross-reference recorder-side `BackgroundRecorder.cs:1837-1847`. Include diagnostic-log evidence pointer (which mechanism fired post-validation).

The bug evidence table in §1 corrects v2's wording: rec=913516b6 is the Probe (a separate vessel), not the parent recording itself; all 7 ghosts shift because they share the parent's anchor pose.

## 6. PR scope boundary vs Bug 2

Bug 2 (`Parsek-fix-tumbling-parent-rot-interp/`) targets rotation-interpolation in `RelativeAnchorResolver.cs:878/1074/1157` and the recorder-side BG attitude trigger.

**This PR touches only:**

- `Source/Parsek/Patches/FloatingOriginSetOffsetPatch.cs` (new; patches protected `FloatingOrigin.setOffset(Vector3d refPos, Vector3d nonFrame)` with a string/`TargetMethod` patch target)
- `Source/Parsek/ParsekFlight.cs` ~14833 (`ComputePlaybackFlags` populates `anchorReFlyUnstable`); new `ResolveReFlySettleStability` helper; Update-phase active-vessel pose logging immediately before `engine.UpdatePlayback`; one-shot hold engaged/released logs while computing flags; existing `LateUpdate` extended for active-vessel pose logging
- `Source/Parsek/ReFlySettleStabilityTracker.cs` (new internal static tracker)
- `Source/Parsek/GhostPlaybackEngine.cs` per-ghost dispatch (new gate after state/ghostActive locals); new SetActive-only overlap hide helper; frame counters updated
- `Source/Parsek/GhostPlaybackEvents.cs` (`anchorReFlyUnstable` field on `TrajectoryPlaybackFlags`; `AnchorReFlyUnstable` enum + log token)
- `Source/Parsek/FlightRecorder.cs` (new `StabilityExtensionFramesAfterShift` and `StabilitySettleClearHoldFrames` consts; notify tracker on settle hold/clear; shared `IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording` predicate)
- `Source/Parsek/BackgroundRecorder.cs` (route the periodic sample and structural-event snapshot parent-debris gates through the shared settle/hold predicate)
- `Source/Parsek.Tests/FloatingOriginSetOffsetPatchTests.cs` (new)
- `Source/Parsek.Tests/ResolveReFlySettleStabilityTests.cs` (new)
- `Source/Parsek.Tests/BackgroundRecorderReFlySettleStabilityTests.cs` (new or extend existing background recorder tests)
- `Source/Parsek.Tests/GhostPlaybackEngineAnchorReFlyUnstableTests.cs` (new)
- `Source/Parsek.Tests/FlightPlaybackExplainabilityTests.cs` (extend skip-token/frame-summary/default-flag assertions)
- `Source/Parsek/InGameTests/RuntimeTests.cs` (new test; use existing `FlightRecorder.ActivateReFlyPostLoadSettleForTesting`, construct minimal recording with production types)

**Not touched:** `RelativeAnchorResolver.cs`, slerp call sites, `BackgroundRecorder` attitude trigger, `IPlaybackTrajectory` (preserves the 28-property data-only interface). Bug 2's fix lives entirely in those files.

Shared files with Bug 2: `ParsekFlight.cs`, `GhostPlaybackEngine.cs`, `GhostPlaybackEvents.cs`. Disjoint regions in each:
- `ParsekFlight.cs`: Bug 1 ~14833 (`ComputePlaybackFlags`); Bug 2 ~16319-16367 (`InterpolateAndPositionRelative` + hysteresis state)
- `GhostPlaybackEngine.cs`: Bug 1 new gate after `SessionSuppressionState` and after the `state`/`ghostActive` locals; Bug 2 reads a different `TrajectoryPlaybackFlags` field at the same gate site (see Bug 2 plan v3).
- `GhostPlaybackEvents.cs`: Bug 1 adds `anchorReFlyUnstable` field; Bug 2 adds `anchorRotationUnreliable` field. Bug 1 adds `AnchorReFlyUnstable` enum entry; Bug 2 adds `AnchorRotationUnreliable`. Both can land in either order.

Three-way merge is clean either way.

`CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md`: textual conflicts on merge but no semantic coupling. Bug 1 entry references "see also: Bug 2 (tumbling-parent rotation interp)" so reviewers see the distinction.
