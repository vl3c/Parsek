# Loop-anchored re-fly reachability check (PR 901 follow-up)

PR 901 listed two known regressions when `forceAbsoluteForReFlyProvisional`
was ON. PR 909 (the narrowed-gate filter) closed both as the new default
behavior. The second regression line was hedged: "loop-anchored re-fly
fork loses Relative-against-live-loop-anchor precision IF REACHABLE".
The post-implementation verification was never done. This note pins
the reachability outcome with concrete evidence from the code.

## Question

Can a re-fly provisional recording (the recording at
`marker.ActiveReFlyRecordingId` while
`ParsekScenario.Instance.ActiveReFlySessionMarker` is non-null and the
narrowed-gate filter is the live code path) ever carry
`Recording.LoopAnchorVesselId != 0`?

If yes, the narrowed-gate filter must keep a real out-of-tree loop
anchor passing through the nearest-search; if no, the regression line
is unreachable by construction and can be dropped from the docs.

## Files read

Production sources (paths relative to the repo root):

- `Source/Parsek/Recording.cs:63` — field declaration
  (`public uint LoopAnchorVesselId;` default `0u`).
- `Source/Parsek/Recording.cs:641-689` —
  `ApplyPersistenceArtifactsFrom(Recording source)`; line 669 copies
  `LoopAnchorVesselId = source.LoopAnchorVesselId`.
- `Source/Parsek/RewindInvoker.cs:1643-1667` —
  `BuildProvisionalRecording`; constructs the provisional via
  `new Recording { ... }` and assigns only RecordingId, MergeState,
  CreatingSessionId, ProvisionalForRpId, ParentBranchPointId, TreeId,
  VesselPersistentId, VesselName, PlaybackEnabled.
- `Source/Parsek/RewindInvoker.cs:234-265` —
  `CopyInheritedIdentityForFork(provisional, inheritFrom)`; copies
  VesselPersistentId, VesselName, IsDebris,
  DebrisParentRecordingId, Generation, StartBodyName, StartBiome,
  StartSituation, LaunchSiteName, VesselSnapshot, GhostVisualSnapshot.
  Explicit comment at lines 229-232 notes Chain identity is
  intentionally NOT copied. `LoopAnchorVesselId` is not in the list
  either.
- `Source/Parsek/RewindInvoker.cs:894-908` —
  `Recording.CapturePreReFlyAnchorTrajectoryFrom`; copies only the
  trajectory artifact lists (Points, OrbitSegments, TrackSections)
  under a session id. No loop fields touched.
- `Source/Parsek/RewindInvoker.cs:1217-1276` — `AtomicMarkerWrite`
  shows the full set of mutations made to the fresh provisional
  during the post-load critical section. The sequence is:
  `BuildProvisionalRecording`, set `SupersedeTargetId`, then for the
  in-place continuation branch `CopyInheritedIdentityForFork`,
  `TagForkInitialSegmentPhase`, `TryRefreshForkSnapshotsFromLiveVessel`,
  `CapturePreReFlyAnchorTrajectoryFrom`. None of these touch
  `LoopAnchorVesselId`.
- `Source/Parsek/SwitchSegmentBuilder.cs` and
  `Source/Parsek/RecordingTreeSplitter.cs` and
  `Source/Parsek/SupersedeCommit.cs` — grep for `LoopAnchorVesselId`
  inside these files: zero hits. The split / supersede / switch
  paths never read or write the loop anchor field.

Repo-wide grep across `Source/Parsek/` for write sites with the
pattern `LoopAnchorVesselId\s*=[^=]` (excluding test files) returns
exactly three call sites:

1. `Source/Parsek/Recording.cs:669` — `ApplyPersistenceArtifactsFrom`
   (field copy from a source `Recording`).
2. `Source/Parsek/ParsekScenario.cs:6693` — deserialization from
   the `.sfs` scenario node (`loopAnchorPid` value).
3. `Source/Parsek/RecordingTreeRecordCodec.cs:550` —
   deserialization from a tree record node (`loopAnchorPid` value).

There is no UI write site, no flight-recorder write site, no
chain-segment write site, no recorder-detected loop-anchor
auto-assignment. `LoopAnchorVesselId` is set only at load and only
propagated by an explicit `ApplyPersistenceArtifactsFrom` copy
elsewhere.

## Evidence

### The provisional starts with `LoopAnchorVesselId == 0`

`BuildProvisionalRecording` (`RewindInvoker.cs:1643-1667`):

```
var rec = new Recording
{
    RecordingId = "rec_" + Guid.NewGuid().ToString("N"),
    MergeState = MergeState.NotCommitted,
    CreatingSessionId = sessionId,
    ProvisionalForRpId = rp.RewindPointId,
    ParentBranchPointId = originChild?.ParentBranchPointId ?? rp.BranchPointId,
    TreeId = originChild?.TreeId,
    VesselPersistentId = stripResult.SelectedPid,
    VesselName = ...,
    PlaybackEnabled = false,
};
return rec;
```

`new Recording { ... }` leaves every unset field at its CLR default.
For `uint LoopAnchorVesselId` that default is `0u`. The initializer
does not assign the field.

### `CopyInheritedIdentityForFork` does not propagate it

`RewindInvoker.cs:234-265` enumerates every field copied from the
inheritance source onto the provisional. The list is:

```
VesselPersistentId, VesselName, IsDebris, DebrisParentRecordingId,
Generation, StartBodyName, StartBiome, StartSituation,
LaunchSiteName, VesselSnapshot, GhostVisualSnapshot
```

`LoopAnchorVesselId` is not in that list. The XML doc on lines 229-232
spells out the inverse-named field `ChainId` as "intentionally NOT
copied" but the same logic applies in practice for every loop field:
nothing reaches across the fork boundary.

### Split / supersede / switch paths are silent on the field

```
$ grep LoopAnchorVesselId Source/Parsek/RecordingTreeSplitter.cs
$ grep LoopAnchorVesselId Source/Parsek/SupersedeCommit.cs
$ grep LoopAnchorVesselId Source/Parsek/SwitchSegmentBuilder.cs
(no matches)
```

The re-fly merge tail (`SupersedeCommit`), the origin splitter
(`RecordingTreeSplitter`), and the switch-segment continuation builder
(`SwitchSegmentBuilder`) never read or write the loop anchor field.
They cannot propagate it.

### What `ApplyPersistenceArtifactsFrom` does and when it runs

`Recording.cs:641-689` is the one method that copies
`LoopAnchorVesselId` between two `Recording` instances. Its three
production call sites:

1. `ChainSegmentManager.cs:515` — `rec.ApplyPersistenceArtifactsFrom(captured)`
   during a CHAIN-SEGMENT commit, where `captured` is the FlightRecorder's
   `CaptureAtStop` (the just-stopped segment's metadata). This is the
   chain-continuation path: end a chain segment, start a new one within
   the same chain. The loop anchor settings propagate from segment to
   segment of one chain. This is NOT the re-fly fork path.
2. `ParsekScenario.cs:5238` — repair-from-clone during scenario load
   (when a sidecar-only recording is being reconstructed on top of an
   in-memory recording). Not the re-fly creation path.
3. `ParsekScenario.cs:5415` — same repair pattern for a different
   target. Not the re-fly creation path.

The re-fly fork creation path (`AtomicMarkerWrite`) does NOT call
`ApplyPersistenceArtifactsFrom`. The provisional therefore retains
its `new Recording`-initialized default of `LoopAnchorVesselId = 0`.

### Sanity check on loaded provisionals

A NotCommitted re-fly provisional that was written to disk and then
reloaded (e.g. after a crash before the merge committed) would
deserialize through `ParsekScenario.cs:6693` or
`RecordingTreeRecordCodec.cs:550`. Both deserializers only populate
the field when the `loopAnchorPid` ConfigNode value is present. The
provisional's authoring code at `BuildProvisionalRecording` never
sets the field, so the serializer in `ParsekScenario.cs:6511-6512`
(and `RecordingTreeRecordCodec.cs:108-109`) gates its write on
`rec.LoopAnchorVesselId != 0` and emits nothing. A reloaded
provisional therefore still arrives with the field at 0u.

## Conclusion

**Outcome A. Not reachable.**

A re-fly provisional recording cannot carry `LoopAnchorVesselId != 0`
through any production code path. The fork constructor leaves the
field at its CLR default, the in-place inheritance helper does not
copy it, the split / supersede / switch paths do not touch it, no
runtime auto-assignment exists, and serializer round-trips preserve
the zero value because the writer skips it when zero. The only way
`LoopAnchorVesselId` becomes non-zero is via disk load of a
recording that explicitly carries it, which never applies to a
fresh re-fly fork.

The narrowed-gate filter's behavior in this case is therefore moot:
when the active recording is a re-fly provisional, the provisional
itself is not a loop-anchored recording, so no
"Relative-against-live-loop-anchor precision" can be lost or
preserved on the provisional. Other looped recordings in the SAME
tree are independent recordings (not the provisional), so the filter
does not affect their loop-anchor resolution — they continue to
resolve against the live `LoopAnchorVesselId` PID through the
existing loop-only gates (`GhostPlaybackEngine.ShouldUseLoopAnchor` /
`Rendering/ProductionAnchorWorldFrameResolver.TryResolveLoopAnchorWorldPos`)
and are not provisional recordings authored during the active
re-fly session.

The user's clarification in `narrow-refly-relative-gate.md:13` —
"the chain anchor is a live persistent vessel" — describes a real
out-of-tree loop anchor. That loop anchor is itself a separate live
vessel and lives outside the active re-fly tree by construction
(otherwise the rendering-side `Recording.LoopAnchorVesselId` would
have to refer to a tree-internal PID, which contradicts the
loop-against-live-PID design). So even if the player did re-fly a
recording whose sibling loop-anchored recording is rendering in
flight, the loop anchor's PID is a live vessel out of the tree and
the filter's same-tree drop rule does not touch it: the loop chain
keeps resolving against the live PID exactly as before. The re-fly
provisional's authored Relative anchor selection is a separate
question that the narrowed-gate filter already handles correctly.

## Recommendation

Mark the second regression line in `docs/dev/todo-and-known-bugs.md`
as **NOT REACHABLE** with a pointer to this research note, rather
than deleting it. The annotation preserves the historical record of
what PR 901 hedged on, so a future reader of the PR 901 todo entry
sees both regressions and how each was resolved (regression #1
closed by narrowed-gate behavior, regression #2 confirmed
structurally impossible). The case is structurally impossible at
the provisional level, and the loop-anchor playback paths for other
recordings in the tree are not affected by the narrowed-gate filter
(which only touches re-fly-provisional anchor candidate selection
in the recorder).

No test or production code change required. The narrowed-gate filter
remains correct.
