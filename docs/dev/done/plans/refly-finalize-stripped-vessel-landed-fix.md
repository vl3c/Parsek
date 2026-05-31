# Re-Fly second-order data loss: FinalizeTreeRecordings clobbers a restored recording's terminal state

## Rationale

After PR #572's `RestoreHydrationFailedRecordingsFromCommittedTree`
repairs an active-tree recording from the committed copy on
`SaveActiveTreeIfAny`, the immediately-following `FinalizeTreeRecordings`
on scene exit walks the same recording, sees its persistent vessel id
is no longer in `FlightGlobals.Vessels` (Re-Fly stripped it), and falls
into the "vessel was alive when unloaded" branch. That branch infers
terminal state from the recording's last trajectory point — which, for
a recording that began with an atmospheric ascent, points at altitude
~10 m and resolves to `Landed`. The just-restored `terminal=none` is
overwritten with `Landed`, `OrbitSegments` are ignored, and the
ghost-map trajectory ends up wrong.

Chosen: **Option A — track recordings restored from the committed
tree this frame and preserve their repaired terminal state while the
scene-exit path sees no live vessel.** The guard skips the cache /
finalizer terminal mutation sources as well as the final
Landed/Splashed inference branch; all three are scene-exit evidence,
not committed-tree truth, for a Re-Fly strip casualty.

Rejected alternatives:

- **Option B — strip-pid set.** Cleanest semantics but invasive: the
  Re-Fly strip set lives in `RewindInvokeContext`, which is cleared
  immediately after the post-load consumer runs, long before scene
  exit. Plumbing a strip-pid set through `ReFlySessionMarker` save/
  load just to support a finalize-time gate is a lot of new state for
  one call site.
- **Option C — skip inference when terminal is already set.** Doesn't
  apply: the user's recording was committed mid-flight with
  `terminal=none`, so the gate would never fire.
- **Option D — orbital-evidence guard (`MaxDistanceFromLaunch` /
  stable orbit segment).** Tried it as a defensive secondary gate,
  but the legitimate "orbit-then-land" case
  (`Bug278FinalizeLimboTests.EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory`,
  `SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog`)
  also has high `MaxDistanceFromLaunch` plus a stable orbit segment
  alongside a low-altitude last point. Any threshold that captures the
  user's case also breaks legitimate orbit-then-land. Dropped.

Option A is surgical, has clear lifecycle (set on restore, cleared on
read in finalize), and directly addresses the second-order companion
to PR #572's repair. The user's specific case is solved by the flag
alone because the committed copy's recording carried no terminal
state, and the broader cache/finalizer gate prevents adjacent
scene-exit sources from reintroducing the same clobber before the
surface-inference branch runs. The active-recording ensure pass is
restricted to true non-leaf active recordings so an already-processed
active leaf cannot be finalized a second time after clearing the
transient restore flag.

## Files touched

- `Source/Parsek/Recording.cs` — new `[NonSerialized]
  RestoredFromCommittedTreeThisFrame` flag.
- `Source/Parsek/ParsekScenario.cs` —
  `RestoreCommittedSidecarPayloadIntoActiveTreeRecording` sets the flag.
- `Source/Parsek/ParsekFlight.cs` — new
  `ShouldSkipSceneExitSurfaceInferenceForRestoredRecording` /
  `ShouldPreserveRestoredSceneExitTerminalState` helpers consulted
  before scene-exit finalizer/cache fallback and from
  `FinalizeIndividualRecording`'s leaf scene-exit branch plus
  `EnsureActiveRecordingTerminalState`'s active-non-leaf scene-exit
  branch. `ShouldEnsureActiveRecordingTerminalState` keeps the active
  ensure pass scoped to true non-leaves. The terminal-preserve paths
  clear the flag on read.
- `Source/Parsek.Tests/Bug572FollowupFinalizeRestoredTests.cs` — new.
- `docs/dev/todo-and-known-bugs.md`, `CHANGELOG.md`.

## Log lines

- `FinalizeTreeRecordings: skipping Landed/Splashed inference for
  '<id>' (vessel pid=<pid>) — recording was repaired from committed
  tree this frame (PR #572 follow-up: trajectory came from a
  non-authoritative committed copy) (lastPtAlt=<alt>m maxDist=<m>
  orbitSegs=<n>)` — leaf scene-exit branch.
- `FinalizeTreeRecordings: skipping Landed/Splashed inference for
  active recording '<id>' (vessel pid=<pid>) — recording was repaired
  from committed tree this frame …` — non-leaf active-recording branch.
- `FinalizeTreeRecordings: preserving repaired terminalState=<state>
  for '<id>' (vessel pid=<pid>) — recording was repaired from committed
  tree this frame …` — restored leaf already had a terminal state and
  scene-exit cache repair was skipped.

These match the existing `[Flight]` `FinalizeTreeRecordings: …` style.

## Out of scope

- The drop from `points=281 orbitSegs=2` to `points=229 orbitSegs=0`
  observed between the first commit and the second finalize is a
  separate question (likely chain-segment pruning when the new probe
  child took over the post-fork data). This fix does not address it;
  it ensures that whatever data the recording carries at finalize time
  is not bulldozed into a wrong `Landed` terminal.
