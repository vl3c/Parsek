# Manual smoke pass: extend parent-anchored contract to controlled-decoupled children

This document tracks the manual playtest for the PR #872 / #874 deeper fix.
The in-game test `ControlledChildBreakup_StampsParentAnchorContract` (under category
`Coalescer`, run via Ctrl+Shift+T) covers the contract-stamping side automatically;
the items below cover the playback-side acceptance that requires Re-Fly + watch
mode.

## Setup

1. Use the Kerbal X stock craft (or any multi-stage rocket with a controlled
   probe-cored payload as the lower-stage core; the PR #872 repro used a
   probe-cored lower stage + radial booster debris).
2. Recommended log filter while testing:
   `grep -E "\[Parsek\].*(Coalescer|BgRecorder|PlaybackTrace|CoBubble|large-delta)" KSP.log`.

## Acceptance items

### Phase 1: recording

1. Launch the rocket. Ascend until the staging configuration matches the PR
   #872 setup (probe-cored lower stage + sibling radial-booster debris in flight).
2. Decouple the upper stage while staying within 250m of the parent.
3. Check the live log for:
   - `[Parsek][VERBOSE][Coalescer] Parent-anchor contract applied at breakup: childRecId=... population=controlled-child parentRecId=...`
   - `[Parsek][INFO][Coalescer] ProcessBreakupEvent: controlled child created: pid=...`
4. In the Recordings table, the new controlled-child row should NOT be in the
   "Debris" group (it carries `IsDebris=false`).

### Phase 2: background sampling

1. Switch focus to a third vessel (KSC, Tracking Station, or another flight)
   so the controlled child goes background.
2. Continue flying for 30+ seconds so the child crosses the 500m / 550m
   hysteresis boundary against its parent.
3. Check the live log for the Relative section closing on hysteresis exit
   and a fresh Absolute section opening:
   - `[Parsek][VERBOSE][BgRecorder] RELATIVE mode entered (debris parent-anchor contract): ...`
   - `[Parsek][VERBOSE][BgRecorder] RELATIVE -> ABSOLUTE: ...` at hysteresis exit.

### Phase 3: re-fly + playback (canonical PR #872 acceptance)

1. Trigger Re-Fly on the parent recording. Watch the controlled child's ghost
   during playback INSIDE the Relative section's UT range (typically the first
   30-60 seconds after decouple).
2. The ghost should track smoothly with no mid-flight trajectory snap regardless
   of whether nearby sibling debris pieces survive or crash during the playback
   window. **Watch the PR #872 snap UTs specifically**: the original bug snapped
   at UT 34.74 and crossfaded back at UT 38.76.
3. Enable ghost render tracing (Settings > Diagnostics > Ghost render tracing)
   and check the log for:
   - `[Parsek][VERBOSE][PlaybackTrace] ... ParentAnchored hit: recId=<controlled-child-id> parentRecId=<parent-id>` -- this is the canonical "path log line indicates the parent recording id" assertion.
   - NO `[Parsek][VERBOSE][CoBubble] blend-window` trace lines for the
     controlled child.
   - NO `[Parsek][VERBOSE][PlaybackTrace] large-delta` trace lines for the
     controlled child.

### Phase 4: post-window Absolute tail (regression)

1. Continue Re-Fly playback past the hysteresis-exit UT (when the controlled
   child crossed 550m from the parent).
2. The ghost should continue playing smoothly through the post-window Absolute
   tail. The dispatch path should switch to the standard Absolute trace pattern:
   - `[Parsek][VERBOSE][PlaybackTrace] Absolute hit ...`
   - NO `ParentAnchoredDebrisCoverageRetired` trace (the retirement helper is
     correctly inert in the Absolute section).

### Phase 5: genuine-debris regression

1. With the same playtest setup, watch the radial-booster debris ghosts during
   re-fly playback.
2. They should play through the same parent-anchored body-fixed primary path as
   before the fix:
   - `[Parsek][VERBOSE][PlaybackTrace] ... ParentAnchored hit: recId=<debris-id> parentRecId=<parent-id>` for each debris piece.
3. No new trace patterns, no regressed snapping.

## Outcome

Record the outcome in the PR description: scenario summary, KSP.log
attached, snap/no-snap verdict per controlled child observed, and any new
anomalies that warrant a separate Open todo entry.
