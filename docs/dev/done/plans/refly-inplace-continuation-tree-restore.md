# Re-Fly in-place continuation: tree restore + sidecar epoch + Limbo error surface

Closes #590 (umbrella). Prerequisite to the #585 / #587 fix in
`fix/585-inplace-continuation-limbo`.

This note answers the three contract questions in #590 with concrete
file references, then specifies the recovery path for #585 and the
strip-pass scoping change for #587.

## Background: what the playtest actually does

`logs/2026-04-25_1933_refly-bugs/KSP.log` captures the user re-flying
the booster recording `01384be4319544aebbc7b4a3e0fdd45c`
("Kerbal X Probe", pid `3474243253`) via the recordings table's
Re-Fly button, while the prior active recording was the capsule
`5294a8d9c77a4c289bcb5b0a944437e6` ("Kerbal X", pid `2708531065`).

Timeline of the rewind quicksave load (all UT 160.4):

1. `19:12:21.968` Re-Fly invoked from SPACECENTER. `RewindInvoker.Invoke`
   triggers `GamePersistence.LoadGame` of the rewind quicksave. Note:
   the Re-Fly path does not touch `RewindContext.IsRewinding` (only
   the legacy `RecordingStore.InitiateRewind` does), so the new
   scenario's `OnLoad` runs the normal SPACECENTER -> FLIGHT branch,
   not `HandleRewindOnLoad`.
2. `19:12:23.124-23.140` `RecordingStore.LoadRecordingFiles` walks
   the loaded scenario's `RECORDING_TREE` node, hits two
   `Sidecar epoch mismatch` warnings: the on-disk `.prec` is
   `epoch=4` for `01384be4` and `epoch=6` for `5294a8d9`, but the
   rewind quicksave's `.sfs` carries `sidecarEpoch=1` and `=2`
   respectively. Bug #270's mitigation drops trajectory + snapshot
   loads on every mismatch.
3. `19:12:23.140` `TryRestoreActiveTreeNode` calls
   `StashPendingTree(state=Limbo)` with the freshly-loaded 8-recording
   tree. The `2 sidecar hydration failure(s)` are recorded but no
   blocking error fires because `staleEpochHydrationFailures > 0` does
   NOT match the `ShouldKeepPendingTreeAfterHydrationFailure` clause
   (the in-memory pending tree was already overwritten by the
   `OnSceneChangeRequested` path at FLIGHT exit).
4. `19:12:24.252-24.257` `RewindInvoker.ConsumePostLoad` runs Strip +
   Activate + AtomicMarkerWrite. Strip kills the capsule
   (pid `2708531065` matched slot 0), selects the booster
   (pid `3474243253`, slot 1). Three pre-existing
   `Kerbal X Debris` vessels (pids `3749279177`, `2427828411`,
   `526847698` -- carried in the rewind quicksave's `protoVessels`
   from prior career flights) are LeftAlone because no slot map
   matches them.
5. `19:12:24.265` `AtomicMarkerWrite: in-place continuation detected
   - marker -> origin 01384be4319544aebbc7b4a3e0fdd45c (no
   placeholder created)`. Marker writes correctly:
   `OriginChildRecordingId == ActiveReFlyRecordingId == 01384be4...`.
6. `19:12:24.275` `Strip left 1 pre-existing vessel(s) whose name
   matches a tree recording: [Kerbal X Debris]`. WARN-and-continue
   -- the debris stays in scene.
7. `19:12:24.534` `RestoreActiveTreeFromPending: waiting for vessel
   'Kerbal X' (pid=2708531065) to load`. The coroutine reads
   `tree.ActiveRecordingId`, which still points at the capsule
   (`5294a8d9`) because that was the active recording at the moment
   the rewind quicksave's `.sfs` was authored. `targetPid=2708531065`
   is the capsule's pid, but the capsule has just been killed by
   Strip; only the booster (`3474243253`) is alive.
8. `19:12:27.525` Coroutine times out: `vessel 'Kerbal X' (and no
   EVA parent fallback) not active within 3s -- leaving tree in
   Limbo`. Recorder is never bound to `01384be4`.
9. `19:12:24.264` (parallel) `Post-switch auto-record armed:
   vessel='Kerbal X Probe' pid=3474243253 tracked=False reason=vessel
   switch to outsider while idle`. The recorder treats the booster
   as a fresh outsider because `activeTree` was nulled by
   `ResetFlightReadyState` and the restore coroutine has not yet
   reinstalled it. Even after the coroutine times out, the
   tracked-set is empty so first-modification auto-record would
   create a brand-new standalone recording, not resume into
   `01384be4`.

Net result: `01384be4` accumulates zero new trajectory points and no
snapshot for the entire 7-minute booster flight. The post-Re-Fly
SPACECENTER merge dialog at `19:19:39.944` then renders
`hasSnapshot=False canPersist=False` and `spawnable=0` for the entire
tree.

## Question 1: marker contract for `RestoreActiveTreeFromPending`

**Contract:** when `ParsekScenario.Instance.ActiveReFlySessionMarker`
is non-null AND `marker.OriginChildRecordingId ==
marker.ActiveReFlyRecordingId` (in-place continuation pattern --
defined by `RewindInvoker.AtomicMarkerWrite`'s `inPlaceContinuation`
branch and pinned in `MarkerValidator.Validate`), the
`RestoreActiveTreeFromPending` coroutine MUST resolve the expected
active vessel from `marker.ActiveReFlyRecordingId`, not from the
freshly-loaded tree's `ActiveRecordingId`.

For the playtest:

- Marker after `AtomicMarkerWrite`: `ActiveReFlyRecordingId = 01384be4`,
  `OriginChildRecordingId = 01384be4`.
- Tree from rewind quicksave: `ActiveRecordingId = 5294a8d9` (because
  the capsule was active when the quicksave was authored).
- The tree's `5294a8d9` recording has `VesselName="Kerbal X"`,
  `VesselPersistentId=2708531065` -- that vessel was just killed by
  Strip. The booster is alive at `3474243253`, named "Kerbal X Probe",
  and corresponds to `01384be4`.

Without the carve-out, the wait-loop targets the capsule, which is
gone, and the tree falls into Limbo.

**Where the carve-out belongs:** inside `RestoreActiveTreeFromPending`
in `Source/Parsek/ParsekFlight.cs`, immediately after the
`tree.ActiveRecordingId` read at line 7856 and BEFORE the wait-loop
at line 7883. If a live in-place-continuation marker exists and its
`ActiveReFlyRecordingId` is present in the freshly-popped tree's
`Recordings` map, swap `activeRec`, `activeRecId`, `targetName`,
`targetPid` to the marker's recording AND flip the tree's
`ActiveRecordingId` to the marker's id (so the rest of the coroutine
-- `recorder.StartRecording(isPromotion: true)` and the BackgroundMap
PID remap -- bind to the right recording).

**Why the live marker is the right authority:** at the moment
`RestoreActiveTreeFromPending` runs in `OnFlightReady`, the
synchronous `ConsumePostLoad` -> `AtomicMarkerWrite` path has
already executed (it runs from `OnLoad` synchronously, before the
flight-ready callback fires). So the marker is guaranteed to be
populated by the time the restore coroutine reads it. The marker is
the only authoritative source of "which recording does the player
intend to keep recording into post-rewind"; the tree's
`ActiveRecordingId` reflects the pre-rewind quicksave state and is
stale by definition for an in-place continuation.

**Carve-out gate:** the swap fires only when
`marker.OriginChildRecordingId == marker.ActiveReFlyRecordingId`
(not the placeholder pattern). The placeholder pattern produces a
fresh `NotCommitted` recording that is NOT in the tree's
`Recordings` map; the existing wait-on-`ActiveRecordingId` behaviour
is correct for that path because the placeholder is never the tree's
active recording -- the tree's ActiveRecordingId still reflects the
pre-rewind active vessel (the capsule), which the placeholder pattern
keeps in scene.

## Question 2: `Recording.Epoch` vs on-disk `.prec` for in-place continuation

**Setup.** `Recording.SidecarEpoch` is incremented every time
`RecordingStore.SaveRecordingFiles` writes a `.prec` for a recording
(see `RecordingStore.cs:5942` `WriteTrajectorySidecar`). The
recording's `.sfs` representation carries the `sidecarEpoch` field
that was written at the moment of the most-recent OnSave. The on-disk
`.prec` carries its own `sidecarEpoch` field (in the binary header for
v3 sidecars; in the `PARSEK_RECORDING.sidecarEpoch` ConfigNode field
for v0-v2). When the two disagree, bug #270's mitigation
(`ShouldSkipStaleSidecar`) drops the sidecar load entirely on the
assumption the `.prec` is from a different save point.

**Playtest case.** The `.sfs` (rewind quicksave, captured pre-mission
launch at UT 160) has `sidecarEpoch=1` for `01384be4` and `=2` for
`5294a8d9`. The on-disk `.prec` files were rewritten as the original
mission progressed and their epochs grew to `=4` and `=6`.

For the rewind quicksave, the `.sfs` epoch represents the recording's
bulk-data state AT the moment the quicksave was authored. The on-disk
`.prec` always reflects the LATEST writes. After the original mission
flew to completion, the `.prec` files contain the FUTURE trajectories
(post-rewind-UT 160 data). Loading them into a recording whose `.sfs`
expects the pre-rewind shape WOULD violate the epoch contract -- bug
#270's mitigation is correct as a default.

**Authoritative side for in-place continuation.** The rewind
quicksave's `.sfs` epoch is authoritative for the trajectory POINTS
AND OrbitSegments AND TrackSections AND PartEvents we keep around the
recording. The on-disk `.prec` (epoch 4 / 6) is authoritative for the
SNAPSHOTS only -- vessel/ghost ConfigNode + visual snapshot data.
Rationale:

- The trajectory points after the rewind UT are by definition the
  "future timeline" we are erasing. Trimming them post-rewind-UT and
  rebuilding from the live recorder is exactly what an in-place
  continuation already DOES going forward -- the recorder appends new
  points, replacing the (now-trimmed) future. Loading the future-state
  `.prec` then trimming past `rewindUT` is a wasted round-trip.
- The vessel snapshot (`VesselSnapshot`, `GhostVisualSnapshot`,
  `GhostGeometrySnapshot`) does not carry per-UT data; it is a
  point-in-time capture of the vessel's ConfigNode shape. The
  most-recent snapshot in the on-disk `.prec` (epoch 4 / 6) IS the
  shape the player landed / staged with at end-of-original-mission,
  and is what the merge dialog needs to render `hasSnapshot=True`.

**Recovery contract.** When `ShouldSkipStaleSidecar` fires for the
ACTIVE recording during a rewind quicksave's OnLoad
(`TryRestoreActiveTreeNode`), the load path must:

1. Skip the trajectory deserialization (current behaviour, keep).
2. Still load the snapshot fields (`VesselSnapshot`,
   `GhostVisualSnapshot`, `GhostGeometrySnapshot`) from the on-disk
   `.prec` if its sidecarEpoch is GREATER than the `.sfs` expected
   epoch -- those snapshots are the most-recent valid shape. If the
   on-disk `.prec` epoch is LESS (legitimately stale -- e.g. file
   corruption or a half-written rollback), keep the current
   skip-everything behaviour.
3. The trajectory list stays empty; the recorder will repopulate it
   on the first frame of resumed recording. Truncation of the existing
   `.prec` is NOT performed at OnLoad -- the next
   `SaveRecordingFiles` call from the resumed recorder will overwrite
   the whole `.prec` with the new (post-rewind) trajectory and bump
   the epoch.

**Scope of the change for #585.** Implementing snapshot-only-rescue
on epoch mismatch is deferred. The minimum-viable fix for #585 is:
when the active recording's sidecar load fails specifically with
`stale-sidecar-epoch` AND a live in-place-continuation marker exists
pinning that recording, the tree-stash path must NOT mark the tree
as Limbo -- it can still go to `PendingTreeState.Limbo` for the
restore coroutine, but the coroutine will use the marker to bind
the recorder regardless of whether the snapshot is hydrated. The
merge dialog's `hasSnapshot=False` for `01384be4` can be addressed
separately by force-capturing a vessel snapshot at the post-rewind
SPACECENTER scene exit (`StashActiveTreeAsPendingLimbo` already does
this for null-snapshot leaves at line 8219) -- the booster is the
live active vessel at scene exit, so a snapshot capture there
populates `VesselSnapshot` regardless of what the on-disk `.prec`
holds.

This avoids any new ConfigNode-parsing logic on the trim-by-epoch
path and keeps bug #270's safety net intact for the
non-in-place-continuation case (corrupt save, half-written file,
etc).

## Question 3: should sidecar hydration drop tree to Limbo?

The current shape is correct for the GENERAL case: when sidecar load
fails for one or more recordings during a quicksave's OnLoad, the
tree goes to `PendingTreeState.Limbo` and the restore coroutine
attempts to bind the active vessel + recorder. If that succeeds, the
recordings with missing trajectories simply have empty `Points`
lists; the merge dialog renders them as 0s but the rest of the tree
is intact.

For an IN-PLACE CONTINUATION rewind, however, two extra invariants
apply:

- **Active recording's sidecar failure is non-fatal.** The recorder
  is about to start appending new points anyway; an empty trajectory
  list at restore time is the pre-rewind contract for the
  to-be-resumed recording. `Recording.Points.Clear()` is in the
  recorder's wheelhouse. So the sidecar failure on `01384be4`
  itself contributes zero degradation. The Limbo state is currently
  appropriate; what's wrong is the COROUTINE'S handling of the wrong
  expected vessel (Question 1).
- **Origin recording's sidecar failure (when it differs from the
  active) ALSO can be non-fatal** for the same reason: an in-place
  continuation will repopulate the same recording. For the playtest's
  in-place case, origin == active so this collapses to the above.

For the placeholder pattern (origin != active, fresh provisional),
the active recording does not exist on disk yet (it was just created
by `AtomicMarkerWrite`'s `BuildProvisionalRecording`), so its
sidecar can never fail to load.

**Conclusion: do not change the Limbo error surface for the rewind
case.** Bug #270's safety net stays as the default. The fix for #585
is in `RestoreActiveTreeFromPending`'s vessel-resolution step,
NOT in the sidecar load path or the tree-stash path. The
`2 sidecar hydration failure(s)` log line still fires for the
in-place continuation case, but with the marker-aware coroutine the
tree exits Limbo via the normal restore path within 3s, the recorder
binds to `01384be4`, and the merge dialog at scene exit reads the
expected `hasSnapshot=True canPersist=True` (modulo Question 2's
deferred snapshot-only-rescue, which we address by re-capturing the
vessel snapshot at scene exit).

## Implementation summary (closes #585 + #587)

### #585 fixes

- `ParsekFlight.RestoreActiveTreeFromPending`: at the top of the
  coroutine body, after the `tree.ActiveRecordingId` read but before
  the 3s wait loop, consult `ParsekScenario.Instance.ActiveReFlySessionMarker`.
  When the marker is in-place-continuation and its
  `ActiveReFlyRecordingId` exists in `tree.Recordings`, swap the
  target to the marker's recording (id, name, pid) and flip
  `tree.ActiveRecordingId` to match. Verbose log the swap with
  before/after ids.
- `ParsekFlight.OnVesselSwitchComplete`: when the live
  `ActiveReFlySessionMarker` indicates an in-place continuation AND
  the new active vessel matches the marker's recording, suppress the
  "outsider while idle" arming (the restore coroutine will bind
  the recorder; arming a fresh standalone auto-record on top would
  race the coroutine and risk creating a duplicate recording). The
  arming path already gates on `restoringActiveTree` via
  `EvaluatePostSwitchAutoRecordSuppression`, but that flag is only
  raised inside the coroutine body's try/finally -- the
  ArmPostSwitchAutoRecord call fires from `OnVesselSwitchComplete`
  BEFORE the coroutine runs. New gate: if the marker is in-place and
  the new active vessel's pid == the recording's pid (or is in
  `tree.BackgroundMap` keyed by the marker's recording id), disarm
  with reason `marker-in-place-continuation`.
- `ParsekScenario.TryRestoreActiveTreeNode`: log line update only --
  the existing tree-stash log gets a `markerOriginActive=...` field
  when the marker is set so the post-mortem is greppable. No
  behavioural change to the stash path itself.

### #587 fix

- `Source/Parsek/RewindInvoker.cs::WarnOnLeftAloneNameCollisions`:
  when the live marker is in-place-continuation and a left-alone
  vessel's NAME matches a committed-recording vessel name AND that
  recording's terminal state is `Destroyed` (i.e., it's tree
  debris, not a live mid-mission ghost), KILL the left-alone vessel
  via `Vessel.Die()` after the strip -- not via the `PostLoadStripper`
  itself (the stripper is keyed on PidSlotMap and stays unchanged),
  but as a follow-up clean-up in the post-strip warning path. This
  prevents pre-existing `Kerbal X Debris` from confusing KSP-stock
  patched conics into the phantom "Kerbin Encounter T+" + 50x warp
  cap that #587 captures.
- The new strip-supplement path explicitly excludes the
  `selectedSlotIndex` vessel and the marker's
  `ActiveReFlyRecordingId` vessel pid by id-and-pid match, so it
  cannot regress #573's strip-kill protection on the actively
  re-flown vessel.

### Post-fix observability

Every state transition gets a structured log line:

- `RestoreActiveTreeFromPending: in-place continuation marker swapped
  target rec=<oldId>->\<newId> vessel='<old>'->'<new>' pid=<old>->\<new>`
  (Info).
- `Post-switch auto-record disarmed: marker-in-place-continuation`
  (Info, on the suppression path).
- `Strip post-supplement: killed N debris vessel(s) for in-place
  continuation: [<names>]` (Warn -- still surface that we did extra
  cleanup, since the warn-only path was the prior contract).

These match `RewindLoggingTests.cs`'s pattern: every test for these
paths asserts on a distinctive substring in the captured log output
via `ParsekLog.TestSinkForTesting`.

## Out-of-scope (filed for follow-up if reproduced)

- Snapshot-only rescue on stale-sidecar-epoch (Question 2 step 2).
  Deferred until a playtest captures the
  `hasSnapshot=False` symptom AFTER the marker-aware tree-restore
  fix is shipped. The fallback "re-capture snapshot at scene exit"
  via `StashActiveTreeAsPendingLimbo`'s null-snapshot loop is
  expected to cover the playtest's symptom in practice.
- Sidecar epoch contract refactor: the epoch-mismatch-must-not-Limbo
  rule for the active in-place continuation recording is implemented
  via Question 1's marker-aware coroutine, not via reshaping the
  sidecar load path. A larger refactor that lets `Recording.Epoch`
  carry an "expected next sidecar epoch" range (instead of an exact
  match) would touch `RecordingStore.WriteTrajectorySidecar`,
  `ShouldSkipStaleSidecar`, every recording's
  `MarkFilesDirty`/`SidecarEpoch` use site, and
  `Bug270SidecarEpochTests`. Out-of-scope for the v0.8.1 fix tail;
  filed as a future invariant-tightening pass.
