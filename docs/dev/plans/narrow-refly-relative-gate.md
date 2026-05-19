# Narrowed-gate re-fly Relative anchor selection

Follow-up to PR 901. Replaces the `ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor` supersede-target bypass at both recorder gate sites with a narrowed-gate filter that drops only same-tree candidates from the nearest-search input. The result: re-fly forks with no nearby real anchor author Absolute (PR 901's validated default), re-fly forks with a nearby real station/base/loop-anchor author Relative-against-the-real-anchor.

## Why

PR 901 shipped the `forceAbsoluteForReFlyProvisional` setting (off by default) and validated end-to-end that Absolute is the right contract for re-fly forks. But the toggle's design was all-or-nothing: when on, the recorder skipped both the supersede-target bypass AND the fallback nearest-search wholesale. That collapsed two regressions documented in the PR 901 todo:

1. **Docking-mid-rewind**: a re-fly that starts within 200m of a real persistent station lost Relative-against-real-station precision. The toggle skipped the nearest-search instead of letting it pick the real station.

2. **Loop-anchored re-fly fork**: a re-fly inside a loop chain where the chain anchor is a live persistent vessel (orbital loops use `Recording.LoopAnchorVesselId` for live-PID anchoring because body-fixed Absolute drifts with body rotation across N loops) lost Relative-against-live-loop-anchor precision. Same root cause: the toggle skipped the nearest-search.

The user's clarification on the loop case is the load-bearing insight: looped ghosts at orbital altitudes have a timing problem. At loop N, body rotation has continued for N x loop_period, so the original lat/lon/alt represents a different inertial point than where the orbit actually puts the ghost. Body-fixed Absolute is wrong for orbital loops. The fix already in the codebase: anchor the loop to a real persistent vessel via `LoopAnchorVesselId`, so the loop replays Relative-against-the-live-anchor and the timing skew cancels. That means the "Relative-against-real-anchor within 500m" recorder behavior is load-bearing for orbital loops and must be preserved across re-fly too.

So: re-fly authoring should be Absolute by default, but Relative when there's a real out-of-tree anchor nearby. That's a different shape from "Absolute always when re-fly" (PR 901's toggle-ON behavior), and a different shape from "Relative against the supersede target ghost" (PR 901's toggle-OFF behavior).

## The filter rule

While a re-fly session is active (`ParsekScenario.Instance.ActiveReFlySessionMarker` is non-null) and the recording being authored is the live provisional (active recording id == `marker.ActiveReFlyRecordingId`), drop every anchor candidate whose `RecordingId` is a member of the same `RecordingTree.Recordings` keyset as the provisional.

Why same-tree is the right cut:
- `RecordingTree.Recordings` contains the provisional, the supersede target, supersede-chain ancestors, parent-anchored debris from the original launch, prior re-fly attempts in the same tree. Every recording that is part of the re-fly lineage.
- Recordings outside the active tree are by definition unrelated persistent vessels: real stations from other missions, bases, live vessels from separate lineages. Exactly the anchors we want to find.

The user constraint ("record Relative for ghosts when there's another real vessel / station / base close, within 500m, for orbital looped ghosts") falls out: real anchors are out-of-tree and pass the filter.

## Walkthrough

| Scenario | Filter effect | Outcome |
|----------|---------------|---------|
| Re-fly, no nearby real anchor | All ghosts in-tree (supersede target, lower-stage debris, prior provisionals) filtered out | Absolute (PR 901 validated default) |
| Re-fly mid-docking, real station within 200m | Station out-of-tree, passes filter; in-tree ghosts dropped | Relative-against-real-station (closes regression #1) |
| Re-fly inside loop chain with live loop anchor | Live loop anchor out-of-tree, passes filter | Relative-against-live-loop-anchor (closes regression #2) |
| Normal recording (no re-fly marker) | Filter inactive (pass-through) | Existing behavior preserved |
| Multiple re-fly attempts in same tree | Marker is singular; all siblings in-tree, all filtered out | Absolute, no sibling pinning |

## Implementation

### `ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional`

Two overloads. The pure overload takes `marker`, `activeRecordingId`, `sameTreeRecordingIds` set, and the candidate list. The production overload reads the marker from `ParsekScenario.Instance` and derives the same-tree id set from `activeTree.Recordings.Keys`.

Fast-path: if `candidates` is null/empty, return as-is. If marker is null or active recording is not the provisional (`IsActiveRecordingReFlyProvisional` returns false), return as-is. If the same-tree id set is null/empty, return as-is (defensive: cannot filter without the set).

Otherwise, walk the candidate list. For each candidate, check `sameTreeRecordingIds.Contains(c.RecordingId)`. If true, drop. Else keep.

Allocation discipline: if zero drops, return the input list by reference (no GC churn on the steady-state pass-through path). If any drops, build a new list and copy.

Observability: when any drop occurs, emit a single rate-limited Anchor Verbose log line carrying `dropped=N kept=M provisionalRecId=...` so a playtest reader can see the filter firing.

### Recorder gate sites

Both `FlightRecorder.UpdateAnchorDetection` and `BackgroundRecorder.UpdateBackgroundAnchorDetection` lose the bypass call. The flow becomes:

```
1. force-Absolute toggle gate (skip everything if toggle ON)
2. BuildRecordingAnchorCandidateList (already hoisted in PR 901 commit 768fd6e2)
3. nearestSearchCandidates = FilterCandidatesForReFlyProvisional(activeTree, candidates)
4. FindNearestRecordingAnchor(..., nearestSearchCandidates, ...)
5. ShouldUseRelativeFrame -> open/keep/close Relative section
```

The candidate-list hoist (PR 901) is preserved. The hoist's load-bearing side effect (`ConsiderReFlyTreeSamplingProximity` populating `reFlyTreeSamplingProximityMeters`, which gates proximity-tier sampling Full/Half/None at 0-250m/250-500m/500m+ ranges) still runs before either the toggle gate or the filter, so neither early-return can skip it.

### What stays (no deletion in this PR)

- `ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor` (the supersede-target bypass function): orphaned but retained for one release.
- `ApplyReFlyProvisionalAnchorToActiveRecording` (FlightRecorder) and `ApplyReFlyProvisionalAnchorToState` (BackgroundRecorder): orphaned but retained.
- `forceAbsoluteForReFlyProvisional` setting + UI toggle + persistence: retained as a rollback path. Its new meaning: "force fully Absolute even when a real station is nearby" (a strict subset of the new default).
- `AnchorCandidateSource.ReFlyProvisionalSupersede` enum value: orphaned but retained (still referenced by the orphan apply helpers).

A follow-up cleanup PR after one release of soak will delete all of the above.

## Test coverage

- 13 pure xUnit tests for `FilterCandidatesForReFlyProvisional`: every input edge (null candidates, empty candidates, null marker, mismatched provisional id, null/empty same-tree set, only out-of-tree, only in-tree, mixed, supersede target specifically, candidate with empty/null recording id) plus drop-count log emission and no-drop log silence.
- Rewritten source-text gates in `ReFlyAnchorBypassWiringTests`: filter is wired at both recorder sites BEFORE the nearest-search, bypass call is absent from both gate-site method bodies (scoped `IndexOf` with `DoesNotContain`), apply helpers are still defined (so a careless cleanup PR cannot delete them prematurely), pre-check anchor pose still in apply helpers, candidate build still hoisted above the force-Absolute gate.
- Existing tests preserved: `ReFlyAnchorSelectionTests` (TryResolveReFlyProvisionalAnchor branches, kept for the rollback path), `ForceAbsoluteReFlyProvisionalSettingTests` (toggle behavior), `ForceAbsoluteReFlyProvisionalGateInGameTest` (in-game force-Absolute behavior).

## Known limitations

- The narrowed gate runs every physics frame on the candidate list. For typical re-fly scenarios with 5-20 candidates, the filter walks the list once and checks a HashSet membership per candidate (O(N) work, N small). Not a perf concern.
- The same-tree filter is a strict drop. If a future case emerges where a same-tree candidate IS the right anchor (a hypothetical "the supersede target's parent-anchored debris is itself a live persistent vessel" case), the filter would over-drop. No such case exists today; debris recordings are not live persistent vessels by construction.
- Multiple concurrent re-fly sessions are not supported by the marker (it's singular). If KSP ever allows multiple re-flies in parallel, the filter's same-tree logic would need to extend to "any active re-fly tree" rather than "the one re-fly tree."

## Validation plan

- xUnit full suite: 12263/12263 green.
- In-game (manual playtest): rerun the PR 901 validation scenario (Kerbal X re-fly, no nearby real station). Confirm `.prec` authoring is still Absolute by default. Then run a docking-mid-rewind scenario (Kerbal X re-fly within 200m of a real station): confirm `.prec` opens a Relative section against the real station's recording id.
- Log observability: search KSP.log for `FilterCandidatesForReFlyProvisional: dropped=` lines during a re-fly to confirm the filter is firing on the expected frames.
