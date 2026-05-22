# Warp to Time (Timeline window)

## Goal

Add a "Warp to time" control to the Timeline window that jumps the game clock to a
user-entered absolute date (Year / Day / Hour / Minute). Forward in time is a plain
time jump; backward in time is achieved by rewinding to the most recent recording
launch at/before the target date and then fast-forwarding to the exact target.

Two cosmetic/layout changes ship in the same PR:

1. Right-align the stats footer ("n Recordings, m Actions, p Events").
2. Add the "Warp to time" button + four integer input boxes on a new row directly
   above the Close button.

## UI changes (`UI/TimelineWindowUI.cs`)

### A. Remove the stats footer

The bottom stats line ("n Recordings, m Actions, p Events") is removed entirely
(per follow-up feedback: it adds little once a timeline has many entries). This
deletes the footer label, the per-frame stats computation block, the
`cachedStatsText` field, and the now-unused `filterDirty` recompute flag and all
its assignments (a write-only private field would otherwise trip CS0414).

(The original plan right-aligned this line; that was superseded by removing it.)

### B. "Warp to time" row (above Close)

New row inserted between the stats footer and the existing `if (GUILayout.Button("Close"))`
block (`TimelineWindowUI.cs:429`). Layout, left-aligned:

```
[ Warp to time ]  [____] Year  [____] Day  [____] Hour  [____] Minute
```

- Button width = 2 x the shared filter button width. Use `FilterButtonWidth * 2f`
  (= 186px) per the user's "2x size of the filter buttons". (Using the constant, not
  `GetResponsiveButtonWidth()`, keeps the button a fixed 2x of the *minimum* filter
  width and avoids it ballooning when the window is wide.)
- Each input box is a narrow `GUILayout.TextField` (~36px) followed by its label
  ("Year"/"Day"/"Hour"/"Minute") as a `GUILayout.Label`.
- The row ends with `GUILayout.FlexibleSpace()` so the controls stay left-aligned.
- The button is enabled/disabled based on the resolved warp plan (see Decision logic);
  when disabled it carries a tooltip explaining why (e.g. "No launch save before that
  date" / "Stop recording first").

### C. Integer input boxes (commit on Enter / focus-loss)

Replicate the loop-period editing idiom from `RecordingsTableUI.cs` (focused-sentinel +
draft buffer + parse-on-commit). Four fields, so use an enum for which field is focused
plus a single draft string, OR four parallel draft/focus/rect triples. Recommended: a
small struct array keyed by field.

State fields on `TimelineWindowUI`:

```csharp
private enum WarpField { None, Year, Day, Hour, Minute }
private WarpField warpFocusedField = WarpField.None;
private string warpEditDraft = "";
private Rect warpEditRect;            // rect of the focused box, for click-outside detection
private int warpYear = 1, warpDay = 1, warpHour = 0, warpMinute = 0;  // committed values
private bool warpValuesLoaded;        // lazy load from persistence on first open
```

Per-field draw (mirrors `RecordingsTableUI.cs:4731-4783`):

- Not-focused: `GUI.SetNextControlName(name)`, draw `TextField` bound to the committed
  value's string. If `GUI.GetNameOfFocusedControl() == name`, seed `warpEditDraft` from
  the committed value, set `warpFocusedField`, capture `warpEditRect` from the last rect.
- Focused: detect Enter BEFORE drawing the field
  (`Event.current.type == EventType.KeyDown && (keyCode == Return || KeypadEnter)`),
  draw `TextField` bound to `warpEditDraft`, on Enter commit + `Event.current.Use()`.
- Escape cancels the edit (revert to committed), matching the rename idiom.

Click-outside commit (once per frame in `DrawTimelineWindow`, mirrors
`RecordingsTableUI.cs:783-790`):

```csharp
if (Event.current.type == EventType.MouseDown
    && warpFocusedField != WarpField.None
    && warpEditRect.width > 0
    && !warpEditRect.Contains(Event.current.mousePosition))
{
    CommitWarpField();
}
```

`CommitWarpField()` parses `warpEditDraft` via `WarpToTimeMath.TryParseField` (see below),
assigns to the matching committed value (rejecting/clamping), resets
`warpFocusedField = None`, `warpEditRect = default`, logs the commit.

### D. Persisting input values

Per the user: "save the input values when closing the window ... in case it's a special
date the user does not want to input every time." Persist across sessions via
`ParsekSettingsPersistence` (writes `GameData/Parsek/PluginData/settings.cfg`, survives
restart; the live `ParsekSettings` is a per-save `CustomParameterNode` and is NOT a good
home for these).

- Add four nullable stored ints + key consts: `storedWarpYear/Day/Hour/Minute`,
  keys `"warpYear"` etc.
- Parse in `LoadIfNeeded()` with `int.TryParse`.
- Add `RecordWarpDate(int year, int day, int hour, int minute)` that sets all four and
  `Save()`s once.
- Add public getters (`GetStoredWarpYear()` ...) returning the nullable.
- Test hooks: `ResetForTesting` clears, `SetStoredWarpXForTesting`.
- These are pure UI draft values; no `ApplyTo(ParsekSettings)` wiring is needed.

Load: in `DrawTimelineWindow`, on first draw (`!warpValuesLoaded`), read the four stored
values (defaulting Year/Day to 1, Hour/Minute to 0 when absent) and set the committed
fields, then `warpValuesLoaded = true`.

Save: when the window closes. The window closes via the Close button and via
`IsOpen = false` (set by `ParsekUI`). Centralize in a `private void CloseWindow()` that
calls `ParsekSettingsPersistence.RecordWarpDate(...)` then sets `showTimelineWindow = false`.
Route both the Close button and the `IsOpen` setter through it. (Also commit any pending
field edit before saving.)

## Date -> UT conversion (`WarpToTimeMath` - new pure static class)

KSP calendar convention (Year 1, Day 1 = game start, UT 0), confirmed with the user.
Months do not exist in KSP; the four fields are Year / Day / Hour / Minute.

```csharp
internal static double ComputeTargetUT(int year, int day, int hour, int minute)
{
    ParsekTimeFormat.GetDayAndYearConstants(out int secsPerDay, out int daysPerYear);
    long secsPerYear = (long)daysPerYear * secsPerDay;
    int y = Math.Max(1, year);     // 0/blank treated as Year 1 (KSP calendar start)
    int d = Math.Max(1, day);      // 0/blank treated as Day 1
    int h = Math.Max(0, hour);
    int m = Math.Max(0, minute);
    return (y - 1) * (double)secsPerYear
         + (d - 1) * (double)secsPerDay
         + h * 3600.0
         + m * 60.0;
}
```

- Uses `ParsekTimeFormat.GetDayAndYearConstants` so it respects the Kerbin (6h day /
  426 d yr) vs Earth (24h / 365 d) setting. No hardcoded day/year lengths.
- Hour/Minute are added arithmetically (`*3600`, `*60`) consistent with the rest of the
  codebase, which has no exposed hours-per-day constant. We do NOT clamp Hour to
  `hoursPerDay-1` or Minute to 59 at compute time: overflow is harmless (Hour 30 just
  means +1 day 6h). Field-level validation (below) clamps for clean UX but compute stays
  total-seconds-correct either way.

Field parse/validate:

```csharp
// Returns true + parsed value when the draft is a non-negative integer.
// Year/Day floor at 1; Hour/Minute floor at 0. Garbage returns false (keep old value).
internal static bool TryParseField(WarpFieldKind kind, string draft, out int value);
```

Display: the committed Year/Day/Hour/Minute render as their integer strings. The target
date for confirmation dialogs uses `KSPUtil.PrintDateCompact(targetUT, true)` for
consistency with the existing Rewind/FF dialogs.

## Decision logic (`WarpToTimeMath` / `WarpToTimeController`)

Pure decision (`WarpToTimeMath`, unit-tested):

```csharp
internal enum WarpPlanKind { ForwardOnly, RewindThenForward, AtTarget, Unreachable }

internal readonly struct WarpPlan
{
    WarpPlanKind Kind;
    bool RequiresFlightExit;     // true when in flight (save recording + return to KSC)
    bool LandsAtTimelineStart;   // RewindThenForward to earliest launch (target precedes all)
    string Reason;               // for Unreachable / disabled tooltip
}

// `hasRewindTarget` = a rewind-target launch was resolved (nearest-prior OR earliest
// fallback); `landsAtTimelineStart` = that target is the earliest launch and is itself
// after targetUT (so no forward jump will follow).
internal static WarpPlan DecideWarpPlan(
    double targetUT, double currentUT, bool inFlight, bool isRecording,
    bool hasRewindTarget, bool landsAtTimelineStart);
```

Rules (single epsilon constant `AtTargetEpsilonSeconds = 1.0` used everywhere a
"close enough / valid jump" comparison is made, so the decision and the deferred consumer
never disagree):

- `targetUT > currentUT + AtTargetEpsilonSeconds` -> `ForwardOnly`.
- `Math.Abs(targetUT - currentUT) <= AtTargetEpsilonSeconds` -> `AtTarget` (no-op, button
  disabled / message). Using >=1s matches `TimeJumpManager.IsValidJump` which aborts a
  forward jump unless `target > current` (`TimeJumpManager.cs:218-228`); a sub-second
  residual must never reach `ExecuteForwardJump`.
- `targetUT < currentUT - AtTargetEpsilonSeconds`:
  - a rewind-target launch exists (see resolution below) -> `RewindThenForward`.
  - else (no recording owns a usable rewind save at all) -> `Unreachable`
    (reason: "No launch save to rewind to").
- `RequiresFlightExit = inFlight` (forward and backward both route through KSC when in
  flight, per the user's "save the recording and exit to KSC" answer).
- Recording-in-flight does NOT disable the button: the flight flow commits/saves the
  recording as part of the exit. (This differs from the existing R/FF buttons, which
  disable while recording. The new control's whole premise is "save & exit, then warp".)

### Career-start snapshot (true Year 1 / Day 1 reset)

A one-time pristine quicksave (`parsek_career_start`, stored beside the rewind saves under
`saves/<save>/Parsek/Saves/`) is captured the first time a brand-new career reaches the
Space Center (`CareerStartSnapshot`; capture decision `ShouldCapture` = no snapshot yet AND
zero ERS recordings AND clock within the first day). The deferred capture is hosted in
`WarpToTimeConsumer.OnLevelWasLoaded(SPACECENTER)`. Saves created before this feature never
get one and keep the earliest-launch fallback below.

When the warp target precedes the first launch, `ResolveRewindTarget` treats the snapshot as
a virtual launch at UT 0, so `SelectRewindTargetIndex` picks it as the nearest-prior target:
the reload (`RecordingStore.InitiateRewindToCareerStart`) reloads the pristine snapshot
(true UT 0, initial resources/facilities), then the deferred consumer fast-forwards to the
exact target. It reuses the rewind machinery (`RewindContext` + `HandleRewindOnLoad`), so the
in-memory recordings are KEPT as future ghosts (consistent with Rewind-to-Launch) and the
ledger recalc at UT 0 restores pristine resources. It skips `PreProcessRewindSave` (the
snapshot is pristine, no strip and no sub-zero windback) and uses no owner/baseline.

**Supersede guard:** the career-start snapshot is only offered when there are NO re-fly
supersede relations. A UT-0 reset has no owner to scope the supersede drop, and leaving
supersedes in place would hide superseded originals. When supersedes exist, the resolver
falls back to the earliest-launch rewind (whose owner-keyed supersede drop is the tested
path). `InitiateRewindToCareerStart` also refuses defensively if supersedes are present.

### Reaching the start of the game when no career-start snapshot exists (earliest-launch fallback)

The user requirement: entering 1/1/0/0 (= UT 0) must take you back to "the start of the
game", with everything reset. There is normally no rewind save AT UT 0 - the earliest
Parsek rewind save is captured at the player's FIRST launch, which sits a little after game
start. So a strict "nearest launch with `StartUT <= target`" rule would mark UT 0
unreachable, which is wrong.

Resolution: when the target precedes every launch, fall back to rewinding to the EARLIEST
launch's save. Loading that save strips all later vessels and winds back to the pad before
the first launch - i.e. "everything at the start of the timeline". That pre-first-launch
state is the earliest world state Parsek can restore; it is what "start of the game" means
in practice. The landed UT is the earliest launch's adjusted UT (its `StartUT` minus the
rewind lead time), which may be slightly after UT 0, not exactly UT 0. The consumer's
forward-jump guard naturally skips the forward jump in this case (target < landed UT), so
the player simply lands at the earliest reachable point. (True exact UT 0 is only reachable
if a launch save exists at UT 0; Parsek does not snapshot game creation. Note this
limitation in the dialog/log.)

Rewind-target resolution (`WarpToTimeController`, ERS-routed):

```csharp
// Among ERS-visible recordings that own a usable rewind save:
//   1. Prefer the owner with the GREATEST StartUT <= targetUT (nearest prior launch).
//   2. If none qualifies (target precedes all launches), fall back to the owner with
//      the SMALLEST StartUT (earliest launch = start of the timeline).
//   3. Return null only if no recording owns a usable rewind save at all.
// `landsAtTimelineStart` is set true for case 2 (drives dialog copy + skips the FF).
internal static Recording ResolveRewindTargetLaunch(double targetUT, out bool landsAtTimelineStart);
```

- Enumerate `EffectiveState.ComputeERS()` (NOT raw `CommittedRecordings`) to satisfy the
  ERS/ELS grep gate.
- A recording qualifies when `RecordingStore.GetRewindSaveFileName(rec)` is non-empty AND
  the resolved rewind save file exists. Resolve each to its owner via
  `RecordingStore.GetRewindRecording(rec)` (branch recordings rewind through the tree
  root); dedupe owners.
- `landsAtTimelineStart = (resolvedOwner.StartUT > targetUT)` (we are rewinding to a launch
  that is itself after the target, because nothing earlier exists).
- Reuse the same can-rewind preconditions as the R button
  (`RecordingStore.CanRewind(owner, out reason, isRecording:false)` after the recording
  has been committed) so we never start an impossible rewind.
- Must NOT read raw `RecordingStore.CommittedRecordings` or `Ledger.Actions` directly
  (CI grep gate). ERS enumeration only.

## Execution flow (`WarpToTimeController`)

Entry point: `WarpToTimeController.RequestWarp(int year, int day, int hour, int minute)`
called from the button. It computes `targetUT`, reads `currentUT`, `inFlight`,
`isRecording`, resolves `hasPriorLaunchSave`, calls `DecideWarpPlan`, then shows a
confirmation dialog (same `PopupDialog`/`MultiOptionDialog` style as
`ShowRewindConfirmation` / `ShowFastForwardConfirmation`).

### Confirmation dialog copy (adapts to plan)

- ForwardOnly, KSC: "Fast-forward to {date}? Time will advance by {N} seconds."
- ForwardOnly, flight: "Save your recording and return to the Space Center, then
  fast-forward to {date}?"
- RewindThenForward, KSC: "Rewind to '{launchVessel}' launch at {launchDate}, then
  fast-forward to {date}? Any uncommitted progress will be lost."
- RewindThenForward, flight: "Save your recording and return to the Space Center, rewind
  to '{launchVessel}' launch at {launchDate}, then fast-forward to {date}?"
- RewindThenForward + `landsAtTimelineStart` (target precedes all launches, e.g. 1/1/0/0):
  drop the "fast-forward to {date}" clause; "Rewind to the earliest launch '{launchVessel}'
  at {launchDate} (the start of your timeline)? Any uncommitted progress will be lost."
  Flight variant prefixes "Save your recording and return to the Space Center, ".
- AtTarget / Unreachable: button disabled; no dialog (tooltip explains).

Confirm button label "Warp"; Cancel logs and closes.

### On confirm

The execution always ends with the final forward jump running from a stable Space Center
state. The deferred consumer is ALWAYS just a forward jump (it never re-dispatches into a
rewind), because any required rewind is started in-line before the scene load. Flight is
handled by an explicit full commit (`CommitTreeFlight()`), NOT by relying on scene-exit
auto-commit (which only fully commits when auto-merge is ON; with auto-merge OFF a scene
exit STASHES a pending tree (`ParsekFlight.cs:2440`) and `CanRewind` then refuses the
rewind on `HasPendingTree` (`RecordingStore.cs:6079-6084`) -> deadlock). `CommitTreeFlight()`
self-guards on `activeTree == null` (`ParsekFlight.cs:12063`) so it is safe to call
unconditionally; it finalizes (`isSceneExit:false`), commits the tree, clears `activeTree`
and `recorder`, so afterwards `IsRecording` is false and no pending tree exists.

Four concrete paths:

1. **KSC + ForwardOnly** -> immediate `TimeJumpManager.ExecuteForwardJump(targetUT)`.
   No scene change, no deferral. (Same call the existing KSC FF branch uses.)

2. **KSC + RewindThenForward** -> `WarpToTimeRequest.Set(targetUT)`, then
   `RecordingStore.InitiateRewind(owner)`. The rewind reloads the launch save into the
   Space Center; the deferred consumer (below) runs the forward jump to `targetUT` once
   the rewind's UT adjustment settles.

3. **Flight + ForwardOnly** -> `if (flight active) flight.CommitTreeFlight()` (full save),
   `WarpToTimeRequest.Set(targetUT)`, then `HighLogic.LoadScene(GameScenes.SPACECENTER)`.
   The deferred consumer runs the forward jump on Space Center arrival. (Because the tree
   was already committed and `activeTree` cleared, the scene-exit handler
   `OnSceneChangeRequested` no-ops on the now-null tree, so no double-commit / no stash.)

4. **Flight + RewindThenForward** -> `if (flight active) flight.CommitTreeFlight()` (full
   save) in-frame, `WarpToTimeRequest.Set(targetUT)`, then `RecordingStore.InitiateRewind(owner)`
   deferred by one frame (see below). `InitiateRewind` does `LoadGame` + `LoadScene(SPACECENTER)`
   and is an already-supported from-flight path once not recording. The deferred consumer
   runs the forward jump on Space Center arrival.

This commit-in-place model (chosen over the earlier exit-to-KSC-then-rewind idea) avoids
the auto-merge-off pending-tree deadlock and keeps the consumer to a single
responsibility.

Two implementation requirements confirmed by review:

- **Re-resolve the rewind owner by id AFTER the commit.** `RequestWarp` resolves the owner
  at confirm time, but `CommitTreeFlight()` mutates committed state. For a
  rewind-to-an-already-committed-launch the owner is stable, but to be safe re-look-up the
  owner by `RecordingId` (via `RecordingStore.GetRewindRecording` / ERS) immediately before
  calling `InitiateRewind`, and abort with a logged reason if it no longer resolves.
- **Defer the post-commit `InitiateRewind` (path 4) and the post-commit `LoadScene` (path 3)
  by one frame** via `WarpToTimeConsumer`'s coroutine host (`StartCoroutine` -> `yield
  return null` -> rewind/scene-load). Same-frame is correctness-safe (`CommitTreeFlight` and
  its `SpawnTreeLeaves` are fully synchronous; the supersede-drop in `InitiateRewind`
  correctly treats the just-committed launch as the rewound-TO owner, not a rewound-out
  fork). The one-frame defer is purely to avoid `SpawnTreeLeaves` materializing real leaf
  vessels into a flight scene that the reload is about to discard, and to let the
  post-commit ledger recalc settle. Cost negligible.

### Pending-warp request + deferred consumer

New session-scoped static holder (NOT serialized - a plain rewind/scene-exit completes
within one process session, like `RewindContext`). It stamps the originating process
session so a later quickload into the Space Center cannot fire a stale jump (mirrors the
staleness guard in `StockActionIntentMarker`):

```csharp
internal static class WarpToTimeRequest
{
    internal static bool HasPending { get; private set; }
    internal static double TargetUT { get; private set; }
    internal static Guid SessionId { get; private set; }   // ParsekProcess.ProcessSessionId at Set time
    internal static void Set(double targetUT);             // stamps SessionId, logs
    internal static void Clear();                          // logs
    internal static bool IsStale();                        // SessionId != ParsekProcess.ProcessSessionId
    internal static void ResetForTesting();
}
```

Consumer: a one-shot coroutine started when the Space Center scene loads with a fresh
pending request. Hook on `GameEvents.onLevelWasLoaded` (fires reliably on every scene load,
including both a save-reload rewind and a plain flight->SpaceCenter change), registered
from a dedicated process-lifetime addon (see Persistent host). The coroutine:

```
OnLevelWasLoaded(scene):
  if scene != SPACECENTER: return
  if !WarpToTimeRequest.HasPending: return
  if WarpToTimeRequest.IsStale(): WarpToTimeRequest.Clear(); log; return
  host.StartCoroutine(ConsumePendingWarp())

ConsumePendingWarp():
  yield return null                                  // let new-scene singletons spin up
  int guard = 300                                    // ~5s @60fps
  while (RecordingStore.RewindUTAdjustmentPending && guard-- > 0)
      yield return null                              // sequence AFTER any rewind UT set
  double target = WarpToTimeRequest.TargetUT
  WarpToTimeRequest.Clear()
  double now = Planetarium.GetUniversalTime()
  if (now < target - AtTargetEpsilonSeconds)  TimeJumpManager.ExecuteForwardJump(target)
  else log "warp consume: already at/after target (now={now} target={target})"
```

Why this is race-safe and uses `RewindUTAdjustmentPending`:
- `onLevelWasLoaded` fires AFTER the Space Center `ScenarioModule.OnLoad` has run, so by
  the time the consumer starts, `HandleRewindOnLoad` (`ParsekScenario.cs:2945`) has already
  set `RewindUTAdjustmentPending = true` and started `ApplyRewindResourceAdjustment`
  (`ParsekScenario.cs:3045-3046`). That is the PLAIN Rewind-to-Launch path that
  `InitiateRewind` triggers (it lands in SPACECENTER), distinct from the Re-Fly
  `RewindInvoker` path which expects FLIGHT - the warp feature only ever uses
  `InitiateRewind`, so the consumer correctly waits in SPACECENTER.
- `ApplyRewindResourceAdjustment` sets `Planetarium.SetUniversalTime(adjustedUT)` then
  clears `RewindUTAdjustmentPending` (`ParsekScenario.cs:6073-6074`). Waiting on the flag
  guarantees the forward jump starts from the post-rewind UT (launch lead-time point), not
  a stale pre-rewind UT. (`ParsekKSC.cs:326` independently early-returns while this flag is
  set, corroborating it as the canonical "KSC not yet settled post-rewind" signal.)
- For the non-rewind flight->KSC forward case (path 3) the flag is already false, so the
  consumer forward-jumps immediately after one frame.

This single consumer covers paths 2, 3, and 4. Only path 1 (KSC forward) is immediate and
bypasses the consumer.

### Persistent host (consumer addon)

New `WarpToTimeConsumer.cs`: a dedicated `[KSPAddon(KSPAddon.Startup.Instantly, true)]`
MonoBehaviour mirroring `TestRunnerShortcut` (process-lifetime, survives every scene
change, alive in the Space Center scene). It is the host for both the `onLevelWasLoaded`
subscription and the `ConsumePendingWarp` coroutine:

- `Awake()`: once-guard (static bool, like `ParsekHarmony.initialized`),
  `DontDestroyOnLoad(gameObject)`, `GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded)`.
- `OnDestroy()`: `GameEvents.onLevelWasLoaded.Remove(...)`.

Do NOT host on `ParsekScenario` (ScenarioModule, per-scene), `ParsekFlight`
(`[KSPAddon(Flight)]`), or `ParsekKSC` (`[KSPAddon(SpaceCentre)]`) - those are destroyed
across the flight->KSC transition and a coroutine started there would die mid-warp.

## Edge cases

- **Target before the earliest launch** (incl. 1/1/0/0 game start) -> rewind to the
  EARLIEST launch save (`landsAtTimelineStart`), landing at the start of the timeline; no
  forward jump follows. See "Reaching the start of the game".
- **First launch within the rewind lead time (StartUT < 15s).** The rewind winds UT back
  by `RewindToLaunchLeadTimeSeconds = 15` (`RecordingStore.cs:149`), so landing at the
  earliest launch can produce a slightly negative `flightState.universalTime` when the
  first launch happened in the first 15s of the game. This is identical to existing
  R-button-to-a-sub-15s-launch behavior (pre-existing, not introduced here), and the
  follow-up forward jump to a >= 0 target is a valid forward jump. Out of scope to change
  the shared rewind lead-time math; the consumer only ever forward-jumps to the user's
  entered (>= 0) UT, never to a negative UT.
- **No usable rewind save at all** (no recordings, or none own a rewind save) -> `Unreachable`,
  button disabled with tooltip "No launch save to rewind to". (A brand-new game sitting at
  ~UT 0 with no launches resolves to `AtTarget` for a 1/1/0/0 target, so the disabled state
  only appears when you are past UT 0 with no rewind saves.)
- **Target == now (within 1s)** -> `AtTarget`, no-op.
- **Target far future** -> single forward jump; `ExecuteForwardJump` already handles large
  deltas (rails + converter-timestamp fix).
- **Sandbox / science mode** -> no resource singletons; `ExecuteForwardJump` and the
  rewind coroutine both already handle null Funding/R&D/Reputation gracefully.
- **Active re-fly merge journal** -> `InitiateRewind` already refuses
  (`RecordingStore.cs:6209-6219`); the RewindThenForward path inherits that guard. Surface
  the same "finish the merge first" message; leave pending-warp unset so no stale jump.
- **Pending tree / HasPendingTree** -> `CanRewind` already blocks; reflect in tooltip.
- **Garbage / negative input** -> `TryParseField` rejects (keeps prior committed value);
  Year/Day floor at 1.
- **Window closed mid-warp** -> the pending request lives in the static, not the window;
  closing the window does not cancel an in-flight warp.

## Files touched

- `UI/TimelineWindowUI.cs` - footer right-align; new warp row; input-box state + commit
  helpers; first-open load / on-close save; button -> `WarpToTimeController.RequestWarp`.
- `WarpToTimeMath.cs` (new) - `ComputeTargetUT`, `TryParseField`, `DecideWarpPlan`,
  `WarpPlan`/`WarpPlanKind`/`WarpFieldKind` types. Pure, fully unit-tested.
- `WarpToTimeController.cs` (new) - `RequestWarp`, `ResolveNearestPriorLaunch`,
  confirmation dialog, on-confirm dispatch (paths 1-4), pending-warp set.
- `WarpToTimeRequest.cs` (new) - session-scoped pending-warp holder with session-id stamp.
- `WarpToTimeConsumer.cs` (new) - process-lifetime `[KSPAddon(Instantly,true)]` addon
  hosting the `onLevelWasLoaded` subscription + `ConsumePendingWarp` coroutine + the
  one-time career-start snapshot capture.
- `CareerStartSnapshot.cs` (new) - career-start quicksave capture / existence / pure
  `ShouldCapture` decision.
- `RecordingStore.InitiateRewindToCareerStart` (new) - no-owner UT-0 reset reload.
- `ParsekSettingsPersistence.cs` - four stored ints + `RecordWarpDate` + getters + test
  hooks.
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md` - feature entry.
- Tests (below).

No serialized scenario fields are added, so `ParsekScenario` OnSave/OnLoad is unchanged
(the persisted input values live in settings.cfg; the pending-warp is session-only).

## Logging

Per project requirements, log every decision and transition (subsystem tag "WarpTime"):

- Field commit: `field=Year value=5 (was 1)`.
- `RequestWarp`: input Y/D/H/M, computed targetUT, currentUT, inFlight, isRecording,
  plan kind, requiresFlightExit, resolved launch (id/name/StartUT) or unreachable reason.
- Dialog confirm/cancel.
- `WarpToTimeRequest.Set/Clear`.
- Consumer: scene-load detection, wait-loop exit (frames waited / guard-expired), final
  jump (now -> target, delta) or skip reason.
- All numeric formatting via `CultureInfo.InvariantCulture`.

## Tests

xUnit (`Source/Parsek.Tests/`), pure logic:

- `WarpToTimeMath.ComputeTargetUT`: Year1/Day1/0/0 -> 0; arbitrary Y/D/H/M; Kerbin vs
  Earth via `ParsekTimeFormat` Kerbin-time test override; 0/blank floors to Y1/D1;
  hour/minute overflow rolls over correctly.
- `TryParseField`: valid ints; negatives floored; Year/Day floor at 1; garbage rejected.
- `DecideWarpPlan`: forward / rewind / at-target / unreachable; `RequiresFlightExit`
  reflects inFlight; `LandsAtTimelineStart` set when target precedes all launches; recording
  state does not gate.
- `ResolveRewindTargetLaunch`: synthetic recordings with/without rewind saves; picks the
  greatest StartUT <= target; falls back to the EARLIEST launch when target precedes all
  (asserts `landsAtTimelineStart`), e.g. target = UT 0 with a first launch at UT 300 returns
  the first launch; null only when no usable rewind save exists; routes through ERS.
- Log-capture tests (`ParsekLog.TestSinkForTesting`) asserting `RequestWarp` and consumer
  decision lines are emitted with the expected fields.

In-game (`InGameTests/RuntimeTests.cs`), live KSP:

- A SPACECENTER (or FLIGHT) forward-jump test: set a known UT, call the forward path with
  `target = now + delta`, assert `Planetarium.GetUniversalTime()` advanced to ~target.
  (The full rewind->KSC->forward round trip crosses a scene reload and is not expressible
  in a single in-game test; the math + decision + nearest-launch resolution are covered by
  xUnit, and the forward jump is covered live here.)

## Implementation phasing (gate reviews at the marked points)

1. **Layout + inputs (no warp behavior):** footer right-align, warp row, four committed
   int fields with commit-on-Enter/focus-loss, persistence load/save. Button present but
   wired only to `ComputeTargetUT` + a log line (no jump). -> review checkpoint.
2. **Forward + KSC paths:** `WarpToTimeMath` decision, `WarpToTimeController.RequestWarp`,
   confirmation dialogs, path 1 (KSC forward) and path 2 (KSC rewind-then-forward) +
   pending-warp + consumer. -> review checkpoint.
3. **Flight paths:** path 3 / 4a (save + exit to KSC, then warp). -> review checkpoint.
4. **Tests + docs.**

## Risks / review focus

1. **(RESOLVED) Flight commit vs auto-merge.** Relying on scene-exit to commit only works
   with auto-merge ON; auto-merge OFF stashes a pending tree and blocks the rewind. Fixed
   by an explicit `CommitTreeFlight()` (full commit) before the scene load in both flight
   paths. Remaining item: confirm `CommitTreeFlight()` -> `InitiateRewind` in the same
   frame is ordering-safe; if not, defer the rewind/scene-load by one frame.
2. **`onLevelWasLoaded` host lifetime (PINNED).** Hosted on the new process-lifetime
   `WarpToTimeConsumer` addon (`[KSPAddon(Instantly,true)]` + `DontDestroyOnLoad`), once-
   guarded subscription, `OnDestroy` unsubscribe. Not on any scene-scoped component.
3. **Double consumption / stale pending.** Button disabled while `WarpToTimeRequest
   .HasPending`. The request carries a process-session-id stamp; the consumer rejects a
   stale request (e.g. a quickload into the Space Center after a restart) and clears it.
   The consumer clears the request exactly once before jumping.
4. **ERS/ELS grep gate.** `ResolveNearestPriorLaunch` reads `ComputeERS()` only (no raw
   `CommittedRecordings` / `Ledger.Actions`); verify the CI grep audit passes.
5. **Rewind preconditions.** RewindThenForward respects the same guards as the R button
   (merge journal, pending tree). Surface the blocking reason instead of starting a
   half-warp; leave the pending-warp request unset when the rewind is refused.
6. **(RESOLVED) Watch mode in flight.** `OnSceneChangeRequested` already calls
   `watchMode.ExitWatchMode()` + removes the control lock (`ParsekFlight.cs:2032-2035`), so
   the flight->KSC transition leaves no dangling watch state.
7. **TimeWarp active.** `ExecuteForwardJump`/`ExecuteJump` stop time warp before jumping
   (`TimeJumpManager.cs:243-247`); the rewind reloads a save. No extra handling needed, but
   confirm no warp-rate desync when the button is pressed during high warp at KSC.
