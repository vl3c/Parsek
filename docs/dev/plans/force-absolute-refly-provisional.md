# Force-Absolute for Re-Fly Provisional Recordings (experiment)

## Motivation

Re-fly fork (provisional) recordings currently author `ReferenceFrame.Relative`
`TrackSection`s with `anchorRecordingId` pinned to the superseded origin
recording (`Source/Parsek/ReFlyAnchorSelection.cs:96-101`,
`Source/Parsek/FlightRecorder.cs:5689-5697`,
`Source/Parsek/BackgroundRecorder.cs:4866-4875`). This inheritance comes
from the general 2300m physics-bubble rule in `AnchorDetector` (every
recording inside the bubble of an eligible anchor opens Relative), and
was not chosen deliberately for re-fly. `ReFlyAnchorSelection` (PR #889)
is a 269-LoC patch on top, pinning the anchor to the supersede target so
the nearest-search does not pick a fast-separating sibling and produce
178m playback teleports
(`docs/dev/todo-and-known-bugs.md:280`).

The sub-meter precision advantage of Relative sections requires a **live**
anchor (real vessel, just sampled: see
`docs/parsek-ghost-trajectory-rendering-design.md:55-95`). The re-fly
fork's anchor is itself a ghost being resolved from recorded data via
Slerp (`Source/Parsek/RecordedRelativeAnchorPoseResolver.cs`); the
float-noise common-mode cancellation does not apply. The deferred plan
`docs/dev/plans/refly-postmerge-relative-to-absolute.md:42-89` already
documents this:

> RELATIVE is the right contract when both vessels in the relationship
> are ghosts replaying together... ABSOLUTE is the right contract when
> one vessel is real (live) and the other is a ghost: the re-fly
> scenario being the canonical case.

This experiment lets us **measure the actual visual delta** before
committing to a contract flip. A new opt-in setting,
`forceAbsoluteForReFlyProvisional`, causes the recorder to skip Relative
authoring entirely while a re-fly provisional is the active recording,
keeping the recording in `ReferenceFrame.Absolute` so its `Points` and
`TrackSection.frames` carry body-fixed lat/lon/alt + `srfRelRotation`,
the same contract the debris path consumes via
`InterpolateAndPosition`.

The setting is **off by default**. Flipping it ON authors all subsequent
re-fly provisional sections as Absolute; existing Relative sections on
disk are untouched. Playback for both modes runs through the existing
dispatch: no rendering changes.

## Goals

1. Add a settings toggle (`forceAbsoluteForReFlyProvisional`,
   default `false`) following the `useCoBubbleBlend` pattern at
   `Source/Parsek/ParsekSettings.cs:193-208`, **without** the
   `.pann` ConfigurationHash participation (see "Out-of-band:
   ConfigurationHash" below for why this differs from `useCoBubbleBlend`).
2. Gate three sites that can open Relative for a re-fly provisional, so
   that when the setting is on **and** the active recording is the live
   re-fly provisional, the recorder stays in Absolute:
   - `FlightRecorder.UpdateAnchorDetection` (`FlightRecorder.cs:5648`)
     `!onSurface` branch: skip both the `ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor`
     bypass and the fallback nearest-search.
   - `FlightRecorder.RestoreTrackSectionAfterFalseAlarm`
     (`FlightRecorder.cs:7227`): when resuming from a false-alarm stop,
     force `resumeRef = Absolute` instead of inheriting the prior
     section's `referenceFrame`, and clear `resumeAnchorRecordingId`
     before the `StartNewTrackSection` call at line 7366.
   - `BackgroundRecorder.UpdateBackgroundAnchorDetection`
     (`BackgroundRecorder.cs:4866`): mirror the FlightRecorder gate.
3. Surface the gate firings in the log so playtests can confirm the
   experiment ran.
4. Leave the playback path completely untouched. Relative sections
   recorded under the old setting still play back via the existing
   resolver; Absolute sections recorded under the new setting play back
   via the existing Absolute path.

## Parent-anchored re-fly provisionals (gate applies to them too)

An earlier revision of this plan carved parent-anchored re-fly
provisionals (controlled-decoupled children being re-flown, with
`DebrisParentRecordingId` propagated by `RewindInvoker.cs:247`) out of
the gate, on the premise that their Relative contract uses a LIVE
parent vessel as anchor and is therefore orthogonal to the
"Relative-against-superseded-origin" anti-pattern this experiment
targets.

Runtime analysis disproved that premise. The 2026-05-19 Kerbal X Probe
re-fly playtest (`logs/2026-05-19_1851_forceabsolute-refly-on-attempt2`)
showed the recorder's `TryResolveReFlyProvisionalAnchor` bypass runs
first at `FlightRecorder.cs:5746` and pins the Relative anchor to the
supersede target (a ghost resolved via Slerp), not to the live parent.
The provisional's track sections came out with
`anchorRecordingId = beaebfe9...` (the prior probe recording, the
supersede target), exact same anti-pattern as a top-level re-fly. The
parent-anchored carve-out was silently making the toggle a no-op for
every controlled-decoupled child or debris re-fly, for a reason that
did not match runtime behavior.

The carve-out was therefore removed. The predicate fires for any active
re-fly provisional whose id matches `marker.ActiveReFlyRecordingId`,
regardless of `DebrisParentRecordingId`. Per CLAUDE.md, the
parent-anchored "primary surface is `bodyFixedFrames`" contract is
already Absolute-style in playback, so authoring Absolute sections on
the provisional aligns with that contract rather than conflicting.

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

## Out-of-band: ConfigurationHash

`useCoBubbleBlend` participates in `PannotationsSidecarBinary`'s
`ConfigurationHash` because flipping it changes co-bubble offset
trace generation, which lives inside `.pann` sidecars
(`ParsekSettings.cs:188-191`, `PannotationsSidecarBinary.cs:282, 965`).
The new `forceAbsoluteForReFlyProvisional` setting affects **recorder
authoring of `.prec` files**, not pannotation block contents. `.pann`
files do not encode `ReferenceFrame` choice: they're a separate
rendering-side sidecar. So **the new setting does NOT participate in
the `.pann` ConfigurationHash**. Do not add a hash byte for it; flipping
the setting must not invalidate cached `.pann` files for the user's
prior re-fly forks.

## Design

### Setting

Add to `ParsekSettings.cs` (after the `useCoBubbleBlend` block at
line 208):

```csharp
[GameParameters.CustomParameterUI("Force Absolute for re-fly provisional",
    toolTip = "Experimental. When on, re-fly provisional recordings skip Relative-anchored authoring and stay in Absolute mode. Off (default) preserves the current ReFlyAnchorSelection pin-to-supersede-target behavior. Off and on produce different on-disk recordings; flipping mid-recording is supported but mixes modes within one recording.")]
public bool forceAbsoluteForReFlyProvisional
{
    get { return _forceAbsoluteForReFlyProvisional; }
    set
    {
        if (_forceAbsoluteForReFlyProvisional == value) return;
        bool prev = _forceAbsoluteForReFlyProvisional;
        _forceAbsoluteForReFlyProvisional = value;
        // Gate BOTH Notify (Anchor flip log) and Record* (persistence
        // write) on IsReconciled. KSP's GameParameters.OnLoad calls
        // this setter to restore the field from .sfs on every scene /
        // quicksave load; that is a state restore, not a user toggle,
        // and must not emit a "False->True" Anchor flip line. See
        // PR 901 validation playtest (commit 187b62d1).
        if (ParsekSettingsPersistence.IsReconciled)
        {
            NotifyForceAbsoluteForReFlyProvisionalChanged(prev, value);
            ParsekSettingsPersistence.RecordForceAbsoluteForReFlyProvisional(value);
        }
    }
}
private bool _forceAbsoluteForReFlyProvisional = false;
```

Plus the `NotifyForceAbsoluteForReFlyProvisionalChanged` log-on-flip
helper (same shape as `NotifyUseCoBubbleBlendChanged`). Existing
notify-helper tests cover the on-flip log line; the gate test
`ForceAbsoluteSetting_SetterDuringUnreconciledLoad_DoesNotLog`
pins the IsReconciled gate (added after the 2026-05-19 validation
playtest exposed 14 spurious flip lines per cycle).

Persistence mirror in `ParsekSettingsPersistence.cs` follows the
existing pattern:
- Storage field `storedForceAbsoluteForReFlyProvisional`
- Save/load key `ForceAbsoluteForReFlyProvisional`
- `RecordForceAbsoluteForReFlyProvisional(bool)` method
- Reconcile block in `ApplyTo` (`Source/Parsek/ParsekSettingsPersistence.cs:344-350` shape)
- Two log lines (one in the reconcile diff dump, one in the
  post-reconcile diff dump): both mirroring the existing
  `useCoBubbleBlend` entries.

### Helper in `ReFlyAnchorSelection`

Add a public predicate so all gate sites and tests share the same
"is the active recording a re-fly provisional that should skip Relative
authoring?" check. Mirrors `TryResolveReFlyProvisionalAnchor`'s
pure-overload + production-overload pattern
(`Source/Parsek/ReFlyAnchorSelection.cs:43-56` / `:64-170`) so xUnit
fixtures can pin every branch without touching `ParsekScenario`
singleton state.

```csharp
// Pure overload: unit-testable with synthesized markers.
internal static bool IsActiveRecordingReFlyProvisional(
    ReFlySessionMarker marker,
    string activeRecordingId)
{
    if (marker == null) return false;
    if (string.IsNullOrEmpty(activeRecordingId)) return false;
    if (!string.Equals(
            marker.ActiveReFlyRecordingId,
            activeRecordingId,
            StringComparison.Ordinal))
        return false;
    return true;
}

// Production wrapper: reads marker + active id from live scenario state.
internal static bool IsActiveRecordingReFlyProvisional(
    RecordingTree activeTree)
{
    var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
    string activeRecordingId = activeTree?.ActiveRecordingId;
    return IsActiveRecordingReFlyProvisional(marker, activeRecordingId);
}
```

The pure overload is fully deterministic and side-effect-free; tests pin
every branch (null marker, id mismatch, null active id, matching). The
production wrapper does no tree lookup. No recursion or chain walk:
that's the `TryResolveReFlyProvisionalAnchor` resolver's contract, not
this predicate's.

### Recorder gate: FlightRecorder anchor detection

In `FlightRecorder.UpdateAnchorDetection`
(`Source/Parsek/FlightRecorder.cs:5648`), add the gate **before** the
bypass at the head of the `!onSurface` branch (line 5680):

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
                identity: "force-absolute-refly|" + (ActiveTree?.ActiveRecordingId ?? "(none)"),
                stateKey: "skipped",
                message: "force-absolute-refly: bypass and nearest-search skipped " +
                    $"vesselPid={RecordingVesselId}");
        }
        return;
    }
    // existing bypass + nearest-search path unchanged
    ...
}
```

`VerboseOnChange` parameter order is `(subsystem, identity, stateKey,
message)` per `ParsekLog.cs:271-274`. The composite identity
`"force-absolute-refly|<recId>"` is the stable scope (one per
provisional recording id). The stateKey `"skipped"` never changes, so
the line emits once when first observed for a recording id and stays
silent thereafter: exactly one log line per re-fly session in the
steady-state Absolute path.

### Recorder gate: FlightRecorder false-alarm resume

`FlightRecorder.RestoreTrackSectionAfterFalseAlarm`
(`Source/Parsek/FlightRecorder.cs:7227-7372`) is a second site that
opens Relative: it inherits `resumeRef = resumeSection.Value.referenceFrame`
(line 7241-7243) from the saved prior section. If a re-fly provisional
was in Relative mode and hit a false-alarm stop, the resume re-opens
Relative regardless of the setting, bypassing the anchor-detection
gate above.

Add the gate just after the `resumeSection.HasValue` block but before
the Relative-validation block (line 7250), wrapping the
`resumeRef = Relative` assignment so it never holds Relative when the
gate fires:

```csharp
ReferenceFrame resumeRef = resumeSection.HasValue
    ? resumeSection.Value.referenceFrame
    : (isRelativeMode ? ReferenceFrame.Relative : ReferenceFrame.Absolute);
TrackSectionSource resumeSource = ...;

// Force-Absolute for re-fly provisional: if the setting is on and this
// recording is the provisional, downgrade a Relative resume to Absolute
// here so the subsequent anchor-validation + StartNewTrackSection at
// :7366 opens an Absolute section instead of Relative. Mirrors the
// existing Relative-resume-failed downgrade pattern at :7265-7267.
bool forceAbsoluteReFly = ParsekSettings.Current != null
    && ParsekSettings.Current.forceAbsoluteForReFlyProvisional
    && ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(ActiveTree);
if (forceAbsoluteReFly && resumeRef == ReferenceFrame.Relative)
{
    ParsekLog.Info("Anchor",
        "force-absolute-refly: RELATIVE resume downgraded to ABSOLUTE " +
        $"vesselPid={RecordingVesselId} ut={ut.ToString("F3", CultureInfo.InvariantCulture)}");
    resumeRef = ReferenceFrame.Absolute;
    isRelativeMode = false;
    ClearCurrentRecordingAnchor();
}

string resumeAnchorRecordingId = null;
... // unchanged below; with resumeRef=Absolute the Relative branch is skipped
```

This reuses the same `isRelativeMode = false` + `ClearCurrentRecordingAnchor`
pattern as the existing in-method downgrades, so the
`downgradedRelativeToAbsolute` boundary-point repair at line 7324
fires correctly (the prior Relative section had body-fixed primary
samples: those become the new Absolute seed via the existing
`absoluteBoundaryPoint` swap at line 7327-7347).

### Recorder gate: BackgroundRecorder anchor detection

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
        identity: "force-absolute-refly-bg|" + treeRec.RecordingId,
        stateKey: "skipped",
        message: $"force-absolute-refly: bypass + nearest-search skipped pid={state.vesselPid} " +
            $"recordingId={treeRec.RecordingId}");
    return;
}
// existing bypass + nearest-search path unchanged
```

Same shape, same early-return. The BG site already has
`ExitBackgroundRelativeMode` for clean Relative-mode exit, mirroring
`ForceExitRelativeToAbsolute` on the flight side.

`BackgroundRecorder` does **not** have a false-alarm-resume analog at
the per-frame level (false-alarm resume is a flight-side concept), so
no third BG gate site is needed.

### UI

Add a toggle to `Source/Parsek/UI/SettingsWindowUI.cs` after the
`useCoBubbleBlend` toggle at line 443. Same shape:

```csharp
bool forceAbsoluteForReFlyProvisional = GUILayout.Toggle(
    s.forceAbsoluteForReFlyProvisional,
    new GUIContent(" Force Absolute for re-fly provisional (experimental)",
        "When on, re-fly provisional recordings skip Relative-anchored authoring and stay in Absolute mode. "
        + "Useful for A/B testing whether simplified Absolute rendering is visually equivalent to the current Relative-against-superseded-origin path. "
        + "Off (default) preserves current ReFlyAnchorSelection behavior. Flipping mid-recording mixes modes within one recording."));
if (forceAbsoluteForReFlyProvisional != s.forceAbsoluteForReFlyProvisional)
{
    s.forceAbsoluteForReFlyProvisional = forceAbsoluteForReFlyProvisional;
    ParsekLog.Info("UI", $"Setting changed: forceAbsoluteForReFlyProvisional={s.forceAbsoluteForReFlyProvisional}");
}
```

Place under the Diagnostics group alongside `useCoBubbleBlend`: same
A/B-testing context.

## Mid-recording flip semantics

Flipping the setting mid-recording is supported but produces a section
boundary in the active recording, by design:

- **OFF → ON**: the next gated tick (anchor detection or false-alarm
  resume) closes the current Relative section via
  `ForceExitRelativeToAbsolute` / the resume-downgrade path and opens
  an Absolute section. The two sections are adjacent in the recording's
  `TrackSections` list; playback dispatches each per its own
  `referenceFrame`.
- **ON → OFF**: the next gated tick that would have skipped runs the
  normal bypass + nearest-search. If the re-fly anchor is still in
  range, `TryResolveReFlyProvisionalAnchor` pins it and the recorder
  enters Relative mode mid-recording. Same section-boundary semantics.

A/B comparison runs must keep the setting stable for the whole
recording; flipping during the run produces a mixed-mode recording
that's harder to compare against a single-mode baseline. Document
this in the UI tooltip and the validation experiment section.

## Logging

- `ParsekLog.Info` once when the setting flips
  (`NotifyForceAbsoluteForReFlyProvisionalChanged`).
- `ParsekLog.Info` from each gate firing that closes an existing
  Relative section (anchor-detection or false-alarm-resume downgrade;
  rare, bounded by section boundaries).
- `ParsekLog.VerboseOnChange` from each gate firing in steady Absolute
  state: composite identity `"force-absolute-refly|<recId>"`,
  stateKey `"skipped"`. Emits exactly once per re-fly session at each
  gate site.

## Tests

Add `Source/Parsek.Tests/ForceAbsoluteReFlyProvisionalSettingTests.cs`
(`[Collection("Sequential")]`):

1. `IsActiveRecordingReFlyProvisional_NullMarker_ReturnsFalse`
2. `IsActiveRecordingReFlyProvisional_MismatchActiveId_ReturnsFalse`
3. `IsActiveRecordingReFlyProvisional_NullActiveId_ReturnsFalse`
4. `IsActiveRecordingReFlyProvisional_EmptyActiveId_ReturnsFalse`
5. `IsActiveRecordingReFlyProvisional_MatchingMarker_ReturnsTrue`
6. `IsActiveRecordingReFlyProvisional_Wrapper_NullScenario_ReturnsFalse`
7. `IsActiveRecordingReFlyProvisional_Wrapper_NullActiveTree_ReturnsFalse`
8. `IsActiveRecordingReFlyProvisional_Wrapper_MarkerMatchesActiveId_ReturnsTrue`
9. `IsActiveRecordingReFlyProvisional_Wrapper_MarkerMatchesParentAnchored_ReturnsTrue`:
   pins the post-carve-out behavior: a provisional carrying a non-null
   `DebrisParentRecordingId` still fires the predicate, since runtime
   analysis showed parent-anchored re-fly provisionals fall into the
   same supersede-target anchor anti-pattern as top-level ones.
10. `ForceAbsoluteSetting_Default_IsFalse`
11. `ForceAbsoluteSetting_PersistenceRoundTrip`
12. `ForceAbsoluteSetting_FlipLogsInfo`
13. `ForceAbsoluteSetting_NoLogWhenUnchanged`

The unit tests pin the pure helper and the setting field; the gates
themselves are validated by in-game tests.

In-game tests (`Source/Parsek/InGameTests/`):

- `ForceAbsoluteReFlyProvisionalGateInGameTest`: observational test
  that runs during any live re-fly session. Asserts that when the
  setting is on and the predicate fires, the active provisional's
  tail `TrackSection.referenceFrame` is Absolute. Covers the FLIGHT-
  side anchor-detection gate and (since the carve-out removal) also
  the parent-anchored re-fly case in one branch. Skips outside an
  active re-fly. BG-side and false-alarm-resume coverage is
  acknowledged as gap; see `docs/dev/todo-and-known-bugs.md`.

## Validation experiment

After implementation:

1. Build the mod (`cd Source/Parsek && dotnet build`).
2. Reproduce the scenario from `logs/2026-05-17_1529_cobubble-disabled-refly`
   (booster Re-Fly to space): once with the setting OFF, once with it ON.
   **Do not flip the setting mid-recording**: keep it stable for the
   duration of each run.
3. Compare the two recordings:
   - `find saves/<save>/Parsek/Recordings -newer <timestamp>` to locate
     the per-run `.prec` sidecars.
   - Enable `writeReadableSidecarMirrors=true` in Settings > Diagnostics
     and verify the readable mirror dumps `TrackSection.referenceFrame`
     and `anchorRecordingId` per section. **Pre-flight check**: grep
     `RecordingSidecarBinary.cs` for `WriteReadableMirror` and confirm
     the mirror serializes both fields. If not, add the diagnostic
     surface in this PR or a follow-up before the experiment matters.
   - The OFF run should have Relative sections with `anchorRecordingId`
     pointing at the superseded origin; the ON run should have Absolute
     sections with no anchor.
4. Play back both recordings as ghosts (after merge) and compare the
   visual rendering of the re-fly fork ghost. Look for:
   - Positional drift vs the live recording trace.
   - Rotation accuracy on staging events.
   - Any visible jitter from the absence of common-mode noise cancellation.
5. Capture both runs via `python scripts/collect-logs.py forceabsolute-refly`.
6. Decide whether the visual delta is acceptable. If yes, the
   `refly-postmerge-relative-to-absolute` plan becomes the next step.

## Known regressions of the experiment toggle

When the setting is ON, the following narrow scenarios lose Relative-
against-live-anchor precision (documented; not addressed by this
experiment):

1. **Docking-mid-rewind**: a re-fly that starts mid-docking-approach
   (within `DockingApproachDistance = 200m` of a real persistent
   station, `ParsekConfig.cs:46`) loses Relative-against-real-station
   tracking. With the setting on, the nearest-search is skipped
   wholesale; a future refinement could narrow the gate to skip only
   when the candidate anchor is the supersede target itself.
2. **Loop-anchored re-fly fork**: a re-fly inside a loop chain where
   the chain anchor is a live persistent vessel (loop logistics,
   `Recording.LoopAnchorVesselId` via the loop-relative path). With
   the setting on, that Relative-against-live-loop-anchor authoring
   is suppressed. Verify reachability post-implementation: if a
   re-fly provisional can be loop-anchored, this is a second known
   regression to call out in the toggle's UI tooltip and the
   `todo-and-known-bugs.md` entry.

Both are narrow cases unlikely to apply to the "fly the booster back"
scenario this experiment targets. The setting being OFF (default)
preserves current behavior for both.

## Reversibility

- The change is fully reversible by flipping the setting off.
  Subsequent recordings author Relative again as before.
- No `ConfigurationHash` byte (see "Out-of-band: ConfigurationHash").
- No schema bump.
- No derived caches keyed on the setting value.
- **One caveat**: `.prec` files recorded with the setting ON contain
  Absolute sections in places that would have been Relative under the
  default. Flipping the setting OFF does not migrate those recordings
  back to Relative: they continue playing back via the Absolute path
  (which is the same dispatch the engine uses for every other Absolute
  section). The setting-on `.prec` is effectively a one-way commitment
  to Absolute for that recording's covered span.

## Out of scope (deliberately)

- Promotion of pre-existing Relative-authored re-fly forks to Absolute.
  Existing recordings stay Relative; only new recordings respect the
  setting.
- `bodyFixedFrames` population on re-fly fork sections. With the setting
  on, sections are Absolute and `Points` + `TrackSection.frames` already
  carry body-fixed lat/lon/alt. There is no `bodyFixedFrames`-specific
  authoring needed.
- Narrowing the gate to filter only the supersede-target anchor (the
  "preserve docking re-flies" refinement). Listed as known regression
  #1; future-scope refinement.
- CHANGELOG / todo entry. Will be added in the implementation commit
  per project workflow.

## File list

- `Source/Parsek/ParsekSettings.cs`: new field + getter/setter +
  `NotifyForceAbsoluteForReFlyProvisionalChanged`.
- `Source/Parsek/ParsekSettingsPersistence.cs`: storage field + key +
  Save/Load + reconcile block + log lines.
- `Source/Parsek/ReFlyAnchorSelection.cs`: new
  `IsActiveRecordingReFlyProvisional` pure + production overloads.
- `Source/Parsek/FlightRecorder.cs`: gate at head of `!onSurface`
  branch in `UpdateAnchorDetection`, plus the
  `RestoreTrackSectionAfterFalseAlarm` downgrade.
- `Source/Parsek/BackgroundRecorder.cs`: gate at head of the bypass
  site in `UpdateBackgroundAnchorDetection`.
- `Source/Parsek/UI/SettingsWindowUI.cs`: new toggle under Diagnostics.
- `Source/Parsek.Tests/ForceAbsoluteReFlyProvisionalSettingTests.cs` -
  new xUnit suite.
- `Source/Parsek/InGameTests/ForceAbsoluteReFlyProvisionalGateInGameTest.cs` -
  observational FLIGHT-side gate test.
- `CHANGELOG.md`: Internals entry under the in-progress version.
- `docs/dev/todo-and-known-bugs.md`: entry noting the experiment is
  live behind the setting + the two known regressions.
