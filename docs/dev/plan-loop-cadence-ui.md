# Plan: Loop-period input shows effective cadence (runtime-clamped)

## Problem

Parsek caps the number of simultaneously-live ghost clones per recording at
`GhostPlaybackEngine.MaxOverlapGhostsPerRecording = 10`. When the player sets
a loop period that would exceed that cap, the engine silently re-derives a
longer cadence for the per-frame spawn logic via
`GhostPlaybackLogic.ComputeEffectiveLaunchCadence`
(`GhostPlaybackLogic.cs:315-331`). The user's stored `LoopIntervalSeconds` is
untouched.

Result: the player types "5" in the loop-period input on a long recording and
the box keeps showing "5" forever, even though ghosts are actually relaunched
far less often. The UI silently diverges from observable in-game behaviour.

There is already one line of runtime log that surfaces the clamp
(`GhostPlaybackEngine.LogOverlapCadenceIfChanged`,
`GhostPlaybackEngine.cs:212-234`) - this is a good log-side marker but does
nothing for players who are not tailing KSP.log.

## Current behaviour - concrete Learstar A1 example

- Recording duration ~267.78s (Learstar A1).
- User types `5` in the loop period input (Sec unit).
- `Recording.LoopIntervalSeconds = 5.0` is persisted.
- At playback time, `UpdateOverlapPlayback` calls
  `ComputeEffectiveLaunchCadence(5, 267.78, 10)`.
- Current doubling algorithm: 5 -> 10 -> 20 -> 40. At 40,
  `ceil(267.78 / 40) = 7 <= 10`, accept.
- Effective cadence = 40s. Ghosts spawn every 40s, not 5s.
- The UI keeps displaying `5`.
- True minimum valid cadence is `267.78 / 10 = 26.778s`; current formula
  overshoots by ~49%.

## Proposed formula change

Replace the doubling iteration with a direct exact floor:

```csharp
internal static double ComputeEffectiveLaunchCadence(
    double userPeriod, double duration, int maxCycles)
{
    double period = Math.Max(userPeriod, MinCycleDuration);
    if (double.IsNaN(period) || double.IsInfinity(period))
        period = MinCycleDuration;
    if (duration <= 0 || maxCycles <= 0)
        return period;

    // Minimum cadence that keeps ceil(duration/cadence) <= maxCycles.
    // The exact floor is duration / maxCycles; if FP rounds it slightly low,
    // bump by one ulp until the cycle count fits.
    double floor = duration / maxCycles;
    while (Math.Ceiling(duration / floor) > maxCycles)
        floor = NextUp(floor);
    double effective = Math.Max(period, floor);
    return effective;
}
```

Properties:

- Monotonic: larger `userPeriod` is respected directly; we only ever raise.
- Exact: `ceil(duration / effective) <= maxCycles` is guaranteed by the
  exact `duration / maxCycles` floor plus a one-ulp FP guard; the existing
  Guard logic in
  `LogOverlapCadenceIfChanged` and `GetActiveCycles` both already tolerate
  this because they re-derive cycle counts with `Math.Ceiling` as well).
- Deterministic: no safety-bounded loop. No 64-iteration guard needed.
- Closer to user intent: Learstar A1 example goes from 40s to 26.778s.
- Non-pathological on the `maxCycles >= 10 && userPeriod >= MinCycle` path,
  which is the only path used in practice; also stays sane for
  `maxCycles = 1` (floor becomes the entire duration).

### One subtle case: floating-point exact floor

For a recording where `duration = 100.0001`, the exact floor is `10.00001`.
A user period of `10.5s` already satisfies the cap and must be preserved; an
integer `11s` snap would over-clamp. The implementation therefore uses
`duration / maxCycles` directly and nudges it upward by one ulp only if the
re-derived cycle count still exceeds the cap. Tests below pin both the cap
invariant and the "already-safe fractional period is preserved" case.

## Proposed UI change

The recordings-table loop-period cell (`RecordingsTableUI.DrawLoopPeriodCell`,
`UI/RecordingsTableUI.cs:3471-3570`) currently displays
`rec.LoopIntervalSeconds` converted into the selected unit. I will change it
to display the EFFECTIVE cadence.

### Display rule

Let `effective = ComputeEffectiveLaunchCadence(rec.LoopIntervalSeconds,
EffectiveLoopDuration(rec), MaxOverlapGhostsPerRecording)`.

- Use `GhostPlaybackEngine.EffectiveLoopDuration(rec)` so a narrowed loop
  range (LoopStartUT/LoopEndUT) uses the same duration the engine will use.
- Only apply the derived display to the enabled/manual path. Disabled rows and
  Auto mode keep their existing display rules.
- Display `effective` converted into `rec.LoopTimeUnit`. Reuse the existing
  conversion helper, but do not blindly reuse the old formatter:
  clamped display uses enough decimal places to keep the runtime cadence
  visible in every unit, including near-integer `Sec` floors such as
  `10.00001s`.
- Stored value on `Recording.LoopIntervalSeconds` is unchanged.

### Auto-update trigger

The input box shows `effective` in these situations:

1. Not focused (read-only view) - `loopPeriodFocusedRi != ri` branch.
2. Focused and the user just committed an edit (Enter or click-outside).
3. Focused but editing - we keep the raw `loopPeriodEditText` buffer as-is,
   so the user sees exactly what they typed. Auto-update only fires on
   commit. (Important so typing `26.778` doesn't get yanked into `30` as you
    type the last digit.)

On commit, if `effective != parsed`, the input-box display for that recording
immediately switches to `effective` on the next frame - that is the visible
"auto-update" the user sees. The user's stored value is still `parsed`.

### Focus-start rule

When a clamped read-only cell gains focus, do **not** seed the edit buffer from
the rendered text. Seed it from the stored raw value:

- `loopPeriodEditText = FormatLoopPeriodEditStartText(rec.LoopIntervalSeconds,
  rec.LoopTimeUnit)`
- This preserves the user's original request and fixes the no-op
  focus/Enter/click-away path that would otherwise overwrite raw with
  effective.

### Hint that the clamp is active

Add a subtle indicator when `effective != rec.LoopIntervalSeconds`:

- The input text colour goes a subtle orange/amber (`new Color(1.0f, 0.8f,
  0.4f)`). Existing cell style is reused - only `GUI.contentColor` is flipped
  inside a try/finally.
- The recordings window grows a real tooltip host in its footer (same
  zero-height/wrapped-label pattern as Settings/Test Runner). The clamped
  Period cell sets an override tooltip on hover:
  `"Runtime cadence clamped to Xs to keep concurrent cycles <= 10 (requested:
  Ys, duration: Zs)."`
- While the user is actively editing, do not colour or tooltip the raw buffer;
  clamp affordances apply to the derived read-only display only.

If `effective == rec.LoopIntervalSeconds` (the common case), the display is
identical to today.

### Why display effective, store raw

Decision: STORE RAW (`rec.LoopIntervalSeconds` keeps the user-typed value),
DISPLAY EFFECTIVE.

Pros of store-raw:

- A later fix to the formula, a change to `MaxOverlapGhostsPerRecording`, or
  a shortening of the recording (which can happen via loop-range tweaks)
  immediately gives the user a shorter cadence without them re-editing the
  box. The clamp is purely a derived presentation layer.
- Save-file round-trip is unchanged. No migration needed for existing
  recordings (especially important since clobbering would overwrite
  previously-typed-but-clamped values with their clamped value, losing the
  player's original intent).
- Matches how `AutoLoopIntervalSeconds` is treated elsewhere (Auto mode
  displays the global effective value without editing the underlying
  recording).

Cons considered:

- A user who types 5s, sees 26.778s displayed, and then lengthens the recording
  to 600s later on will get a new displayed value of ~60s (still clamped).
  Acceptable because the raw intent is preserved.

The plan reviewer should validate this decision before I code it.

## Files touched

- `Source/Parsek/GhostPlaybackLogic.cs` - replace
  `ComputeEffectiveLaunchCadence` body (lines ~315-331).
- `Source/Parsek/UI/RecordingsTableUI.cs` - `DrawLoopPeriodCell` display
  logic (lines ~3471-3570). Add small helpers:
  `ComputeDisplayedLoopPeriod`, `FormatLoopPeriodDisplayText`,
  `FormatLoopPeriodEditStartText`, and `BuildLoopPeriodClampTooltip`.
  `CommitLoopPeriodEdit` keeps its existing raw-store semantics.
- `Source/Parsek/UI/RecordingsTableUI.cs` - add a footer tooltip host for the
  recordings window so clamped Period hover text is actually rendered.
- `Source/Parsek.Tests/LoopPhaseTests.cs` - update the existing
  `ComputeEffectiveLaunchCadence_Tests` region (lines ~627-735). Five existing
  tests change expected values (they encode the doubling behaviour). Add
  new tests for the Learstar A1 scenario and the exact floor
  property. The `ComputeOverlapCycleLoopUT_WithEffectiveCadence_MatchesEnginePhase`
  test continues to pass once its expected effective cadence is updated to
  the new formula.
- `Source/Parsek.Tests/RecordingsTableUITests.cs` - pure tests for the UI
  display/edit/tooltip helpers (no IMGUI needed).
- `CHANGELOG.md` - one line under `## Unreleased` or the active working
  version.
- `docs/dev/todo-and-known-bugs.md` - note the fix if the bug was listed; it
  is not currently listed so this is optional.

## Data-model decision (to validate with plan reviewer)

**Store raw, display effective.** No schema change. No migration. The
decision hinges on:

- Existing clamp log (`LogOverlapCadenceIfChanged`) is engine-side and
  keyed by `(index, userPeriod, effectiveCadence, duration)`. It keeps
  working unchanged.
- Existing `ResolveLoopInterval` defensive clamp to `MinCycleDuration`
  stays. It also operates on the raw value.
- Watch-mode path (`WatchModeController.cs:2878-2889`) already uses
  `ComputeEffectiveLaunchCadence` to produce its own effective cadence -
  unaffected.
- `ParsekKSC.cs:418-426` overlap ghost path also calls the same function
  - unaffected.

The only wart: a pending behaviour from `RecordingStore.cs:5647-5680`
auto-adjusts `LoopIntervalSeconds` upward when it is below `MinCycleDuration`
on load (to dodge the `ResolveLoopInterval` per-frame warning). That is a
load-time one-shot and does not interact with the per-frame cap-clamp. No
action needed.

## Tests

### Formula unit tests (update + add)

Existing tests in `LoopPhaseTests.cs:627-735` will be updated. Expected
effective cadences:

- `WithinCap_ReturnsUserPeriod`: still 30 (duration 60, cap 20 -> floor
  `ceil(60/20) = 3`; effective = max(30, 3) = 30). No change.
- `PeriodBelowMin_ClampedToMinThenRaisedToCapFloor`: period 1 -> clamped to
  5; duration 164, cap 10 -> floor `164/10 = 16.4`; effective =
  max(5, 16.4) = **16.4**. Was 20.
- `ExceedsCap_DoubledUntilFits` (rename to
  `ExceedsCap_SnapsToCeilingDivFloor`): period 5, duration 1000, cap 10
  -> floor `ceil(1000/10) = 100`; effective = max(5, 100) = **100**. Was
  160.
- `CapOneDegenerate_DoublesUntilWholeTrajectoryFits`: period 5, duration
  100, cap 1 -> floor `ceil(100/1) = 100`; effective = max(5, 100) =
  **100**. Was 160.
- `ZeroDuration_ReturnsClampedMinPeriod`: unchanged - duration 0
  short-circuit.
- `ZeroCap_ReturnsClampedMinPeriod`: unchanged - maxCycles 0 short-circuit.
- `HugeDuration_TerminatesInBoundedIterations`: rename to
  `HugeDuration_ReturnsLargeButFinite`. `ceil(1e12/10) = 1e11`. Add
  `Assert.Equal(1e11, effective)`.
- `NaNPeriod_FallsBackToMin`: NaN -> 5; duration 60, cap 10 -> floor
  `ceil(60/10) = 6`; effective = **6**. Was 10.
- `ComputeOverlapCycleLoopUT_WithEffectiveCadence_MatchesEnginePhase`:
  update expected effective from 20 to **16.4** (period 1, duration 164,
  cap 10 -> floor 16.4). Recompute expected loopUT. `currentUT=100,
  loopStartUT=0, effective=16.4`. `lastCycle = floor(100/16.4) = 6`.
  `cycleStartUT = 6*16.4 = 98.4`. `phase = min(100-98.4, 164) = 1.6`.
  `loopUT = 0 + 1.6 = 1.6`. Update `Assert.Equal(1.6, loopUT, 6)`.

### New tests

- `LearstarA1_ClampFloorMatchesExactFloor`: period 5, duration 267.78, cap
  10 -> `267.78 / 10 = 26.778`. `Assert.Equal(26.778, effective, 6)`.
- `ExactFloorKeepsConcurrentCyclesWithinCap` (property-ish): for a range of
  `(period, duration, cap)` tuples, assert
  `Math.Ceiling(duration / effective) <= cap` holds whenever `cap > 0 &&
  duration > 0`.
- `FractionalUserPeriodAlreadyWithinCap_Respected`: period 10.5, duration
  100.0001, cap 10 -> effective stays 10.5 (no integer snap-to-11).
- `UserPeriodAboveFloor_Respected`: period 50, duration 100, cap 10 ->
  floor `ceil(100/10) = 10`; effective = max(50, 10) = 50. Guards against
  accidentally raising the cadence when the user is already above the
  floor.

### UI display helper tests

Extract a pure static helper
`RecordingsTableUI.ComputeDisplayedLoopPeriod(double storedSeconds,
double loopDurationSeconds, int cap, out bool clamped)` returning the
effective seconds. Tests:

- `StoredAboveFloor_NotClamped`: stored=30, duration=60, cap=10. Expect
  (30, false).
- `StoredBelowFloor_ClampedAndFlagged`: stored=5, duration=267.78, cap=10.
  Expect (~26.778, true).
- `ZeroDuration_ReturnsStored_NotClamped`: stored=30, duration=0, cap=10.
  Expect (30, false).
- `FormatLoopPeriodDisplayText_ClampedSeconds_ShowsFractionalCadence`:
  26.778s in `Sec` renders as `26.778`, not `26`.
- `FormatLoopPeriodDisplayText_ClampedSeconds_NearIntegerPreservesPrecision`:
  10.00001s in `Sec` renders as `10.00001`, not `10`.
- `FormatLoopPeriodDisplayText_ClampedMinutes_UsesExtraPrecision`: 26.778s in
  `Min` renders as `0.4463`, not `0.4` / `0.45`.
- `FormatLoopPeriodDisplayText_ClampedHours_UsesExtraPrecision`: 26.778s in
  `Hour` renders as `0.007438`, not `0.0` / `0.01`.
- `FormatLoopPeriodEditStartText_UsesStoredRawValue`: a clamped read-only
  display still seeds the edit buffer with the raw stored request.
- `FormatLoopPeriodEditStartText_Minutes_RoundTripsStoredRawValue` and
  `_Hours_...`: no-op focus/commit in `Min` / `Hour` does not mutate raw.
- `FormatLoopPeriodEditStartText_InvalidMinutes_FallsBackToExactMinCycle`:
  invalid stored values in `Min` seed the edit box with an exact 5s fallback,
  not a rounded `0.1m` / 6s mutation.
- `BuildLoopPeriodClampTooltip_ContainsKeyNumbers`: tooltip text includes
  effective / requested / duration / cap for cap-driven clamps.
- `BuildLoopPeriodClampTooltip_MinCycleOnly_DoesNotMentionCap`: minimum-period
  cleanup is described accurately and does not claim the overlap cap was
  binding.

## Rollout / risk

- Formula change is a tightening (shorter effective cadence in some cases).
  Ghosts will spawn more frequently in those cases. The cap invariant
  (`ceil(duration/cadence) <= maxCycles`) is preserved. No risk of the
  overlap spawn path exceeding its per-recording ghost cap.
- UI change is display-only. Stored data model unchanged. No save-file
  migration.
- Rollback path: `git revert` the two-line formula change and the UI diff.
  Stored `LoopIntervalSeconds` values remain valid.
- Manual verification: build DLL, playtest Learstar A1 with loop period 5s,
  see the Period cell display 26.778s in amber, hover it to read the footer
  tooltip, focus it and confirm the edit buffer starts from raw `5`, then
  commit and watch the read-only display snap back to 26.778s.

## CHANGELOG entry (wording)

Under `## 0.8.2` -> `### Fixes` (or the current working version):

```
- Loop period input now displays the effective runtime cadence. When the user-configured period would exceed the 10-concurrent-ghosts cap, the input shows the clamped value (amber) with a tooltip explaining the clamp; the user's original value is still stored.
```

Under the same heading, a second line for the formula tightening:

```
- Effective loop cadence under the ghost-cap clamp is now the exact minimum cap-safe cadence (with a one-ulp FP guard) instead of a power-of-two overshoot - closer to the user's requested period while respecting the cap.
```

## Diagnostic logging

Existing:

- `GhostPlaybackEngine.LogOverlapCadenceIfChanged` - unchanged. Still logs
  `requested`/`duration`/`cap`/`effective` on any input change.
- No new UI log is required for this fix. Per-row display is not logged
  because it would spam every frame; the amber state plus tooltip are the
  intended user-facing signal.

## Out of scope (explicit)

- Changing `MaxOverlapGhostsPerRecording` (explicitly out of scope).
- Surfacing the cap in `ParsekSettings` or the Settings window.
- Touching #450 / #414 / #406 overlap-engine / frame-budget paths.
- Altering `WatchModeController` effective-cadence consumers (they already
  use the same function - they benefit transparently).
- `ParsekKSC.cs` KSC-scene overlap path (already uses the same function -
  benefits transparently).
