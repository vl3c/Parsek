# Fix #546 - Post-switch first-modification auto-record coverage

**Branch:** `bug/546-auto-start-switch-gap`  
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-bug-546-auto-start-switch-gap`  
**Related:** `#534` (spawned chain-tip FLIGHT -> FLIGHT restore gap), `#111`, `#155`

## Problem

Parsek currently auto-starts reliably in only two idle-to-live cases:

1. `OnVesselSituationChange` launch transitions out of `PRELAUNCH` / settled `LANDED`
2. `OnCrewOnEva` for pad EVA

The broader design intent is richer than that. `docs/parsek-flight-recorder-design.md` says physical state is the trigger, focus switches are observational rather than claim-worthy on their own, and the focus vessel should be the active recording while other vessels in-session fall back to background/checkpoint coverage. The older chaining design notes also explicitly called out vessel-switch trigger expansion before v1 scope was narrowed.

Today, once the player switches to a vessel and Parsek is idle, there is no generic "first meaningful modification" path that starts or resumes recording before the change is lost. The main misses are:

- orbital or suborbital engine burns that do not cross a situation boundary
- sustained RCS translation / rotation that does not trip existing launch logic
- rover / base motion while the vessel remains `LANDED`
- switched-to vessel part / crew / resource / ground-science changes after the switch
- outsider states where the tree context is gone and later changes never re-arm recording

`#534` is the narrow restore bug inside the existing tracked-tree return path. `#546` is the larger feature gap: even when there is no restore failure, Parsek still lacks a general post-switch arming and trigger system.

## Invariant to enforce

After the player switches focus to a real vessel and then performs the first meaningful physical change, Parsek must start or resume recording before that change is lost.

For this fix, "meaningful physical change" should follow the design doc boundary:

- include motion / orbit changes, engine / RCS activity, non-cosmetic part-state changes, crew changes, resource changes, and structural changes
- exclude observation-only actions and pure cosmetic toggles such as opening windows or moving the camera
- do not start recording merely because focus changed; the switch only arms the detector

This plan intentionally maps the design doc's ghosting taxonomy (Section 12.6 / 12.9.4) onto **session-start policy**: focus switch alone does not claim a vessel, but the first later physical modification does. One deliberate divergence must be called out explicitly for reviewers: ghosting is classified at the authored-event level, while the post-switch watcher may use debounce / settle thresholds to suppress switch-settle noise, SAS micro-corrections, and one-frame physics jitter before deciding that a new session should start.

## Missing systems to add

1. **Post-switch arming state**
   Parsek needs an explicit "armed after vessel switch" state while idle. Today `OnVesselSwitchComplete` can promote a tracked background member (`ParsekFlight.cs:1627`, `:1908`), but if there is no promotable active tree it simply leaves the session idle.

2. **First-modification trigger detection**
   There is no lightweight detector for "meaningful change happened after the switch" when `recorder == null`. The recorder already knows how to observe many of these changes once live, but nothing watches for them before recording starts.

3. **Resume-vs-new-recording decision**
   When the first trigger fires, Parsek needs one place to decide whether to:
   - promote a tracked background recording,
   - restore-and-promote a pending tree path fixed by `#534`, or
   - start a fresh recording on the switched vessel.

4. **Coverage and operator-facing docs**
   Current runtime coverage only proves launch and pad-EVA auto-record (`RuntimeTests.cs:775-861`), while the outsider-state runtime canary explicitly stops at ConfigNode/dispatch coverage (`ExtendedRuntimeTests.cs:832-880`).

## Key file / line map

- `docs/parsek-flight-recorder-design.md:57` - physical state is the trigger
- `docs/parsek-flight-recorder-design.md:1480-1483` - focus vessel active recording, focus switches logged
- `docs/dev/done/recording-chaining.md:172-183` - trigger expansion note for boarding / vessel switch / docking
- `Source/Parsek/ParsekFlight.cs:1627` - `OnVesselSwitchComplete`
- `Source/Parsek/ParsekFlight.cs:1908` - `PromoteRecordingFromBackground`
- `Source/Parsek/ParsekFlight.cs:3861` - `OnVesselSituationChange`
- `Source/Parsek/ParsekFlight.cs:3941` - `OnCrewOnEva`
- `Source/Parsek/ParsekFlight.cs:3999` - `EvaluateAutoRecordLaunchDecision`
- `Source/Parsek/ParsekFlight.cs:4944` - `HandleMissedVesselSwitchRecovery`
- `Source/Parsek/ParsekFlight.cs:6297` - `RestoreActiveTreeFromPendingForVesselSwitch`
- `Source/Parsek/ParsekFlight.cs:6537` - `ShouldRecoverMissedVesselSwitch`
- `Source/Parsek/ParsekFlight.cs:6582` - `ApplyPreTransitionForVesselSwitch`
- `Source/Parsek/ParsekFlight.cs:6618` - `StashActiveTreeForVesselSwitch`
- `Source/Parsek/FlightRecorder.cs:6319` - `DecideOnVesselSwitch`
- `Source/Parsek/ParsekSettings.cs:34-38` - only launch / EVA auto-record settings exist
- `Source/Parsek/UI/SettingsWindowUI.cs:266-274` - UI only exposes launch / EVA toggles
- `Source/Parsek/Patches/PhysicsFramePatch.cs` - existing active-vessel physics-frame hook
- `Source/Parsek.Tests/AutoRecordDecisionTests.cs` - current pure auto-record decision tests
- `Source/Parsek.Tests/MissedVesselSwitchRecoveryTests.cs` - current restore-safety-net tests
- `Source/Parsek.Tests/VesselSwitchTreeTests.cs` - current tree switch decision tests
- `docs/dev/manual-testing/test-auto-record.md` - manual checklist only covers launch / EVA paths

## Recommended implementation

### 1. Boundary with `#534`

Do not mix the restore bug with the broader trigger feature. `#534` should keep ownership of:

- FLIGHT -> FLIGHT stash / restore correctness
- `vesselSwitchPending` freshness and dispatch
- outsider return to an already-tracked tree member

`#546` should build on that seam, not bury it inside a larger behavior branch.

### 2. Add an explicit armed-after-switch state in `ParsekFlight`

Add a small state object owned by `ParsekFlight`, for example:

- `armedVesselPid`
- `armedAtUt`
- `armedFromTrackedTree`
- `armedReason` / diagnostic trigger text
- baseline motion / orbit values
- baseline digests for crew, resources, and selected part-state surfaces

Arm it from these paths:

- `OnVesselSwitchComplete` when the new active vessel is real, no recorder is active, and the immediate promotion path did not already start recording
- `RestoreActiveTreeFromPendingForVesselSwitch` when the restore completes in outsider state instead of promoting
- any future explicit outsider recovery path that intentionally leaves Parsek idle on the new active vessel

Disarm it when:

- recording starts by any path
- the player switches again before a trigger fires
- scene change / revert / merge / discard tears down the current flight context
- transient states such as pending split / dock merge make trigger evaluation unsafe
- time warp / on-rails phases mean the vessel is no longer in a valid "first live modification" window

Baseline capture needs an explicit rule so the watcher does not self-trigger on the switch seam:

- arm immediately on the switch / restore seam, but do **not** compare digests yet
- capture the baseline on the first stable physics frame where the armed PID is still active, the vessel is unpacked, and no transient guard is active
- for landed vessels, require a short settle window after that baseline frame before motion/resource/part-state comparisons are allowed
- keep the digest surface intentionally narrow: crew manifest, resource manifest, and non-cosmetic module states that already imply authored physical change (for example gear, cargo bay, ladder / deployable, robotic motion). Exclude lights and other purely cosmetic toggles from the idle-to-live trigger surface even if the live recorder can serialize them later

### 3. Add a lightweight first-modification detector instead of starting on every switch

The new behavior should be "arm on switch, trigger on first meaningful change", not "start immediately on switch".

Recommended split:

- **Physics-frame watcher** for motion / orbit / engine / RCS triggers.
  Reuse the existing `PhysicsFramePatch` shape so the active vessel can be checked at physics cadence only while armed. This avoids missing short burns and keeps the watch dormant when not needed.
- **Event-based triggers** for discrete changes already surfaced by GameEvents or existing handlers, such as dock / undock / ground science deploy-remove / flag placement / crew transition.
- **Digest comparison** for non-cosmetic part-state or manifest changes that do not already arrive through a dedicated event while idle.

The detector should intentionally ignore pure cosmetic toggles in v1 of this fix. The recorder may continue to serialize lights and similar details once recording is active, but those should not be the thing that causes recording to start.

Suppression classes should be spelled out rather than hidden under "transient guards":

- pending split / dock-merge / boarding-transition paths
- restore coroutine in progress
- regular time warp or physics warp
- packed / on-rails vessels
- active-vessel mismatch after the switch
- first-frame switch settling before the baseline has been captured

### 4. Centralize the first-trigger start decision

Add one helper that handles "the watcher fired; what now?" Suggested shape:

```csharp
internal enum PostSwitchAutoRecordStartDecision
{
    None,
    PromoteTrackedRecording,
    RestoreAndPromoteTrackedRecording,
    StartFreshRecording,
}
```

The decision helper should take only pure inputs where possible:

- armed vessel PID
- current active vessel PID / state
- whether an active tree exists
- whether the active PID is in `BackgroundMap`
- whether a pending tree from a vessel-switch restore path can be reinstated
- whether transient guards make the action unsafe this frame

Decision order:

1. If the active PID is already tracked in the live tree, promote.
2. Else if the `#534` restore path can reinstate a pending tracked tree, restore and promote.
3. Else start a fresh recording on the switched vessel with a specific start reason such as `post-switch engine start`, `post-switch motion`, or `post-switch crew/resource change`.

This should not overload `EvaluateAutoRecordLaunchDecision`, which is still specifically about launch transitions.

Reachability note: in the steady-state happy path, `PromoteTrackedRecording` is usually consumed earlier by `OnVesselSwitchComplete` / `PromoteRecordingFromBackground`. The watcher mainly needs to cover:

- `RestoreAndPromoteTrackedRecording` when the `#534` seam has to reinstate a pending tracked tree
- `StartFreshRecording` for true outsider vessels
- the narrow "promotion was intentionally suppressed during the immediate switch seam" cases such as pending dock/board handling

When the decision lands on `StartFreshRecording`, that is the point where the outsider vessel becomes claimable on future rewind. This remains consistent with Section 12.9.4 because focus alone still does not claim the vessel; the first physical modification does.

### 5. Reuse existing recorder capabilities once recording is live

Do not duplicate the recorder's full state machine. The watcher only needs to detect the first trigger. After start:

- `FlightRecorder.StartRecording(...)` should seed the normal module / manifest caches
- existing part-event polling in `FlightRecorder.OnPhysicsFrame(...)` should take over
- existing tree / background logic remains responsible for later vessel switches

The plan goal is to add the missing idle-to-live seam, not invent a second recorder.

### 6. Add an explicit setting instead of overloading launch/EVA

Add a new setting in `ParsekSettings` and `SettingsWindowUI`, for example:

- `Auto-record on first modification after switch`

Do not hide this behind `autoRecordOnLaunch`; the semantics are different and the behavior is materially broader. Recommended default: `true`, because the current gap loses mission history and the design docs already lean toward continuity. If rollout caution is preferred during implementation, flipping the default is a one-line follow-up.

Upgrade behavior needs to be explicit: for existing saves/settings blobs that do not yet have the new field, seed it from the user's current launch auto-record preference instead of blindly opting every legacy install into the broader behavior. Fresh saves can still default to `true`.

### 7. Logging requirements

Add logs at these seams:

- arm / disarm with PID, vessel name, tree state, and why the watcher armed
- each suppressed trigger class with one-line reason when ignored (cosmetic-only, transient guard, wrong vessel, duplicate)
- each accepted trigger with the chosen start decision
- whether the trigger promoted an existing tree member or started a fresh recording

Use rate-limited logging for per-frame armed-state checks.
Specifically use `ParsekLog.VerboseRateLimited` with a shared armed-state key rather than raw `Verbose` spam.

## Concrete test plan

### Headless xUnit

Add a focused pure-helper test file, for example `Source/Parsek.Tests/PostSwitchAutoRecordTests.cs`, covering:

- arming only when the switch leaves Parsek idle
- disarming on start / switch-away / scene-change guards
- trigger classification for:
  - landed motion above threshold
  - sustained engine or RCS activity
  - orbit / velocity change while the vessel stays in the same situation
  - crew / resource / non-cosmetic part-state digests changing
  - cosmetic-only changes being ignored
- first-trigger decision routing:
  - tracked background member -> promote (only for the narrow suppressed-promotion seam)
  - pending tracked restore available -> restore and promote
  - outsider / not tracked -> fresh recording

Extend existing tests where they already own the seam:

- `AutoRecordDecisionTests.cs` for any new pure gating helper
- `VesselSwitchTreeTests.cs` for promotion-vs-fresh decision edges
- `MissedVesselSwitchRecoveryTests.cs` only if the `#546` wiring changes recovery preconditions

### In-game runtime

Add isolated runtime canaries in `Source/Parsek/InGameTests/RuntimeTests.cs` for:

1. Switch to a landed rover, drive without leaving `LANDED`, verify exactly one auto-start.
2. Switch to an orbital vessel, start engine or sustained RCS, verify exactly one auto-start without a situation change.
3. Switch to a landed vessel with deployable gear, toggle the gear once, verify exactly one auto-start. Skip if the isolated test craft lacks a deployable gear module.
4. Switch to a vessel and do nothing, verify no auto-start.

Do not fold the `#534` restore scenario into the same runtime tests. That bug should keep its own acceptance coverage.

### Manual checklist

Update `docs/dev/manual-testing/test-auto-record.md` with:

- switched-vessel landed-motion case
- switched-vessel orbital-burn case
- switched-vessel no-op negative case
- setting-enabled / disabled coverage for the new toggle

## Docs and release notes

When the implementation lands:

- mark `#546` done in `docs/dev/todo-and-known-bugs.md`
- keep `#534` separate unless that branch also lands and closes it independently
- add a `0.8.3` changelog line describing the new post-switch auto-record coverage
- update the manual auto-record checklist

During implementation, keep `docs/dev/todo-and-known-bugs.md` and the changelog in sync **per commit**, not only at final close-out. If the implementation narrows to outsider-only first because `#534` is still open, the docs must say exactly that on the intermediate commit.

## Out of scope

- full always-on focus-switch recording with no arming window
- background recording for every nearby vessel outside the existing tree model
- broad design-doc completion for every historical trigger-expansion idea (docking, undocking, crew transfer, etc.) unless they naturally fall out of the armed trigger surface
- repair of `#534` itself inside this branch

## Main risks

- **False positives from noise**: SAS blips, wheel jitter, or tiny landed drift could start recordings unexpectedly. Mitigate with thresholds and debounce.
- **Cosmetic scope creep**: the recorder can serialize some cosmetic events once live; that does not mean they should be idle-to-live triggers.
- **Double-start races**: the armed watcher could fire in the same window as a restore / promotion / split transition. Centralize guards and log the chosen winner.
- **Performance**: avoid a permanent idle poller. The watcher should only attach when a post-switch arm is active.

## Suggested implementation order

1. Merge or at least stabilize the narrow `#534` restore fix. If that slips, gate out `RestoreAndPromoteTrackedRecording` and land the outsider-only `StartFreshRecording` path first.
2. Add the armed-after-switch state, baseline-capture timing, and disarm rules.
3. Wire the physics-frame watcher for motion / engine / RCS.
4. Add event / digest triggers for crew, resource, and non-cosmetic part changes.
5. Add the explicit setting, upgrade defaulting, and UI text.
6. Add xUnit coverage.
7. Add runtime canaries and update the manual checklist.
