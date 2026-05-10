# Re-Fly debris reference-frame mismatch (recorder live, playback frozen)

Date: 2026-05-09

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-investigate-item1-refly-anchor`

## Verdict and recommendation up front

The undocumented bug is **real by static inspection but not yet
reachable in any captured log bundle** — the trigger condition (debris
created DURING an in-place Re-Fly, parent loaded at sample time) is
narrow enough that none of the three reviewed playtests hit it. The
fix is still correctness work because the recorder and resolver
disagree on which anchor frame is in effect.

For v12 debris whose parent recording is the active Re-Fly recording,
there are two different recorder-time paths today:

- The initial loaded-state seed point prefers the recorded-parent
  fallback (`BackgroundRecorder.cs:3189-3197`). During active Re-Fly,
  that fallback builds a resolver context with `focusRecordingId: null`
  (`BackgroundRecorder.cs:4641-4644`), so it can take the existing
  frozen pre-Re-Fly substitution. The seed can therefore already be
  authored in the frozen frame.
- Ordinary periodic samples and the structural-event call-through
  re-apply the debris contract, resolve the loaded parent as a live
  anchor, and write `(dx, dy, dz)` against the mutable active-fork pose.

The bug is therefore more specific than "all recorder samples are live
while playback is frozen." The current recording can be internally
inconsistent: frozen-frame seed, then live-frame periodic samples. A
simple "delta = live - frozen" assertion only proves the periodic path;
it misdescribes the seed.

Recommendation: **PR0 first, PR1 data-gated.** PR0 is recorder
telemetry only: add `frameContract=` and `parentDriftFromRecorded=` to
the seed and periodic recorder logs with zero resolver behavior change.
PR1 chooses the behavior fix after PR0 tells us whether live-vs-recorded
drift is visible. Option B2 remains the leading PR1 candidate if PR0
shows material drift and the lockstep parent/debris test proves
post-commit decode equivalence. If PR0 shows drift below the visual
threshold, choose Option C or make no behavior change; do not land B2.

Save format should remain unchanged for either PR1 path unless the data
shows the v12 debris relative contract itself is not worth preserving for
this slice. There is no migration path because "correct contract
end-to-end" is the right pre-1.0 stance per the project's
no-backwards-compatibility memory.

The residually-live Re-Fly exclusion at
`Source/Parsek/BackgroundRecorder.cs:4242-4248` is a separate concern.
Recommendation: leave it alone in this PR (legacy v11 debris and
non-debris BG recordings depend on it for safety; touching it here
broadens the risk surface). Track removal as a follow-up once v11 debris
is no longer reachable.

## Scope

In scope:

- v12+ debris recordings (`Recording.DebrisParentRecordingId != null`)
  whose parent recording is the active Re-Fly recording
  (`marker.ActiveReFlyRecordingId == recording.DebrisParentRecordingId`).
  This is the durable post-commit contract: the child is relative to the
  active fork's path, not to the temporary frozen pre-Re-Fly overlay.
- PR0 telemetry for the recorder only. The canonical logging contract is
  in §6. PR0 must not change resolver behavior.
- All background recorder code paths that write Relative offsets for
  debris: the initial loaded-state seed, ordinary periodic samples, and
  the structural-event seam (`BackgroundRecorder.cs:3213, 4402, 6327`).
  The structural-event seam is not an independent math path: line 6327
  calls through to `ApplyBackgroundRelativeOffset`, so fixing and
  testing the periodic writer fixes structural writes too. Keep a routing
  test so this does not silently split later.
- PR1 playback resolver contexts that resolve those Relative sections
  while an active Re-Fly marker is still alive, but only after PR0 and
  the lockstep test justify Option B2. After commit, the marker is gone
  and the existing resolver already uses the final fork trajectory.

Out of scope:

- Item (2) — recorder-side residual Re-Fly exclusion broadening.
- Item (4), Item (5) — separate bugs with their own plans.
- Loop-relative recording. Looping has its own live-anchor contract
  (`Recording.LoopAnchorVesselId`) and is unrelated to Re-Fly debris.
- The focused parent vessel's own `FlightRecorder.cs` structural-event
  point. Debris children created by that breakup and handed to
  `BackgroundRecorder.OnVesselBackgrounded` are in scope, including the
  initial seed point written in `InitializeLoadedState`.

## 1. Verification step (do this BEFORE the code change)

The frame mismatch must be measured before PR1 behavior lands. The
existing log bundles do not settle it — see §1d.

### 1a. xUnit reproduction (mandatory)

New test class `Source/Parsek.Tests/ReFlyDebrisFrameMismatchTests.cs`,
[Collection("Sequential")], drives the recorder and resolver in
lockstep:

- Build a synthetic Re-Fly setup: an origin `Recording` covering UT
  [0, 100], call `Recording.CapturePreReFlyAnchorTrajectory(sessionId)`
  at UT 50, set a `ReFlySessionMarker` with
  `ActiveReFlyRecordingId = origin.RecordingId` and
  `InPlaceContinuation = true`.
- Add a paired no-flag marker case (`InPlaceContinuation = false`) with
  the same active id + captured snapshot. Playback freezes in both
  cases today, so the resolver-side fix must not accidentally introduce
  an extra in-place-only gate.
- Author a fake "live diverged" active fork trajectory at UT 60 that
  differs from the frozen pose by a known translation + rotation
  `delta`.
- Drive two recorder entry points, not one generic payload seam:
  `InitializeLoadedState` seed and ordinary
  `ApplyBackgroundRelativeOffset` periodic sampling. Pre-fix expectation:
  the seed prefers the recorded fallback at `BackgroundRecorder.cs:3189`
  and can be authored in the frozen frame, while the periodic sample uses
  the loaded live anchor and is authored in the mutable active-fork frame.
  The test should prove that asymmetry before any PR1 behavior change.
- At the seed recorded-fallback call, pass the local `recordingId`
  parameter from `InitializeLoadedState` into the future context helper
  and assert it is non-empty. Do not leave the plan saying "pass
  `state.recordingId`" without naming the seed-local source, because this
  is the call where a missing focus id would silently preserve the frozen
  seed.
- Drive `RelativeAnchorResolver.TryResolveAnchorPose` against the same
  child recording id and UT with the active marker alive. Pre-fix,
  expected deltas differ by entry point: the seed should reconstruct near
  the frozen pose, while periodic should be wrong by approximately
  `live - frozen`. Post-PR1 Option B2, both seed and periodic must resolve
  against the mutable active fork, so both deltas are < 1e-3 m.
- Repeat the decode after clearing the active marker and pre-Re-Fly
  snapshot (simulating post-commit playback). The same persisted
  `(dx, dy, dz)` must still decode to < 1e-3 m against the final active
  fork trajectory. This is the regression that rules out recording
  against a transient frozen overlay.
- Add the stronger post-commit equivalence test that the resolver claim
  depends on: drive the focused parent `FlightRecorder` and the
  background debris writer at lockstep UTs, then assert the parent
  recording's resolved surface pose at the debris sample UT matches the
  live parent transform used by `BackgroundRecorder` when it authored the
  child offset. The decode delta must stay below 1e-3 m, or Option B2's
  "post-commit playback uses the same frame" claim is only theoretical.

Capture log lines via `ParsekLog.TestSinkForTesting` per
`RewindLoggingTests.cs`. No Unity scene state required for the resolver
contract: build synthetic `RecordingTree`/`Recording` objects and use the
pure relative-offset seam for the payload write. The lockstep parent /
child test must exercise the actual focused-recorder and background
entry seams, not only injected `AnchorPose` mocks.

### 1b. Repro recipe (manual playtest)

For a sanity playtest after PR0 telemetry lands, and again after any PR1
behavior fix:

1. Launch a Kerbal X. Stage to separate booster + upper stage. Continue
   to apoapsis.
2. Return to Space Center, then Re-Fly the booster (in-place
   continuation). The upper stage is now a background-recording ghost.
3. During the Re-Fly, fire a side decoupler so the booster sheds debris
   while the upper-stage ghost is still being background-recorded
   (a shower of debris from the staging pyrotechnics).
4. Let the recording continue for 10-20 s, then exit Flight.
5. Watch the captured recording. Pre-fix: debris drifts visibly away
   from the parent ghost as Re-Fly time advances. Post-fix: debris
   stays attached to the parent ghost over the same span.

A bundled log via `python scripts/collect-logs.py refly-debris-frame
--save <career>` captures the new log keys for inspection.

### 1c. Runtime evidence (mandatory closeout)

Do not promise an automated `[InGameTest]` unless the harness can
actually script Re-Fly start, load-roundtrip, replay, and a distance
assertion. If such a harness exists, name it here and add the test under
`Source/Parsek/InGameTests/RuntimeTests.cs`. Otherwise, keep this as a
manual playtest requirement using §1b plus `collect-logs.py`. The closeout
evidence must include `KSP.log`, `parsek-test-results.txt` if an
automated runtime test exists, and the PR0/PR1 log keys. Do not describe
the change as fully runtime-verified without that evidence.

### 1d. Existing log bundles do not exercise the bug — gap acknowledgement

I mined three Re-Fly bundles for evidence the bug is reachable in the
wild today:

- `logs/2026-05-08_1739_rewind-launch-and-refly-probe-glitchy/KSP.log`
  has two distinct in-place Re-Fly sessions
  (`sess_4c55297d... slot=1 provisional=rec_929b...` at line 12876, and
  `sess_c82d0aac... slot=0 provisional=rec_07f2...` at line 67953).
  Both Re-Fly windows produced exactly ONE new recording each — the
  Re-Fly provisional itself (`Recording started: vessel="Kerbal X
  Probe"... rec_929b...` at line 14009; `Recording started: vessel="Kerbal
  X"... rec_07f2...` at line 69009). Neither Re-Fly attempt broke off
  debris. The pre-existing debris ghosts that do play during the Re-Fly
  (`recording #1..#5 "Kerbal X Debris"`) anchor to the original launch
  recording id `714e1923...`, not to the active Re-Fly recording id, so
  the resolver's `IsActiveReFlyRecordingId` check is false for them and
  the substitution does not fire.
- `logs/2026-05-09_1054_refly-stash-regression/` shows 49
  `RELATIVE sample.*source=Live` lines but zero
  `[Parsek][INFO][BgRecorder] RELATIVE mode entered (debris parent-anchor
  contract)` — meaning `ApplyDebrisAnchorContractToState`
  (`BackgroundRecorder.cs:4300`) never reached its current log line at
  `BackgroundRecorder.cs:4367-4373` for a debris recording during the
  captured Re-Fly window.
- `logs/2026-05-07_2251_refly-after-anchor-fix/KSP.log` has two more
  Re-Fly sessions with the same pattern: no debris-during-Re-Fly events.

**Conclusion:** the buggy combination is reachable by static inspection
but not exercised by any captured playtest. The bug is also internally
asymmetric: seed can be frozen via recorded fallback, while periodic
samples can be live via loaded-parent resolution. The code needs
telemetry before the behavior choice is locked in.

**PR0 telemetry split:** land a tiny recorder-telemetry-only PR before
the resolver behavior change. PR0 must add the recorder log keys below
and no resolver bypass, no focus-id behavior dependency, and no
frame-contract behavior change. PR1 Option B2 is the resolver
bypass/plumbing candidate and may proceed only if PR0 shows material
drift and the lockstep post-commit equivalence test passes. If PR0 does
not show material drift, choose Option C or make no behavior change.

Add the §6 PR0 recorder keys before any resolver behavior change. Those
keys turn the next captured Re-Fly bundle into a definitive answer about
whether the bug is reachable and visually material in the wild.

## 2. Root-cause analysis

### Recorder side (writes `(dx, dy, dz)`)

`BackgroundRecorder.UpdateBackgroundAnchorDetection`
(`Source/Parsek/BackgroundRecorder.cs:3986`) short-circuits at lines
3999-4003 for v12 debris and calls
`ApplyDebrisAnchorContractToState`
(`BackgroundRecorder.cs:4300-4379`). At lines 4318-4320 it resolves
the parent vessel by pid. If loaded, lines 4322-4334 author a `Live`
`RecordingAnchorCandidate` with `WorldPos = parentVessel.GetWorldPos3D()`
and `WorldRotation = parentVessel.transform.rotation`.

Per-sample,
`ApplyBackgroundRelativeOffset`
(`BackgroundRecorder.cs:4402`) has the second v12 debris contract
short-circuit at lines 4419-4422, which re-applies
`ApplyDebrisAnchorContractToState` before current-anchor resolution.
Then it calls `TryResolveBackgroundCurrentAnchorPose`
(line 4553) -> `TryResolveBackgroundAnchorPoseForCandidate`
(line 4579). For `Source = Live` with a loaded vessel, lines
4593-4614 return `AnchorPose(liveAnchor.transform.position,
liveAnchor.transform.rotation, ...)` — the **diverged Re-Fly parent
surface-pose frame**. This later transform-position resolution is the
actual frame used for the relative payload; the initial candidate's
`GetWorldPos3D()` is only the candidate-position seed.

`ApplyBackgroundRelativeOffsetForAnchorPose`
(line 4494) then writes `(dx, dy, dz)` via
`TrajectoryMath.ComputeRelativeLocalOffset(focusWorldPos,
anchorPose.WorldPos, anchorPose.WorldRotation)` into
`point.latitude/longitude/altitude`. For periodic samples, that offset
is in the **live diverged frame**.

The initial debris seed path is different. `InitializeLoadedState`
starts with a live pose, but then prefers queued seed pose
(`BackgroundRecorder.cs:3174-3188`) and recorded fallback
(`BackgroundRecorder.cs:3189-3197`) before using the live warn fallback
(`BackgroundRecorder.cs:3198-3208`). Today that recorded fallback calls
`TryResolveBackgroundRecordedAnchorPose` with `focusRecordingId: null`
(`BackgroundRecorder.cs:4641-4644`), so during active Re-Fly it can
resolve the parent through the frozen pre-Re-Fly overlay. Then line 3213
writes the seed payload through the same relative writer. Net effect:
pre-fix seed can be frozen-frame while later periodic samples are
live-frame.

The structural-event seam at line 6327 is a call-through to
`ApplyBackgroundRelativeOffset`, not a separate math implementation, so
it follows the periodic frame decision.

### Playback side (resolves `(dx, dy, dz)`)

For Relative-section playback,
`ParsekFlight.TryResolveRelativeOffsetWorldPosition` calls
`RelativeAnchorResolver.TryResolveAnchorPose` against
`section.anchorRecordingId`. The resolver runs
`TryResolveActiveReFlyAnchorRecording`
(`RelativeAnchorResolver.cs:1289-1320`): when the id matches
`marker.ActiveReFlyRecordingId` AND the recording has a captured
pre-Re-Fly anchor trajectory, it substitutes
`recording.BuildPreReFlyAnchorTrajectoryRecording(sessionId)` and the
resolver returns a pose in the **pre-Re-Fly frame**.

Important gate detail: this resolver path does **not** require
`ReFlySessionMarker.IsInPlaceContinuation(marker)`. Other focused-vessel
helper paths do, but Relative-section playback is governed by
`TryResolveActiveReFlyAnchorRecording`, so the debris-specific playback
bypass must not add an in-place-only gate either.

Durability detail: the frozen substitution only exists while the active
Re-Fly marker and the captured pre-Re-Fly snapshot exist. Normal
supersede commit clears both (`SupersedeCommit.cs:353-367`). Once the
session is committed, the same `anchorRecordingId` resolves against the
final active fork trajectory. That means the durable recording contract
for Re-Fly-born debris is the active fork path, not the frozen overlay.

The focused-vessel mirror at
`ParsekFlight.cs:21045-21069` (`ShouldUsePreReFlyAnchorTrajectory`) +
`ParsekFlight.cs:20845-20884` (`TryResolvePreReFlyFrozenAnchorPose`)
applies the same substitution for non-resolver call sites.

### Periodic-sample frame diagram

```
Recording time:                Playback time:
==================             ==================

PARENT live transform          PARENT frozen trajectory
(diverged Re-Fly path)         (pre-Re-Fly path)
        |                              |
        v                              v
 anchorPose.WorldPos =          resolved.WorldPos =
   live transform.position        frozen.GetWorldSurfacePosition(...)
        |                              |
        v                              v
recorded(dx,dy,dz) =            world(replay) =
  Inv(live.rot)*(focus -         resolved.rot * recorded(dx,dy,dz)
                live.pos)         + resolved.pos

      LIVE FRAME                    FROZEN FRAME
           ^                             ^
           |                             |
           +--- frame mismatch ----------+
                  (delta = live - frozen)
```

For periodic samples, the two arrows for the recorder vs the playback
path go through **different anchor poses** but the same `(dx, dy, dz)`
payload. The result is wrong by exactly the live-minus-frozen delta. The
seed path is different: it can already use the frozen recorded fallback,
so its pre-fix delta may be near zero while the next periodic point jumps
to the live-frame contract.

### Reachability and field evidence

The bug-trigger condition is narrow:

1. A Re-Fly session marker is active, the debris parent recording id
   matches `marker.ActiveReFlyRecordingId`, and that active recording has
   a captured pre-Re-Fly anchor trajectory. The usual modern producer is
   an in-place Re-Fly continuation, but the playback resolver does not
   check `InPlaceContinuation`.
2. A new debris recording is started during that Re-Fly window — i.e.
   the Re-Fly vessel itself sheds debris while it is being re-flown.
3. That debris is NOT the focused vessel (so `BackgroundRecorder` owns
   the recording, not `FlightRecorder`).
4. The parent vessel (the live diverged Re-Fly vessel) is loaded at
   sample time, so `ApplyDebrisAnchorContractToState` takes the
   `parentVessel.loaded` branch.
5. The write is one of the background recorder relative-write seams:
   initial seed, ordinary periodic sample, or structural-event sample.

All five conditions overlap only when a Re-Fly attempt itself spawns
loose pieces. None of the three captured Re-Fly bundles in `logs/`
reached this — see §1d.

### Why the existing v12 ApplyDebrisAnchorContractToState path doesn't already prevent this

The contract intends to **pin the anchor recording id** to the parent
recording so playback can resolve through the resolver chain
(`BackgroundRecorder.cs:4291-4299` doc comment is explicit about this).
For Re-Fly-born debris, the loaded-parent live branch is the durable
recording contract: the parent is the active fork whose final trajectory
will remain after commit. The misuse is on active-session playback:
`TryResolveActiveReFlyAnchorRecording` treats this new debris child the
same as pre-existing relative recordings and swaps its parent to the
temporary frozen pre-Re-Fly overlay. That overlay is correct for old
ghosts but wrong for new debris born from the fork.

### Why the `BackgroundRecorder.cs:4242-4248` Re-Fly exclusion no longer matters here

The exclusion at `TryGetBackgroundEligibleAnchorRecording` only fires
inside the candidate-list path
(`UpdateBackgroundAnchorDetection -> BuildBackgroundRecordingAnchorCandidates`)
and recorded-anchor fallback searches. For v12 debris, both real call
paths bypass it before candidate-list logic: ordinary detection
short-circuits at `BackgroundRecorder.cs:3999-4003`, and the
sample/structural path re-applies the debris contract at
`BackgroundRecorder.cs:4419-4422` before recorded-anchor resolution.
The exclusion remains live for legacy v11 debris (which had no
`DebrisParentRecordingId` and used the candidate-list path) and for
non-debris BG recordings. Removing it now is cosmetic for v12 and
doesn't fix the frame-mismatch bug; see §9.

## 3. Fix design — PR0 first, then choose PR1

Do not land a resolver behavior change as the first patch. PR0 measures
the actual seed/periodic frame split with recorder telemetry only. PR1
chooses between Option B2 and Option C based on that data plus the
lockstep equivalence tests.

### Option A: recorder writes against the frozen pre-Re-Fly anchor pose

Make the loaded-parent debris path stop treating an active Re-Fly parent
as a live anchor. When the debris parent recording id matches the active
Re-Fly recording id, route recorder pose resolution through the same
`RelativeAnchorResolver` recorded-anchor path that active-session
playback uses, so the recorder writes `(dx, dy, dz)` in the frozen
pre-Re-Fly frame.

**Pros:**

- Active-session recorder and playback agree while the marker is alive.

**Cons / edge cases:**

- Rejected after second review: the frozen substitution is transient.
  Supersede commit clears the active marker and pre-Re-Fly snapshots
  (`SupersedeCommit.cs:353-367`). A saved child section would persist
  only `anchorRecordingId`, so post-commit playback would decode the
  frozen-frame payload against the final active fork trajectory. That is
  the same frame mismatch, just delayed until normal committed replay.

### Option B2 (PR1 candidate): playback resolves Re-Fly-born debris against the active fork path

Add a v12-specific gate inside
`RelativeAnchorResolver.TryResolveActiveReFlyAnchorRecording` that skips
the frozen substitution when the **requesting/focus recording** is debris
whose `DebrisParentRecordingId == marker.ActiveReFlyRecordingId` and the
anchor being resolved is that same active Re-Fly recording. The gate
only runs inside the existing captured-pre-Re-Fly-snapshot branch, so an
active provisional with no snapshot keeps today's unresolved behavior.

This makes the durable contract explicit: Re-Fly-born debris is relative
to the mutable active fork, not the temporary frozen overlay. During
active-session playback, the resolver uses the mutable active fork
recording for that child, matching what post-commit playback will do
once the marker is cleared. Pre-existing recordings and non-debris focus
recordings keep the frozen pre-Re-Fly substitution.

**Pros:**

- Durable if the lockstep parent/debris test passes: the same persisted
  `(dx, dy, dz)` decodes correctly both while the active marker is alive
  and after commit clears it.
- Save format unchanged; semantics of `(dx, dy, dz)` unchanged.
- Scope is narrow and data-driven: only v12+ debris whose saved
  `DebrisParentRecordingId` names the active Re-Fly recording gets the
  mutable-fork interpretation.

**Cons:**

- The resolver now has a focus-recording-dependent exception. Mitigation:
  centralize it in one helper with explicit logs/tests, and require all
  resolver contexts that can resolve debris relative sections to pass
  `focusRecordingId`.
- It must repair seed and periodic together. A seed-only frozen fallback
  plus live periodic samples is already today's inconsistency; a PR1 B2
  implementation that only fixes periodic would preserve the bug in a
  different shape.
- During the active Re-Fly session, pre-existing ghosts still freeze to
  the pre-Re-Fly path while newly-created debris follows the active fork.
  That split is intentional: the debris is a product of the new Re-Fly
  attempt and must remain consistent with the fork that will be
  committed.

### Option C: recorder emits an Absolute section for v12 Re-Fly-born debris

Force `state.currentTrackSection.referenceFrame = ReferenceFrame.Absolute`
in `ApplyDebrisAnchorContractToState` whenever the marker is active and
the parent is the active Re-Fly recording, dropping the v12 contract's
"always Relative for debris's lifetime" promise for this slice.

**Pros:**

- No anchor-pose dependency at all; debris floats in body-fixed
  planet-relative space and therefore cannot be decoded through the
  wrong parent frame.
- This engages the prior reviewer's point: during active Re-Fly, the
  recorded parent path visible to generic recorded-anchor resolution is
  the frozen path, so refusing to use that parent and falling back to
  Absolute is internally consistent.
- There is existing precedent in the current code. The residual Re-Fly
  exclusion at `BackgroundRecorder.cs:4242-4248` already creates
  Option-C-like semantics for candidate-list paths: when the active
  Re-Fly recording is excluded, no relative parent is chosen and the
  background point falls back toward Absolute/non-relative handling.

**Cons:**

- Loses the metre-scale precision the v12 debris contract was
  introduced for (`PR 3b`). Re-Fly-born debris would render in
  body-fixed lat/lon/alt with no anchor binding, which is the very
  design rejected in `docs/dev/research/recording-system-design.md`
  for fast-moving, anchor-relative debris.
- It fixes the active-session mismatch by abandoning parent-relative
  semantics for the bug slice rather than making the durable v12
  contract coherent. If the parent recorder's post-commit surface pose
  matches the live transform used by the debris writer, Option B2 keeps
  both active-session and committed playback parent-relative. Option C
  gives that up for every Re-Fly-born debris point.
- The visual cost is currently unquantified. Add
  `parentDriftFromRecorded=<m>` telemetry (§1d, §6) so the next playtest
  measures whether the relative contract is buying visible fidelity or
  whether the drift is below a practical threshold.

Data-gated. If PR0 shows `parentDriftFromRecorded < 1 m` for every seed
and periodic sample in the live playtest, choose Option C for PR1 or make
no behavior change; do not land B2. B2 may proceed only when PR0 shows
material drift, including in any docking/proximity scenario claimed to
need sub-metre parent-relative fidelity, and the lockstep post-commit
equivalence test passes. If that test fails or the context plumbing
becomes too broad, choose Option C or no PR1 behavior change.

## 4. Implementation steps

### 4.0 PR0 recorder telemetry only

Edits are limited to `Source/Parsek/BackgroundRecorder.cs` logging and
diagnostic classification. Do not change resolver selection, do not add
the mutable-fork bypass, and do not change recorded payloads.

PR0 must distinguish:

- seed recorded fallback (`BackgroundRecorder.cs:3189-3197`) that can
  resolve frozen today;
- seed live warn fallback (`BackgroundRecorder.cs:3198-3208`);
- periodic loaded-parent live samples (`BackgroundRecorder.cs:4402`,
  `4593-4614`);
- structural-event call-through
  (`BackgroundRecorder.cs:6327` directly calls
  `ApplyBackgroundRelativeOffset(state, ref point, v, eventUT)`).

It must log enough to decide PR1; §6 is the canonical logging spec.

### 4.1 PR1 Option B2 resolver/context implementation

If PR0 justifies Option B2, edits are in
`Source/Parsek/RelativeAnchorResolver.cs` plus small context plumbing in
recorder/playback callers that build `RelativeAnchorResolverContext`.
The recorder's loaded-parent periodic Relative write remains live-frame;
the seed recorded fallback must be made mutable-active-fork too, or the
seed/periodic asymmetry remains.

### 4a. Add a focus-recording lookup helper to the resolver

Add a private helper in `RelativeAnchorResolver`:

```
private static bool ShouldResolveActiveReFlyAnchorThroughMutableFork(
    RelativeAnchorResolverContext context,
    string anchorRecordingId,
    Recording anchorRecording,
    out Recording focusRecording);
```

Behaviour:

- Require an active marker with
  `anchorRecordingId == context.ActiveReFlyMarker.ActiveReFlyRecordingId`.
- Require
  `anchorRecording.HasPreReFlyAnchorTrajectory(context.ActiveReFlyMarker.SessionId)`
  to be true. This bypass only replaces the existing frozen-substitution
  branch; it must not broaden the current no-snapshot behavior where an
  active Re-Fly anchor returns unresolved (`active-provisional-out-of-scope`).
- Resolve the focus recording by `context.FocusRecordingId` using the same
  tree-scope rules as anchor lookup: check `context.FocusTree` first;
  check `context.ProvisionalRecordings` only for the matching id; check
  `context.PendingTree` only when `PendingTreeIsInScope(context)` would
  return true (`RelativeAnchorResolver.cs:1273-1287`). If the focus id is
  missing or found only in an out-of-scope pending tree, return false and
  keep the frozen branch.
- Require `focusRecording.DebrisParentRecordingId == anchorRecordingId`
  and `focusRecording.RecordingFormatVersion >= RecordingStore.DebrisParentRecordingFormatVersion`.
- Do **not** require `ReFlySessionMarker.IsInPlaceContinuation(marker)`;
  the current frozen resolver gate does not require it, and this bypass
  must cover the same active-marker shape.

### 4b. Gate `TryResolveActiveReFlyAnchorRecording`

In `TryResolveActiveReFlyAnchorRecording`, after
`IsActiveReFlyRecordingId(context, recordingId)` succeeds and after the
existing `recording.HasPreReFlyAnchorTrajectory(sessionId)` check passes,
call the helper before `BuildPreReFlyAnchorTrajectoryRecording`:

- If it returns true, leave `resolvedRecording = recording` and return
  true. Log `active-refly-anchor-mutable-fork` with
  `focusRecordingId`, `anchorRecordingId`, `debrisParentRecordingId`,
  `sessionId`, and UT.
- Otherwise keep the existing frozen substitution unchanged.
- If the active recording has no captured pre-Re-Fly snapshot, keep the
  current `return false` behavior unchanged. Do not let v12 debris
  bypass resolve a no-snapshot active provisional anchor.

This makes only Re-Fly-born debris resolve through the active fork while
pre-existing relative recordings keep the frozen pre-Re-Fly overlay.

### 4c. Ensure resolver contexts carry the focus recording id

The bypass depends on `context.FocusRecordingId`, so every call path that
can resolve these debris sections must provide it:

- Audit every construction site and every wrapper that feeds one. The
  current grep surface is:
  - `BackgroundRecorder.cs:4641` direct constructor, currently
    `focusRecordingId: null` and must be fixed.
  - `FlightRecorder.cs:7534`
    (`BuildRecorderRelativeAnchorResolverContext`), already tied to the
    active focused recording but still needs an audit test proving it is
    not a v12 debris playback path.
  - `ParsekFlight.cs:15167`
    (`BuildFlightRelativeAnchorResolverContext`), fed by the direct
    flight playback call at `ParsekFlight.cs:15084` and wrappers at
    `ParsekFlight.cs:17584`, `ParsekFlight.cs:22081`, and
    `ParsekFlight.cs:22152`. The `15084` path already passes
    `focusedRecordingId` and skips the active provisional candidate at
    `ParsekFlight.cs:15103-15112`; name it in the audit so the skip is
    an explicit decision, not an accidental omission.
  - `RecordedRelativeAnchorPoseResolver.cs:57`, already passes the focus
    recording id.
  - `Rendering/ProductionAnchorWorldFrameResolver.cs:405`, already
    passes the focus recording id.
- `BackgroundRecorder.TryResolveBackgroundRecordedAnchorPose` currently
  builds the context with `focusRecordingId: null`
  (`BackgroundRecorder.cs:4641-4644`). Add a `focusRecordingId`
  parameter to that helper. Periodic/current-anchor fallbacks pass
  `state.recordingId`. The seed recorded-fallback call at
  `BackgroundRecorder.cs:3189-3197` must pass the local
  `InitializeLoadedState` `recordingId` parameter explicitly and assert
  or log if it is null/empty before building the context. This matters
  because a missing seed focus id would keep the seed frozen while
  periodic samples become mutable-active-fork.
- Extract the context construction into an internal helper such as
  `BuildBackgroundRelativeAnchorResolverContext(string focusRecordingId)`
  so xUnit can assert the background recorded-anchor fallback really
  carries the focus id. `TryResolveBackgroundRecordedAnchorPose` should
  call this helper instead of constructing the context inline.
- Add a source/audit xUnit such as
  `EveryResolverContextProduction_CarriesFocusRecordingId_ForV12DebrisPaths`
  that fails if a new `RelativeAnchorResolverContext` constructor or
  wrapper is added without an explicit focus-id decision. The test does
  not need to ban `null` globally; it must document why any remaining
  null context cannot resolve v12 Re-Fly-born debris sections.

### 4d. Add a pure relative payload seam for xUnit

Extract the core of `ApplyBackgroundRelativeOffsetForAnchorPose` that
does not need a live `Vessel` into an internal static helper, for
example:

```
internal static bool TryWriteRelativePayloadForWorldPose(
    ref TrajectoryPoint point,
    Vector3d focusWorldPos,
    Quaternion focusWorldRotation,
    AnchorPose anchorPose,
    int recordingFormatVersion);
```

The existing vessel-based method calls this helper after resolving
`focusWorldPos` / `focusWorldRotation`. Tests can now build synthetic
world poses and verify seed/periodic/structural payloads without Unity
scene state or reflection over private `BackgroundVesselState`.

Do not stop at pure-helper tests. Expose the narrowest internal seams
needed to drive the three real background entry points under xUnit or
in-game test harnesses: initial seed (`BackgroundRecorder.cs:3213`),
periodic sample (`BackgroundRecorder.cs:4402`), and structural event
(`BackgroundRecorder.cs:6327`, call-through to periodic). A regression
where one site stops supplying the right `AnchorPose` must fail tests.

### 4e. Logging

PR0 recorder logs are defined only in §6. Do not duplicate the key list
here.

PR1 Option B2 resolver logs, if B2 is selected:

- Keep the existing `active-refly-anchor-frozen` log for pre-existing
  recordings.
- Add `active-refly-anchor-mutable-fork` in `RelativeAnchorResolver` when
  the debris-specific bypass fires.

### 4.2 PR1 Option C recorder implementation

If PR0 points to Option C, keep the resolver untouched and implement a
recorder-only gate:

```
internal static bool ShouldForceActiveReFlyDebrisAbsolute(
    Recording focusRecording,
    ReFlySessionMarker marker);
```

Behaviour:

- Require v12+ debris:
  `focusRecording.RecordingFormatVersion >= RecordingStore.DebrisParentRecordingFormatVersion`
  and `focusRecording.DebrisParentRecordingId != null`.
- Require an active marker whose `ActiveReFlyRecordingId` equals
  `focusRecording.DebrisParentRecordingId`.
- Do not require a captured pre-Re-Fly snapshot; Option C avoids the
  resolver and records body-fixed Absolute points for active-Re-Fly-born
  debris regardless of frozen-overlay availability.

Apply the gate consistently:

- Seed: in `InitializeLoadedState`, before the recorded-fallback branch at
  `BackgroundRecorder.cs:3189`, start/keep an Absolute section for the
  initial point, skip `TryResolveBackgroundRecordedAnchorPose`, and skip
  `ApplyBackgroundRelativeOffsetForAnchorPose`.
- Periodic: in `ApplyBackgroundRelativeOffset`
  (`BackgroundRecorder.cs:4402`), return false early for this gate so the
  ordinary absolute point is retained and no Relative section is opened.
- Structural: no separate math path; `BackgroundRecorder.cs:6327`
  directly calls `ApplyBackgroundRelativeOffset(state, ref point, v, eventUT)`,
  so it inherits the periodic early return.
- Logging: emit `frameContract=absolute-refly` with
  `parentDriftFromRecorded` when resolvable, so PR1 C evidence remains
  comparable to PR0.

PR1 C tests:

- `OptionC_ReFlyDebrisSeedPeriodicStructural_WriteAbsolute_WhenSelected`
  — assert seed, periodic, and structural entry points keep/write
  Absolute points and do not call the relative payload writer for
  active-Re-Fly-born v12 debris.
- `OptionC_DoesNotAffectPreExistingRelativeOrNonDebrisRecordings` —
  assert pre-existing relative ghosts, non-debris background recordings,
  and v12 debris whose parent does not match the active Re-Fly id keep
  existing behavior.
- `OptionC_DoesNotRequireResolverBypassOrFocusContextPlumbing` — assert
  no `active-refly-anchor-mutable-fork` log is emitted and existing
  frozen/unresolved resolver behavior is unchanged when C is selected.
- `OptionC_AbsoluteWritesSurviveDiscardOfActiveReFly_AndStillReplayCleanly`
  — record active-Re-Fly-born debris through C, then cover discard paths
  that clear the marker (`MergeDialog.cs:874-879`,
  `RevertInterceptor.cs:270, 367, 427`, `TreeDiscardPurge.cs:471-480`)
  and assert the Absolute payload replays without needing the discarded
  active Re-Fly marker or mutable-fork resolver bypass.

### 4f. Save-format impact

PR0 has no save-format impact. PR1 Option B2 keeps
`TrackSection.referenceFrame = Relative`, keeps
`TrackSection.anchorRecordingId` pointing to the parent active-fork
recording, and keeps `(dx, dy, dz)` v6+ semantics. PR1 Option C would
write `ReferenceFrame.Absolute` for this narrow slice but still needs no
codec or format-version bump.

## 5. Test plan

xUnit (added in `Source/Parsek.Tests/`):

- `ReFlyDebrisFrameMismatchTests.cs`, [Collection("Sequential")]:
  - `PreFixSeedAndPeriodicUseDifferentFrames_DuringActiveReFly` —
    mainline reproduction from §1a. Pre-fix expectation: seed recorded
    fallback resolves frozen and reconstructs near the frozen pose, while
    periodic loaded-parent sampling is live and reconstructs wrong by
    approximately `live - frozen` under frozen playback.
  - `PR0RecorderTelemetry_ReportsSeedAndPeriodicFrameContracts` — no
    behavior change; assert seed logs include `seedAnchorSource`,
    `seedFocusRecordingId`, `seedRecordedFallbackFrameContract`, and
    periodic logs include `frameContract` plus
    `parentDriftFromRecorded`.
  - `OptionC_ReFlyDebrisSeedPeriodicStructural_WriteAbsolute_WhenSelected`
    — if PR0 selects Option C, assert seed, periodic, and structural
    entry points keep/write Absolute points and do not call the relative
    payload writer for active-Re-Fly-born v12 debris.
  - `OptionC_DoesNotAffectPreExistingRelativeOrNonDebrisRecordings` —
    assert pre-existing relative ghosts, non-debris background recordings,
    and v12 debris whose parent does not match the active Re-Fly id keep
    existing behavior.
  - `OptionC_DoesNotRequireResolverBypassOrFocusContextPlumbing` —
    assert no `active-refly-anchor-mutable-fork` log is emitted and
    existing frozen/unresolved resolver behavior is unchanged when C is
    selected.
  - `OptionC_AbsoluteWritesSurviveDiscardOfActiveReFly_AndStillReplayCleanly`
    — assert C-authored Absolute seed/periodic/structural points replay
    cleanly after active Re-Fly discard/cleanup clears the marker.
  - The remaining mutable-fork tests are B2-only. Do not require them if
    PR1 selects Option C.
  - `ActiveMarker_ReFlyDebrisBypassesFrozenAnchor_UsesMutableFork` —
    PR1 B2-only mainline test. Post-fix, both seed and periodic samples
    decode below 1e-3 m because playback resolves the parent through the
    mutable active fork. Skip this test path entirely if PR1 selects
    Option C.
  - `MarkerCleared_PostCommitReplayStillMatchesMutableFork` — clear the
    active marker and pre-Re-Fly snapshot after writing the same payload.
    Assert decode still matches the final fork path. This pins the
    durability issue that rejects Option A.
  - `FocusedParentAndBackgroundDebris_Lockstep_PostCommitDecodeDeltaUnderTolerance`
    — drive the focused parent recorder and background debris writer at
    the same UTs, then clear the marker and decode the child against the
    parent recording's final surface pose. Assert delta < 1e-3 m. This is
    the core correctness proof for Option B2, not an optional nice-to-have.
  - `PreExistingRelativeRecording_StillUsesFrozenAnchor` — focus
    recording is not v12 debris with `DebrisParentRecordingId` matching
    the active id. Assert existing frozen substitution and
    `active-refly-anchor-frozen` logging remain unchanged.
  - `NotInPlaceContinuation_ReFlyDebrisStillBypassesFrozenAnchor` —
    marker present, active id matches parent, captured snapshot exists,
    but `InPlaceContinuation = false`. Assert the debris-specific bypass
    still fires because the frozen resolver gate would otherwise fire too.
  - `DebrisParentMismatch_DoesNotBypassFrozenAnchor` — focus recording is
    debris but its `DebrisParentRecordingId` names some other recording.
    Assert the active Re-Fly anchor remains frozen.
  - `ReFlyDebrisWithoutPreReFlySnapshot_RemainsUnresolved` — active
    marker and v12 debris parent match, but the anchor recording has no
    captured pre-Re-Fly snapshot. Assert the resolver preserves existing
    no-snapshot behavior and returns unresolved instead of resolving the
    active provisional through the mutable-fork bypass.
  - `PureRelativePayloadSeedPeriodicStructural_UseSameMath` — drive the
    new pure payload helper with representative seed, periodic, and
    structural points so all three seams write identical `(dx, dy, dz)`
    for the same focus/anchor world poses.
  - `BackgroundSeedEntryPoint_UsesMutableActiveReFlyFrame_WhenB2Selected` — drive the
    loaded-state seed path through its real entry seam
    (`BackgroundRecorder.cs:3189-3213`) and assert it passes the local
    `recordingId` into recorded fallback and supplies the mutable
    active-fork anchor pose, not only the pure helper.
  - `BackgroundPeriodicEntryPoint_UsesMutableActiveReFlyFrame_WhenB2Selected` — drive
    `ApplyBackgroundRelativeOffset` (`BackgroundRecorder.cs:4402`) and
    assert the v12 debris short-circuit at `BackgroundRecorder.cs:4419-4422`
    runs before recorded/candidate fallback.
  - `BackgroundStructuralEventEntryPoint_RoutesThroughPeriodicMutableFrame`
    — drive the structural-event call-through at
    `BackgroundRecorder.cs:6327` and assert it reaches the same periodic
    writer/contract path.
  - `BackgroundRecordedAnchorContext_CarriesFocusRecordingId_ForMutableForkBypass`
    — B2-only. Build a synthetic tree with active fork parent + v12
    debris child, call the internal background context helper with the
    child's recording id, and assert `RelativeAnchorResolver` takes
    `active-refly-anchor-mutable-fork` rather than
    `active-refly-anchor-frozen`. Cover both recorded fallback call sites
    that matter: seed fallback (`BackgroundRecorder.cs:3189`) and current
    anchor fallback (`BackgroundRecorder.cs:4572-4624`) by routing them
    through the same context helper.
  - `EveryResolverContextProduction_CarriesFocusRecordingId_ForV12DebrisPaths`
    — audit the direct constructors/wrappers listed in §4c and fail when
    a resolver context that can decode v12 debris sections omits the
    focus recording id.
  - `MarkerLifecycle_CommitDiscardRevertAndLoadSweep_DisableMutableForkBypass`
    — assert the bypass only applies while the active marker is alive.
    Cover supersede commit (`SupersedeCommit.cs:353-367`), merge-dialog
    discard (`MergeDialog.cs:874-879`), revert/discard cleanup
    (`RevertInterceptor.cs:270, 367, 427`), merge-journal cleanup
    (`MergeJournalOrchestrator.cs:241-251, 382-390, 426-433`), and
    tree discard / load-time invalid/zombie cleanup
    (`TreeDiscardPurge.cs:471-480`, `LoadTimeSweep.cs:78, 434`).
- `DebrisParentAnchorContractTests.cs` (existing): add only lightweight
  tests for the pure payload helper or logging labels if they naturally
  fit. Do not pretend the private live-vessel seed path is xUnit-covered
  without the pure seam.

Runtime closeout:

- Automated in-game test only if the existing runtime harness can script
  Re-Fly start, load-roundtrip, replay, and a distance assertion. If it
  can, add `[InGameTest(Category = "ReFlyDebris", Scene = GameScenes.FLIGHT)]`
  in `Source/Parsek/InGameTests/RuntimeTests.cs`.
- Otherwise, require the manual §1b playtest and collected bundle. The
  exported evidence should include seed and periodic `frameContract`,
  `parentDriftFromRecorded=<m>`, and, for PR1 B2 only,
  `active-refly-anchor-mutable-fork`.

## 6. Logging requirements

Per CLAUDE.md, every state transition + guard skip MUST be logged.

PR0 recorder keys:

- `frameContract=live|recorded|frozen-refly` — PR0 diagnostic
  classification only. `mutable-active-refly` is reachable only in PR1
  Option B2; `absolute-refly` is reachable only in PR1 Option C.
- `parentDriftFromRecorded=<m>|(unresolved)` — emitted for seed and
  periodic samples only when `markerActive=true` and the debris parent
  recording id matches `marker.ActiveReFlyRecordingId`. Compute it only
  when the active Re-Fly parent live pose and recorded pose can both be
  resolved at the same UT; otherwise emit `(unresolved)`. Use
  `VerboseRateLimited` for periodic samples because this is per-frame
  telemetry.
- `seedAnchorSource=queued-parent-seed|recorded|live-warn-fallback`,
  `seedFocusRecordingId=<id>|(missing)`, and
  `seedRecordedFallbackFrameContract=frozen-refly|recorded|unresolved`.
  Use `Verbose` for the one-shot seed log.

PR1 Option B2 resolver keys, only if B2 is selected:

- `active-refly-anchor-mutable-fork|<focusRecordingId>|<anchorRecordingId>`
  in subsystem `RelativeAnchorResolver` — `VerboseRateLimited`, emitted
  when the debris-specific bypass skips the frozen substitution. Fields:
  focus recording id, anchor recording id, debris parent id, session id,
  UT, and focus tree id.
- `active-refly-anchor-frozen` remains unchanged for pre-existing
  recordings and non-matching debris. It is already per-resolver and
  should remain `VerboseRateLimited`. Tests must assert the new bypass
  does not suppress this log outside the v12 Re-Fly-born debris case.

The existing `RELATIVE mode entered (debris parent-anchor contract)`
function starts at `BackgroundRecorder.cs:4300`; its current log line at
`BackgroundRecorder.cs:4367-4373` gains three extra fields (per §1d's
reachability ask):

- `mutableActiveReFlyAnchor=true|false` — whether the recording is on
  the durable active-fork contract.
- `markerActive=true|false` — whether a Re-Fly session marker is live
  at the moment the contract was applied.
- `markerActiveReFlyId=<id>|(none)` — the active Re-Fly recording id,
  so a parent-id match against this debris's parent recording is
  visible in one log line.

The existing `bg-relative-offset` line at
`BackgroundRecorder.cs:4539-4547` gains PR0 fields and remains
`VerboseRateLimited`:

- `frameContract=live|recorded|frozen-refly` —
  which anchor-pose source produced the `(dx, dy, dz)`. This is the
  single most-useful tag for triage if a future Re-Fly playtest visibly
  shows debris drift.
- `parentDriftFromRecorded=<m>|(unresolved)` under the marker/id-match
  guard above, so a playtest can tell whether those frames materially
  differ regardless of the chosen `frameContract`.

The initial debris seed log at `BackgroundRecorder.cs:3252-3257` gains
the same `frameContract`, `parentDriftFromRecorded`, and seed-source
fields so the first point and later samples can be compared directly.
This is a one-shot log and should use `Verbose`.

The resolver's `active-refly-anchor-mutable-fork` and
`active-refly-anchor-frozen` lines should pair with recorder
`frameContract=mutable-active-refly` / `frameContract=frozen-refly`
labels during PR1 B2 active-session playtests.

## 7. Documentation updates

- PR0 telemetry-only: no user-facing `CHANGELOG.md` bug-fix claim unless
  the project wants an internal diagnostics note. The behavior has not
  changed yet.
- PR1 behavior fix (`CHANGELOG.md` under `0.9.2 -> Bug Fixes`): one line
  in the project's user-facing tone. Suggested B2 wording: "Re-Fly-born
  debris now stays attached to its parent on replay. Seed and periodic
  debris samples now use one active-fork frame instead of mixing the
  frozen pre-Re-Fly seed with live periodic offsets."
- `docs/dev/todo-and-known-bugs.md`: add a new `## Done -` block under
  the v0.9.2 cycle and replace the stale "Suspected fix / work queue"
  item that currently says to make the active Re-Fly recording usable as
  a live anchor by dropping the recorder exclusion. Use this exact
  replacement bullet so the doc does not churn in review:

  ```markdown
  - Re-Fly-born v12 debris now uses one frame contract for seed and periodic samples. PR0 added recorder telemetry for seed/periodic frameContract and parentDriftFromRecorded. PR1 keeps the parent-relative contract through a resolver-side mutable-active-fork bypass only when PR0 data justifies B2: the active Re-Fly anchor has a captured pre-Re-Fly snapshot and the focus recording is v12 debris whose DebrisParentRecordingId is the active Re-Fly recording. The fix also threads focusRecordingId through background recorded-anchor fallback, adds a pure relative payload seam, and covers the behavior in ReFlyDebrisFrameMismatchTests. See RelativeAnchorResolver.cs:1289-1320, BackgroundRecorder.cs:3152-3222, and BackgroundRecorder.cs:4641-4649.
  ```
- `AGENTS.md` and `.claude/CLAUDE.md`: add this sentence to the
  "Rotation / world frame" block in both files:

  ```markdown
  Format-v12 Re-Fly-born debris has a data-gated active-Re-Fly exception: if PR0 telemetry justifies preserving the parent-relative contract, then while the marker is alive and the active Re-Fly anchor has a captured pre-Re-Fly snapshot, resolver contexts with a v12 debris focus recording whose DebrisParentRecordingId is the active Re-Fly id must resolve that parent through the mutable active fork, matching the post-commit contract.
  ```

## 8. Risk and rollback

**What could go wrong:**

- PR0 shows seed/periodic drift is below the visible threshold, making
  Option B2 unnecessary complexity. Mitigation: explicitly re-evaluate
  Option C before PR1; do not treat B2 as pre-decided.
- PR0 never triggers the debris-during-Re-Fly path in live playtests.
  Stop condition: if no retained playtest hits
  `markerActive=true` + `parentRecId == markerActiveReFlyId` within two
  weeks or three targeted §1b attempts, use the synthetic xUnit
  seed/periodic drift reproduction as the proxy data for PR1 selection,
  and record that live reachability remains unproven.
- The mutable-fork bypass is too broad and makes pre-existing ghosts
  follow the live Re-Fly path. Mitigation: gate strictly on the focus
  recording's `DebrisParentRecordingId == anchorRecordingId`, and test
  pre-existing/non-debris/mismatched-debris cases.
- A resolver context omits `focusRecordingId`, so the bypass fails in
  one playback surface. Mitigation: audit every
  `RelativeAnchorResolverContext` construction and add tests for flight,
  recorded resolver, production world-frame resolver, and background
  recorded fallback; the named background regression test is
  `BackgroundRecordedAnchorContext_CarriesFocusRecordingId_ForMutableForkBypass`.
- The mutable-fork bypass could accidentally make no-snapshot active
  provisional anchors resolvable. Mitigation: require the same captured
  pre-Re-Fly snapshot that the frozen branch requires today, and add
  `ReFlyDebrisWithoutPreReFlySnapshot_RemainsUnresolved`.
- The parent recording's resolved surface pose may not exactly match the
  live transform used by the background debris writer at the same UT.
  That would break Option B2's post-commit claim even if active-session
  playback looks right. Mitigation: the lockstep
  `FocusedParentAndBackgroundDebris_Lockstep_PostCommitDecodeDeltaUnderTolerance`
  test is blocking, and `parentDriftFromRecorded=<m>` telemetry must be
  reviewed in the first playtest bundle that hits the path.
- The active fork recording may not yet have a sample covering the
  requested UT during the same frame a debris child is created. Existing
  resolver gap/continuation handling still applies; if it cannot resolve
  the active fork, playback should fail visibly with existing
  `relative-anchor-unresolved` logging rather than silently using the
  frozen overlay. The runtime closeout should cover the first-frame seed.

**Save-format impact:**

PR0 has none. PR1 Option B2 has none: Re-Fly-born debris samples stay
Relative with the same `(dx, dy, dz)` semantics; active-session playback
resolves their parent through the mutable active fork, which is the same
trajectory post-commit playback resolves after marker cleanup. If PR1
chooses Option C, save format still does not bump, but the frame contract
for this slice changes to Absolute by design.

**Recordings produced before the fix:**

Recordings captured before PR1 may contain mixed seed/periodic frame
contracts during active-session preview. After commit, behavior depends
on which frame authored each point and how the parent fork resolves at
that UT. Per the project's pre-1.0 stance, do not migrate old sidecars;
PR1 should make new recordings use one deliberate contract.

**Rollback:**

PR0 is logging-only and should be trivially revertible. PR1 B2 is a
resolver/context patch plus a pure recorder math seam, with no
save-format implication; revert is a clean `git revert`. Option C is a
smaller recorder gate if selected after PR0.

## 9. Secondary cleanup decision: leave `BackgroundRecorder.cs:4242-4248` alone

The `TryGetBackgroundEligibleAnchorRecording` Re-Fly exclusion was
added to keep candidate-list anchor selection from binding background
debris to a live Re-Fly recording before v12. Under v12 it is
short-circuited for debris at both relevant entry points:
`UpdateBackgroundAnchorDetection` exits through the debris contract at
`BackgroundRecorder.cs:3999-4003`, and
`ApplyBackgroundRelativeOffset` re-applies that contract at
`BackgroundRecorder.cs:4419-4422` before recorded/candidate fallback.
The structural seam at `BackgroundRecorder.cs:6327` is a call-through to
`ApplyBackgroundRelativeOffset`, so it inherits the second short-circuit.
The exclusion is unused for the bug this plan fixes.

Recommendation: do not touch it in this PR. The exclusion still
guards:

- Legacy v11 debris (no `DebrisParentRecordingId`, candidate-list
  path).
- Non-debris BG recordings (e.g. station ghosts) — the exclusion
  prevents these from anchoring to the live Re-Fly target, which is
  the right call for the same reason this plan exists.

A separate follow-up plan can revisit the exclusion once v11 debris is
no longer reachable from any active save (post-1.0). Until then, the
exclusion is cheap and load-bearing for narrow legacy paths; removing
it now broadens the risk surface beyond the bug this PR fixes.

## 10. Out of scope

Items (2), (4), and (5) from the original investigation are separate
bugs with their own plans:

- Item (2) — "drop the recorder-side Re-Fly exclusion broadly." See §9
  above for the deferral rationale.
- Item (4) — separate plan, separate PR.
- Item (5) — separate plan, separate PR.

PR1 should fix the measured recorder-vs-resolver frame mismatch and
nothing else.
