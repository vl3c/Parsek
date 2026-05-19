# Force-Absolute for Re-Fly Provisional Recordings (experiment)

## Motivation

Re-fly fork (provisional) recordings currently author `ReferenceFrame.Relative`
`TrackSection`s with `anchorRecordingId` pinned to the superseded origin
recording (`Source/Parsek/ReFlyAnchorSelection.cs:96-101`,
`Source/Parsek/FlightRecorder.cs:5689-5697`,
`Source/Parsek/BackgroundRecorder.cs:4866-4875`). This inheritance comes
from the general 2300m physics-bubble rule in `AnchorDetector` — every
recording inside the bubble of an eligible anchor opens Relative — and
was not chosen deliberately for re-fly. `ReFlyAnchorSelection` (PR #889)
is a 269-LoC patch on top, pinning the anchor to the supersede target so
the nearest-search does not pick a fast-separating sibling and produce
178m playback teleports
(`docs/dev/todo-and-known-bugs.md:280`).

The sub-meter precision advantage of Relative sections requires a **live**
anchor (real vessel, just sampled — see
`docs/parsek-ghost-trajectory-rendering-design.md:55-95`). The re-fly
fork's anchor is itself a ghost being resolved from recorded data via
Slerp (`Source/Parsek/RecordedRelativeAnchorPoseResolver.cs`); the
float-noise common-mode cancellation does not apply. The deferred plan
`docs/dev/plans/refly-postmerge-relative-to-absolute.md:42-89` already
documents this:

> RELATIVE is the right contract when both vessels in the relationship
> are ghosts replaying together... ABSOLUTE is the right contract when
> one vessel is real (live) and the other is a ghost — the re-fly
> scenario being the canonical case.

This experiment lets us **measure the actual visual delta** before
committing to a contract flip. A new opt-in setting,
`forceAbsoluteForReFlyProvisional`, causes the recorder to skip Relative
authoring entirely while a re-fly provisional is the active recording —
keeping the recording in `ReferenceFrame.Absolute` so its `Points` and
`TrackSection.frames` carry body-fixed lat/lon/alt + `srfRelRotation`,
the same contract the debris path consumes via
`InterpolateAndPosition`.

The setting is **off by default**. Flipping it ON authors all subsequent
re-fly provisional sections as Absolute; existing Relative sections on
disk are untouched. Playback for both modes runs through the existing
dispatch — no rendering changes.

## Goals

1. Add a settings toggle (`forceAbsoluteForReFlyProvisional`,
   default `false`) following the `useCoBubbleBlend` pattern at
   `Source/Parsek/ParsekSettings.cs:193-208`.
2. Gate the re-fly anchor bypass at both recorder sites
   (`FlightRecorder.UpdateAnchorDetection` and the BG-side
   `UpdateBackgroundAnchorDetection`) so that when the setting is on
   **and** the active recording is a re-fly provisional, the recorder:
   - Skips the `ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor`
     bypass (no anchor pinned).
   - Skips the fallback nearest-search anchor scan entirely (a re-fly
     provisional in-bubble with the superseded origin would otherwise
     re-trigger the original 178m teleport bug via nearest-search).
   - Force-exits Relative mode if the recorder is currently in it
     (e.g. setting flipped mid-recording).
3. Surface the gate firings in the log so playtests can confirm the
   experiment ran.
4. Leave the playback path completely untouched. Relative sections
   recorded under the old setting still play back via the existing
   resolver; Absolute sections recorded under the new setting play back
   via the existing Absolute path.

## Non-goals

- **No deletion** of `ReFlyAnchorSelection.cs`, the Relative resolver
  (`RelativeAnchorResolver.cs` / `RecordedRelativeAnchorPoseResolver.cs`),
  or any tests. Those remain load-bearing for docking, loop logistics,
  and parent-anchored debris.
- No schema bump. `TrackSection.anchorRecordingId` is already nullable
  and Absolute sections legitimately leave it null today.
- No rendering changes. The engine doesn't know which mode authored the
  data; it just reads what's there.
- No post-merge contract flip (the `refly-postmerge-relative-to-absolute`
  plan is separate, larger scope).
- No co-bubble interaction. Co-bubble is already default off and
  orthogonal to anchor authoring.

## Design

### Setting

Add to `ParsekSettings.cs` (after the `useCoBubbleBlend` block at
line 208):

```csharp
[GameParameters.CustomParameterUI("Force Absolute for re-fly provisional",
    toolTip = "Experimental. When on, re-fly provisional recordings skip Relative-anchored authoring and stay in Absolute mode. Off (default) preserves the current ReFlyAnchorSelection pin-to-supersede-target behavior. Off and on produce different on-disk recordings; flipping mid-recording closes the current section and continues Absolute.")]
public bool forceAbsoluteForReFlyProvisional
{
    get { return _forceAbsoluteForReFlyProvisional; }
    set
    {
        if (_forceAbsoluteForReFlyProvisional == value) return;
        bool prev = _forceAbsoluteForReFlyProvisional;
        _forceAbsoluteForReFlyProvisional = value;
        NotifyForceAbsoluteForReFlyProvisionalChanged(prev, value);
        if (ParsekSettingsPersistence.IsReconciled)
            ParsekSettingsPersistence.RecordForceAbsoluteForReFlyProvisional(value);
    }
}
private bool _forceAbsoluteForReFlyProvisional = false;
```

Plus the `NotifyForceAbsoluteForReFlyProvisionalChanged` log-on-flip
helper (same shape as `NotifyUseCoBubbleBlendChanged`).

Persistence mirror in `ParsekSettingsPersistence.cs` follows the
existing pattern:
- Storage field `storedForceAbsoluteForReFlyProvisional`
- Save/load key `ForceAbsoluteForReFlyProvisional`
- `RecordForceAbsoluteForReFlyProvisional(bool)` method
- Reconcile block in `ApplyTo` (`Source/Parsek/ParsekSettingsPersistence.cs:344-350` shape)
- Two log lines (one in the reconcile diff dump, one in the
  post-reconcile diff dump) — both mirroring the existing
  `useCoBubbleBlend` entries.

### Helper in `ReFlyAnchorSelection`

Add a public predicate so both recorder sites and tests share the same
"is the active recording a re-fly provisional?" check, without each
site rebuilding the marker comparison inline:

```csharp
internal static bool IsActiveRecordingReFlyProvisional(
    RecordingTree activeTree)
{
    var scenario = ParsekScenario.Instance;
    var marker = scenario != null ? scenario.ActiveReFlySessionMarker : null;
    if (marker == null) return false;
    string activeRecordingId = activeTree != null ? activeTree.ActiveRecordingId : null;
    if (string.IsNullOrEmpty(activeRecordingId)) return false;
    return string.Equals(
        marker.ActiveReFlyRecordingId,
        activeRecordingId,
        StringComparison.Ordinal);
}
```

This is a pure boolean derived from already-public marker state. It
does not change `TryResolveReFlyProvisionalAnchor` semantics.

### Recorder gate — FlightRecorder

In `FlightRecorder.UpdateAnchorDetection`
(`Source/Parsek/FlightRecorder.cs:5648`), the existing structure is:

```csharp
if (onSurface && isRelativeMode) { exit relative }
else if (!onSurface) {
    if (ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(...)) {
        ApplyReFlyProvisionalAnchorToActiveRecording(...);
        return;
    }
    nearest-search + maybe-enter-Relative
}
```

Add the gate **before** the bypass at the head of the `!onSurface`
branch:

```csharp
else if (!onSurface)
{
    bool isReFlyProvisional = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(ActiveTree);
    if (ParsekSettings.Current != null
        && ParsekSettings.Current.forceAbsoluteForReFlyProvisional
        && isReFlyProvisional)
    {
        if (isRelativeMode)
        {
            double boundaryUT = Planetarium.GetUniversalTime();
            ForceExitRelativeToAbsolute(boundaryUT, "force-absolute-refly-setting");
            ParsekLog.Info("Anchor",
                "force-absolute-refly: closed Relative section and continued Absolute " +
                $"vesselPid={RecordingVesselId} ut={boundaryUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }
        else
        {
            ParsekLog.VerboseOnChange(
                "Anchor",
                "force-absolute-refly-skip",
                ActiveTree?.ActiveRecordingId ?? "(none)",
                "force-absolute-refly: bypass and nearest-search skipped " +
                $"vesselPid={RecordingVesselId}");
        }
        return;
    }
    // existing bypass + nearest-search path unchanged
    if (ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(...)) { ... }
    ...
}
```

The early-return ensures both the bypass and the nearest-search are
skipped. The `VerboseOnChange` per-recording-id key keeps log volume
sane: one line per provisional recording when the gate first applies,
then silence until the active recording id changes.

### Recorder gate — BackgroundRecorder

`BackgroundRecorder.UpdateBackgroundAnchorDetection`
(at the bypass site `Source/Parsek/BackgroundRecorder.cs:4866`):

```csharp
bool isReFlyProvisional = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(tree);
if (ParsekSettings.Current != null
    && ParsekSettings.Current.forceAbsoluteForReFlyProvisional
    && isReFlyProvisional)
{
    if (state.isRelativeMode)
    {
        ExitBackgroundRelativeMode(state, bgVessel, ut, "force-absolute-refly-setting");
    }
    ParsekLog.VerboseOnChange(
        "BgRecorder",
        "force-absolute-refly-skip-bg",
        treeRec.RecordingId,
        $"force-absolute-refly: bypass + nearest-search skipped pid={state.vesselPid} " +
        $"recordingId={treeRec.RecordingId}");
    return;
}
// existing bypass + nearest-search path unchanged
```

Same shape, same early-return. The BG site already has
`ExitBackgroundRelativeMode` for clean Relative-mode exit, mirroring
`ForceExitRelativeToAbsolute` on the flight side.

### UI

Add a toggle to `Source/Parsek/UI/SettingsWindowUI.cs` after the
`useCoBubbleBlend` toggle at line 443. Same shape:

```csharp
bool forceAbsoluteForReFlyProvisional = GUILayout.Toggle(
    s.forceAbsoluteForReFlyProvisional,
    new GUIContent(" Force Absolute for re-fly provisional (experimental)",
        "When on, re-fly provisional recordings skip Relative-anchored authoring and stay in Absolute mode. "
        + "Useful for A/B testing whether simplified Absolute rendering is visually equivalent to the current Relative-against-superseded-origin path. "
        + "Off (default) preserves current ReFlyAnchorSelection behavior. Flipping mid-recording closes the current section."));
if (forceAbsoluteForReFlyProvisional != s.forceAbsoluteForReFlyProvisional)
{
    s.forceAbsoluteForReFlyProvisional = forceAbsoluteForReFlyProvisional;
    ParsekLog.Info("UI", $"Setting changed: forceAbsoluteForReFlyProvisional={s.forceAbsoluteForReFlyProvisional}");
}
```

Place under the Diagnostics group alongside `useCoBubbleBlend` — same
A/B-testing context.

## Logging

- `ParsekLog.Info` once when the setting flips
  (`NotifyForceAbsoluteForReFlyProvisionalChanged`).
- `ParsekLog.Info` from each gate firing that closes an existing Relative
  section (rare; bounded by section boundaries).
- `ParsekLog.VerboseOnChange` from each gate firing in steady Absolute
  state — keyed on the active recording id so each provisional emits at
  most one line per recording-id transition. Empirically this means
  one log line per re-fly session at the FlightRecorder site and a few
  at the BG site (one per packed vessel that becomes a re-fly
  provisional). Acceptable.

## Tests

Add `Source/Parsek.Tests/ForceAbsoluteReFlyProvisionalSettingTests.cs`
(`[Collection("Sequential")]`):

1. `IsActiveRecordingReFlyProvisional_NullMarker_ReturnsFalse`
2. `IsActiveRecordingReFlyProvisional_MismatchActiveId_ReturnsFalse`
3. `IsActiveRecordingReFlyProvisional_NullActiveTree_ReturnsFalse`
4. `IsActiveRecordingReFlyProvisional_NullActiveRecordingId_ReturnsFalse`
5. `IsActiveRecordingReFlyProvisional_MatchingMarker_ReturnsTrue`
6. `ForceAbsoluteSetting_Default_IsFalse`
7. `ForceAbsoluteSetting_FlipFiresLogLine` — uses `ParsekLog.TestSinkForTesting`
   to assert one `[Settings]` line on the false→true edge and one on
   true→false.

The `IsActiveRecordingReFlyProvisional_*` tests pin the helper
directly; they don't touch the recorder gate (which lives in a
non-pure code path with KSP state dependencies). The gate's
behavioral correctness is validated by:

- An in-game test (`Source/Parsek/InGameTests/ForceAbsoluteReFlyProvisionalInGameTest.cs`)
  that arms a synthetic re-fly marker, flips the setting on, runs
  a few recorder ticks, asserts the active recording's tail section
  is `ReferenceFrame.Absolute`, then flips the setting off, runs a
  few more ticks, asserts the next new section is `ReferenceFrame.Relative`
  if the bypass would have fired. The test is `[InGameTest(Scene = FLIGHT)]`
  and skips if no live `ParsekScenario` is available.

## Validation experiment

After implementation:

1. Build the mod (`cd Source/Parsek && dotnet build`).
2. Reproduce the scenario from `logs/2026-05-17_1529_cobubble-disabled-refly`
   (booster Re-Fly to space): once with the setting OFF, once with it ON.
3. Compare the two recordings:
   - `find saves/<save>/Parsek/Recordings -newer <timestamp>` to locate
     the per-run `.prec` sidecars.
   - Open the `.prec` files (binary; use `RecordingSidecarBinary`'s
     readable mirror via `writeReadableSidecarMirrors=true`) and verify
     the OFF run has Relative sections with `anchorRecordingId` set and
     the ON run has Absolute sections with no anchor.
4. Play back both recordings as ghosts (after merge) and compare the
   visual rendering of the re-fly fork ghost. Look for:
   - Positional drift vs the live recording trace.
   - Rotation accuracy on staging events.
   - Any visible jitter from the absence of common-mode noise cancellation.
5. Capture both runs via `python scripts/collect-logs.py forceabsolute-refly`.
6. Decide whether the visual delta is acceptable. If yes, the
   `refly-postmerge-relative-to-absolute` plan becomes the next step.

## Rollback

The change is fully reversible by flipping the setting off. Recordings
authored with the setting on remain Absolute on disk; they continue to
play back correctly via the existing Absolute path (no migration
needed). No data is destroyed; no schema is bumped.

If the experiment surfaces a regression we can't characterize, we
revert the commit and the in-place recordings still play back as
Absolute via the existing Absolute path.

## Out of scope (deliberately)

- Promotion of pre-existing Relative-authored re-fly forks to Absolute.
  Existing recordings stay Relative; only new recordings respect the
  setting.
- `bodyFixedFrames` population on re-fly fork sections. With the setting
  on, sections are Absolute and `Points` + `TrackSection.frames` already
  carry body-fixed lat/lon/alt. There is no `bodyFixedFrames`-specific
  authoring needed.
- Docking-mid-rewind edge case. With this experiment the docking re-fly
  loses Relative-against-real-station tracking when the setting is on.
  Documented as a known regression of the experiment toggle; if it
  matters, a future refinement can narrow the gate to skip Relative
  authoring only when the candidate anchor is the supersede target
  itself (preserving Relative against truly external persistent vessels).
- CHANGELOG / todo entry. Will be added in the implementation commit
  per project workflow.

## File list

- `Source/Parsek/ParsekSettings.cs` — new field + getter/setter +
  `NotifyForceAbsoluteForReFlyProvisionalChanged`.
- `Source/Parsek/ParsekSettingsPersistence.cs` — storage field + key +
  Save/Load + reconcile block + log lines.
- `Source/Parsek/ReFlyAnchorSelection.cs` — new
  `IsActiveRecordingReFlyProvisional` helper.
- `Source/Parsek/FlightRecorder.cs` — gate at head of `!onSurface`
  branch in `UpdateAnchorDetection`.
- `Source/Parsek/BackgroundRecorder.cs` — gate at head of the bypass
  site in `UpdateBackgroundAnchorDetection`.
- `Source/Parsek/UI/SettingsWindowUI.cs` — new toggle under Diagnostics.
- `Source/Parsek.Tests/ForceAbsoluteReFlyProvisionalSettingTests.cs` —
  new xUnit suite.
- `Source/Parsek/InGameTests/ForceAbsoluteReFlyProvisionalInGameTest.cs` —
  new in-game test.
- `CHANGELOG.md` — Internals entry under the in-progress version.
- `docs/dev/todo-and-known-bugs.md` — entry noting the experiment is
  live behind the setting + the docking-mid-rewind known regression.
