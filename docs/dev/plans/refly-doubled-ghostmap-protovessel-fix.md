# Doubled upper-stage GhostMap ProtoVessel during Re-Fly (#587 third facet)

## Why the chosen option wins

The user's exact symptom: during in-place continuation Re-Fly, two distinct
in-scene representations exist for a parent recording. The legitimate
`GhostPlaybackEngine` ghost (Ghost #0) is what the user wants kept. The bug is
the *second* representation -- a real `Vessel` registered in
`FlightGlobals.Vessels` via `GhostMapPresence.CreateGhostVesselFromStateVectors`
on the state-vector-fallback path, with a degenerate `sma=2 ecc=0.999999`
orbit and a world position that resolves through the active vessel's transform
(the recording is in a `Relative`-frame section anchored to the *active*
Re-Fly target's pid). The synthesized orbit is meaningless (ascent state at
altitude 0), and the spawned ProtoVessel ends up colocated with the active
vessel because the resolution maths run anchor-pose * local-offset.

Alternatives considered:

- **A (engine-aware suppression)** -- couples GhostMap to engine state; the
  engine is also active for purely visual playback that has no anchor-bound
  state-vector entanglement, so suppressing on engine presence alone would also
  hide legitimate orbit lines for orbital recordings.
- **B (suppress non-orbital states)** -- the `inRelative` branch *deliberately*
  skips altitude thresholds because the lat/lon/alt fields are local XYZ
  offsets, not geographic altitude (see `TryResolveStateVectorMapPointPure`
  comment block). Reintroducing a threshold here re-opens the #582/#571 class
  of bugs.
- **C (active-UT overlap)** -- broad and forces UT-window arithmetic at
  every per-frame call. Catches the symptom but also catches legitimate
  cases (a docking-target recording whose anchor is the active vessel and
  whose state IS orbital).
- **D (pure relative+anchor=ActiveVessel)** -- the right shape but applied
  globally. A non-Re-Fly playback session whose anchor happens to be the
  active vessel is a legitimate case for an orbit line if the recording is
  orbital; the degenerate orbit is only a symptom of Re-Fly's specific
  placement.

**The chosen gate (D + Re-Fly session check)**: suppress GhostMap state-vector
ProtoVessel creation only when **all three** hold:

1. A `ReFlySessionMarker` is active;
2. `StateVectorWorldFrame.Branch == "relative"`;
3. The resolution's `AnchorPid` equals the live active Re-Fly target's
   persistent id (resolved from `marker.ActiveReFlyRecordingId` via the
   committed recordings list).

This is the narrowest signal that catches the user's exact case and
preserves every adjacent legitimate code path -- non-Re-Fly relative-frame
ghosts, Re-Fly relative-frame ghosts whose anchor is some *other* vessel,
absolute-frame state-vector ghosts (real orbital state), terminal-orbit
ghosts, and segment-derived ghosts. The gate sits at the one create site
that the user's log shows is firing wrongly (`CreateGhostVesselFromStateVectors`,
just before `BuildAndLoadGhostProtoVesselCore`), and is tested via a pure
predicate (`ShouldSuppressStateVectorProtoVesselForActiveReFly`) that takes
the marker, branch, anchor pid, and committed-recordings list as inputs --
no KSP scene required.

## Predicate signature

```csharp
internal static bool ShouldSuppressStateVectorProtoVesselForActiveReFly(
    ReFlySessionMarker marker,
    string resolutionBranch,
    uint resolutionAnchorPid,
    IReadOnlyList<Recording> committedRecordings,
    out string suppressReason);
```

Returns false (with `suppressReason="not-suppressed"`) when no marker, when
the marker is in placeholder pattern (`origin != active`, mirrors #587
carve-out), when the branch is not relative, when the anchor pid is zero, or
when the marker's `ActiveReFlyRecordingId` cannot be resolved to a recording
with a non-zero `VesselPersistentId` matching the resolution's anchor.

Returns true (with a structured reason) when the resolution would place the
state-vector ProtoVessel in the same world frame as the active Re-Fly target.

## Logged decision shape

```
[Parsek][INFO][GhostMap] create-state-vector-suppressed: rec=<recId> idx=<i>
    vessel="<name>" reason=refly-relative-anchor=active anchorPid=<pid>
    activePid=<pid> sess=<sessId> ut=<UT>
```

Logged once per suppression at INFO so the post-test audit can trivially
grep `create-state-vector-suppressed`. Existing `create-state-vector-intent`
and `source-resolve` lines stay at VERBOSE for routing visibility.
