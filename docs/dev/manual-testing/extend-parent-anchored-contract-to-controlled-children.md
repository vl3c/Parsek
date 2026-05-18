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
3. The recorder transitions logs through `[BgRecorder]` and `[BG_CreateAbs]`
   tagged lines. Useful patterns:
   - `[Parsek][VERBOSE][BgRecorder] Parent-anchor contract applied at split: childRecId=... population=controlled-child parentRecId=...` from the split moment.
   - `[Parsek][VERBOSE][BG_CreateAbs]` per-frame BG sample lines once the
     child is unpacked (boundary samples that drive the section transition
     show up here when the proximity gate flips).

### Phase 3: re-fly + playback (canonical PR #872 acceptance)

1. Enable ghost render tracing (Settings > Diagnostics > Ghost render tracing).
   The `[PlaybackTrace]` line format is
   `rec=<short-id> #<idx> ut=... sec=... [start,end] ref=<sectionFrame> worldPos=(...) dM=... dSpd=...`.
   The `ref=Relative` / `ref=Absolute` field is the canonical "which path
   played this frame" indicator.
2. Trigger Re-Fly on the parent recording. Watch the controlled child's ghost
   during playback INSIDE the Relative section's UT range (typically the first
   30-60 seconds after decouple).
3. The ghost should track smoothly with no mid-flight trajectory snap regardless
   of whether nearby sibling debris pieces survive or crash during the playback
   window. **Watch the PR #872 snap UTs specifically**: the original bug snapped
   at UT 34.74 and crossfaded back at UT 38.76.
4. Check the log for:
   - `[Parsek][INFO][PlaybackTrace] rec=<controlled-child-short-id> ... ref=Relative ...` for frames inside the Relative section UT range (this is the canonical "the path played via parent-anchored Relative, not CoBubble peer-blend" assertion).
   - Look for the controlled child's `dM=` (delta meters per frame) staying
     close to its `dSpd*dt` expected value; a `dM=178.xx` against an
     `dSpd=0.xx` expected delta is the PR #872 / PR #874 snap signature.

### Phase 4: post-window Absolute tail (regression)

1. Continue Re-Fly playback past the hysteresis-exit UT (when the controlled
   child crossed 550m from the parent).
2. The ghost should continue playing smoothly through the post-window Absolute
   tail. The dispatch path switches to the standard Absolute trace pattern:
   - `[Parsek][INFO][PlaybackTrace] rec=<controlled-child-short-id> ... ref=Absolute ...` for frames inside the post-hysteresis Absolute section.
   - NO `[Parsek][WARN][Anchor] recorded-relative-retired: reason=parent-anchored-debris-outside-relative-coverage ...` warnings for the controlled child (the retirement helper is correctly inert in the Absolute section).

### Phase 5: genuine-debris regression

1. With the same playtest setup, watch the radial-booster debris ghosts during
   re-fly playback.
2. They should play through the same parent-anchored body-fixed primary path as
   before the fix:
   - `[Parsek][INFO][PlaybackTrace] rec=<debris-short-id> ... ref=Relative ...` for each debris piece inside its Relative section UT range.
3. No new `recorded-relative-retired` warnings beyond ones that already fire
   today (compare against a pre-fix log if available); no regressed snapping
   on the debris ghosts.

## Outcome

Record the outcome in the PR description: scenario summary, KSP.log
attached, snap/no-snap verdict per controlled child observed, and any new
anomalies that warrant a separate Open todo entry.
