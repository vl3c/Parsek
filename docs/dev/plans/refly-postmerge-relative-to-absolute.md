# Re-Fly post-merge: promote parent-chain RELATIVE sections to ABSOLUTE

## Status

Plan only. Depends on PR #604 (`fix-refly-upper-stage-absolute`) landing first
because it introduces format v7 and `TrackSection.absoluteFrames`. Branch
`plan-refly-postmerge-rel-to-abs` is forked from that PR's branch.

## Rationale

PR #604 (#623) made parent-chain ghost playback during in-place Re-Fly read
the v7 absolute shadow frames instead of reconstructing position through the
actively-mutating booster anchor. That fixes playback today, but it leaves a
permanent footgun in the data:

1. **Wrong-anchor risk persists indefinitely.** Once the Re-Fly merge
   completes and the booster the upper-stage was anchored to is superseded,
   the relative anchor metadata on the upper stage is dead state. Any future
   reader that doesn't know about the active-Re-Fly bypass — a new playback
   call site, a stand-alone playback mod that ports `GhostPlaybackEngine` —
   will silently use the relative path and lock the upper stage to whatever
   vessel happens to share that PID, or to the recorded anchor source for
   another recording, or to the now-canonical merged tip. The bypass is a
   runtime invariant we have to keep paying attention to forever.

2. **2× sidecar storage on parent-chain RELATIVE sections.** v7 stores both
   `frames` (anchor-local) and `absoluteFrames` (planet-relative) for every
   relative sample. After a Re-Fly merge, the anchor-local copy is
   permanently load-bearing for nothing and just inflates the `.prec` file.

3. **Conceptual cleanup at the right moment.** The Re-Fly merge is the only
   moment we know the relative anchor has just been retired. Doing the
   demotion at any other time is fragile or wrong; doing it at merge is a
   one-shot cost on a code path that already runs the optimizer.

The fix is a new optimizer pass that runs once per Re-Fly merge, walks the
just-merged Re-Fly target's parent-chain ancestry, and promotes RELATIVE
sections whose `anchorVesselId` matches the merged target vessel pid to
ABSOLUTE — copying `absoluteFrames` over `frames`, rewriting the
corresponding flat `Recording.Points` entries, clearing `anchorVesselId`,
and dropping the now-redundant shadow payload.

## When RELATIVE data IS load-bearing — preserved scenarios

The promotion is intentionally narrow because anchor-local RELATIVE data
is the visually correct contract for several existing gameplay scenarios.
The pass must leave them untouched.

**Principle.** RELATIVE data is the right contract when both vessels in
the relationship are ghosts replaying together against the same time
stream. ABSOLUTE (planet-relative) data is the right contract when one
vessel is real (live in the scene) and the other is a ghost — the
re-fly scenario being the canonical case. The Re-Fly merge is the only
moment the codebase knows that an anchor vessel has just transitioned
from "another ghost" to "the live re-flown vessel," so the merge is the
only natural time to flip the contract.

**Scenario 1 — Station approach and docking.** A vessel approaches a
persistent station (the station may be a real loaded vessel, or another
ghost in the same tree). The recorder enters RELATIVE mode inside the
~2300m physics-bubble entry threshold (`FlightRecorder.UpdateAnchorDetection`)
to capture metre-scale anchor-local offsets. From `recording-system-design.md`:
> "Anchor reference data for Phase 3's relative-frame positioning
> (computing ghost position relative to the real vessel for pixel-perfect
> docking replay)."

If the station is a persistent real vessel, the loop replays with the
ghost locked to the station's current pose, with zero positional drift
regardless of how much time has passed. Promotion to ABSOLUTE here would
break visual proximity — the ghost would track the station's
recording-time position instead of its current pose.

**Scenario 2 — Orbital rendezvous, both vessels ghosts.** From
`docs/dev/research/loop-playback-and-logistics.md`:
> "The only scenario where it would matter: looping a specific orbital
> rendezvous or station flyby recording. This is an extreme edge case…"

When two ghosts replay a rendezvous together, both timelines are driven
by the same UT clock. Their relative positions are visually meaningful;
their absolute orbits drift because of orbital mechanics, but the
anchor-local frame keeps them visually together at every UT.

**Scenario 3 — Looped logistics routes.** From `recording-system-design.md`:
> "When a looped segment plays back, it uses the real anchor vessel's
> current position, not the historical position from recording time. This
> means: Zero positional drift between ghost and anchor, regardless of
> how much time has passed. If the anchor vessel has moved (different
> orbit, repositioned base), the ghost adapts automatically."

A tanker that repeatedly docks with a base uses RELATIVE-anchor looping
to stay glued to the base's *current* pose. Promotion here would break
the loop — the ghost would replay against the base's recording-time
pose forever.

**What separates the Re-Fly case.** From `parsek-rewind-staging-design.md`:
> "Enable 'fly the booster back' gameplay: launch AB, stage, fly B to
> orbit, merge, then rewind to the staging moment and fly A down as a
> self-landing booster."

In Re-Fly, the booster anchor stops being a ghost and becomes the live
vessel currently being re-flown along a *different* trajectory. The
parent-chain upper-stage's anchor-local data was captured against the
*original* booster timeline, which the merge has now superseded
post-rewind-UT. The anchor-local copy is dead state for that specific
relationship — but only that relationship. Other RELATIVE sections in
the same recording (e.g., a docking section anchored to a station) stay
load-bearing and stay RELATIVE.

## Scope filter (precise)

The pass walks the parent-chain ancestry of the just-merged Re-Fly
target. For each recording in that ancestry, for each `TrackSection`
on that recording, **promote ONLY when all of the following hold:**

1. `section.referenceFrame == ReferenceFrame.Relative`.
2. `section.anchorVesselId == activeReFlyTargetVesselPid`. (The anchor
   is exactly the re-flown vessel.)
3. `section.absoluteFrames != null` AND
   `section.absoluteFrames.Count == section.frames.Count` AND
   `Count > 0`. (Full v7 shadow coverage; partial-shadow sections are
   skipped with a Verbose log.)

**Explicitly NOT promoted:**

- A RELATIVE section whose `anchorVesselId` is a different vessel pid
  (a station, a base, a docked partner that isn't the re-flown
  vessel). Even if the recording is in the parent-chain ancestry, that
  section's anchor relationship is unaffected by the merge.
- Any RELATIVE section in a recording NOT in the parent-chain
  ancestry.
- Any RELATIVE section in a v6 recording (or v7 recording with
  partial-shadow coverage) — no shadow data to promote against.

**Same-PID loop sections ARE promoted.** A parent-chain recording
whose `LoopAnchorVesselId` matches the active Re-Fly target's pid AND
whose looped section is anchored to that same pid is in scope. The
loop's "ghost adapts to current anchor pose" semantics depended on
the booster's *original* trajectory; post-merge that pid points to
the re-flown vessel's NEW trajectory, so the live-pose anchor is
already broken regardless. Promoting to ABSOLUTE makes the loop
replay against the recorded planet-relative trajectory — which is
self-consistent and what the user observed when the recording was
made. This is the same logic as the rest of the parent-chain
promotion; the loop case is not a special exemption. Pinned by
test 11.

This makes the promotion a strictly narrower transformation than
"convert all RELATIVE to ABSOLUTE." A single recording can come out of
the pass with one promoted ABSOLUTE section (the one anchored to the
re-flown booster) sitting next to an untouched RELATIVE section (the
one anchored to a station). Per-section serialization already handles
that mix.

## What changes

### New optimizer pass

`Source/Parsek/RecordingOptimizer.cs` gets a new internal entry point:

```csharp
internal static int RunReFlyParentChainAbsolutePromotionPass(
    RecordingTree tree,
    List<Recording> recordings,
    string activeReFlyTargetRecordingId,
    uint activeReFlyTargetVesselPid)
```

`tree` is required: the parent-chain ancestry walk relies on
`tree.BranchPoints` to enforce the single-parent predicate and on
`tree.Recordings` to collect same-chain members. Passing it explicitly
keeps the pass off `RecordingStore.CommittedTrees` and makes it
testable with synthetic trees in unit tests. Both call sites need
explicit tree wiring before they can invoke the helper — see the
"Shared promotion step" call-site notes below (`TryCommitReFlySupersede`
needs a signature change to take the tree; the orchestrator does an
inline `CommittedTrees` scan). The pass returns 0 with a `Warn` if
`tree` is null or doesn't contain `activeReFlyTargetRecordingId`.

The pass returns the number of recordings it promoted (for logging) and:

1. Resolves the parent-chain ancestry from `activeReFlyTargetRecordingId`
   using the same predicate as
   `MergeDialog.CollectActiveReFlyParentChainTerminalTipIds` (collect
   chain-mates of the active target via `ChainId`, then walk
   `ParentBranchPointId` to single-parent ancestor branch points, then
   resolve each parent's chain terminal). The function lives in
   `MergeDialog.cs` today; we extract a pure helper into
   `EffectiveState.cs` (so optimizer code can call it without taking a
   `MergeDialog` dependency) and call from both sites. The new helper
   takes `(RecordingTree tree, string activeId)` — same as the existing
   tip collector — so it stays tree-explicit too.

2. For each parent-chain recording, walks its `TrackSections`. For every
   section where `referenceFrame == ReferenceFrame.Relative` AND
   `anchorVesselId == activeReFlyTargetVesselPid`:

   - **Guard**: `absoluteFrames != null` AND
     `absoluteFrames.Count == frames.Count` AND `absoluteFrames.Count > 0`.
     Sections that fail this (v6 recordings promoted to v7 mid-flight, with
     partial shadow payloads) are skipped with a structured `Verbose` log
     line. A counter feeds into the per-recording summary.

   - **Promote in place**:
     - `section.frames = section.absoluteFrames`
     - `section.absoluteFrames = null`
     - `section.anchorVesselId = 0u`
     - `section.referenceFrame = ReferenceFrame.Absolute`

3. **Splice flat `Recording.Points`.** This is the load-bearing correctness
   step that simple "flip the section header" would skip. The flat
   `Recording.Points` list is populated by `FlightRecorder.CommitRecordedPoint`
   after `ApplyRelativeOffset` has mutated the point in place, so for the UT
   range covered by a relative section, `Points[i].latitude/longitude/altitude`
   carry anchor-local metres, not body-fixed coordinates. Any code path that
   reads the flat list without first dispatching through `TrackSection`
   (project CLAUDE.md flags this explicitly) will misread metre-offsets as
   degrees + altitude. The pass therefore:

   - **Match by UT, not by count.** `FlightRecorder.SeedBoundaryPoint`
     intentionally seeds the previous section's last frame into a
     newly-opened section's `frames` list WITHOUT also re-adding the
     point to flat `Recording.Points` (the comment in
     `CommitRecordedPoint` calls this out: "the point is already there
     from SamplePosition"). A valid promoted section will therefore
     have one more `frames[]` entry than there are unique flat
     `Points` entries inside its UT range, because the boundary frame
     duplicates the previous section's tail point. A naive
     count-equals check would roll back every promotion.
   - **Splice algorithm.** For each promoted section:
     1. Binary-search `Recording.Points` for entries with
        `point.ut >= section.startUT && point.ut <= section.endUT`.
     2. For each section frame, find the matching flat-list entry by
        `ut` (within `1e-6` tolerance for floating-point noise — UTs
        are written from the same source so equality should be exact,
        but allow tolerance defensively). The boundary frame at
        `section.startUT` may map to the last point of the *previous*
        section in the flat list — that's expected; either skip it
        (the previous section's flat entry already carries the
        previous section's reference-frame coordinates, which is
        correct for that section, not this one — promoting it would
        corrupt the previous section's data) or, if the previous
        section is also being promoted, both are absolute-shadow
        values and writing either is fine. The simpler rule: skip
        the boundary frame's flat-list overwrite when the previous
        section is NOT being promoted in this pass.
     3. For the rest of the section's frames (non-boundary), overwrite
        the matching flat-list entry's `latitude/longitude/altitude/`
        `rotation` fields with the absolute-shadow values.
   - **Validation invariants.** Before any rewrite lands:
     - Every non-boundary section frame must find a matching
       flat-list entry within tolerance.
     - Every flat-list entry inside `(section.startUT, section.endUT]`
       (excluding the section's start boundary) must have a matching
       section frame.
     - UTs in the flat list within the section's range must be
       monotonic with no duplicates other than the boundary case.
   - **On validation failure.** Roll back the section flip for that
     section, emit a `Warn` with rec id, section index, expected vs
     observed counts, and the first mismatching UT. Partial state is
     not allowed to ship.

4. Calls `recording.MarkFilesDirty()` so the sidecar gets rewritten with
   the new shape.

5. Emits one structured `Info` summary per recording: `parent-chain absolute
   promotion: rec={id} promoted={n} skippedPartialShadow={k}
   skippedNonMatchingAnchor={m} pointsRewritten={p} sessionId={…}`. A
   pass-level `Info` line aggregates totals.

### Wiring into the Re-Fly merge

`MergeDialog.MergeCommit` (`Source/Parsek/MergeDialog.cs:238`) currently
calls `RecordingStore.RunOptimizationPass()` at line 254, then
`TryCommitReFlySupersede()` at line 265. Critically, `TryCommitReFlySupersede`
has TWO completion paths and a naive single-call-site wiring would only
catch one of them:

1. **Placeholder path.** Non-in-place Re-Fly: a fresh provisional
   recording is built and `MergeJournalOrchestrator.RunMerge`
   drives the supersede / tombstone / finalize / RpReap phases.
2. **In-place continuation path.** [MergeDialog.cs:393-660](../Source/Parsek/MergeDialog.cs)
   detects in-place continuation, runs its own
   `SupersedeCommit.AppendRelations` + `FlipMergeStateAndClearTransient`
   + RP reap + `persistent.sfs` durable save sequence, and returns
   `Completed` WITHOUT calling `RunMerge`. The in-place branch
   logs `in-place continuation persisted via persistent.sfs` /
   `in-place continuation skipped durable save`.

The parent-chain promotion needs to run on BOTH paths — in fact the
in-place path is the *primary* target case (the upper-stage relative
data goes stale precisely because the in-place continuation extends
the booster's recording). A pass wired only into the orchestrator
would do nothing for the very scenario this plan exists to fix.

#### Shared promotion step

Extract the call as an internal static helper:

```csharp
internal static int SupersedeCommit.PromoteParentChainRelativeToAbsolute(
    ReFlySessionMarker marker,
    Recording activeReFlyTarget,
    RecordingTree tree)
```

`tree` is required (not optional). Neither call site currently has
the tree in scope at the helper-invocation point in the production
code, so the implementation needs explicit signature/lookup work:

- **`MergeDialog.TryCommitReFlySupersede` is parameterless today**
  (`MergeDialog.cs:327`). The implementation MUST change its
  signature to `internal static ReFlyMergeCommitResult
  TryCommitReFlySupersede(RecordingTree tree)`, and the only
  caller (`MergeDialog.MergeCommit` at `MergeDialog.cs:265`) MUST
  be updated to pass the `tree` it already received as its first
  parameter. `TryCommitReFlySupersede` then forwards `tree` into
  the in-place branch's call to the new helper. Existing tests
  that call `TryCommitReFlySupersede()` directly will need to
  pass an explicit tree (mostly synthetic ones already constructed
  in the fixtures); this is mechanical and is a step in the
  implementation PR, not a separate refactor. Treat the signature
  change as part of the same PR that lands the helper, NOT a
  prerequisite. The signature change is documented here so the
  implementation reviewer expects it.

- **`MergeJournalOrchestrator.RunMerge(ReFlySessionMarker marker,
  Recording provisional)`** also lacks a tree parameter. The
  orchestrator resolves the tree once at the top of the method via
  `provisional.TreeId` against `RecordingStore.CommittedTrees`
  using a small inline scan (the same scan pattern
  `EffectiveState.ResolveChainTerminalRecording` uses today —
  `EffectiveState.cs:1810-1840`). Adding an `internal static
  RecordingTree RecordingStore.FindCommittedTreeById(string)`
  helper is a reasonable one-liner refactor if more than one
  consumer wants it, but it is NOT a prerequisite for this plan;
  the inline scan is small and local. We deliberately do NOT
  change `RunMerge`'s public signature because the orchestrator
  is called from multiple places and a signature change
  ripple-effects more callers than the in-place branch's case.

The helper:
1. Validates inputs (non-null marker, target, tree; target id present
   in `tree.Recordings`). Returns 0 with a `Warn` on failure — the
   merge is allowed to proceed; only the optional cleanup is skipped.
2. Calls `RecordingOptimizer.RunReFlyParentChainAbsolutePromotionPass(
   tree, RecordingStore.CommittedRecordings, marker.ActiveReFlyRecordingId,
   activeReFlyTarget.VesselPersistentId)`.
3. Logs a structured `Info` line whether or not anything was promoted
   ("nothing to promote" is a useful audit landmark — surfaces "we
   checked").
4. Returns the count for the caller's summary log.

#### Call sites

- **In-place continuation path.** `TryCommitReFlySupersede` invokes
  the helper after `FlipMergeStateAndClearTransient` returns and
  BEFORE the `persistent.sfs` durable save block at
  `MergeDialog.cs:643`. That ordering means the durable save in the
  same try-catch persists the promoted sections in one atomic step.
  If the durable save throws, the same catch path that already exists
  ("in-place continuation finalization threw …") logs the failure;
  the in-memory promotion remains, but the on-disk sidecars still
  carry the pre-promotion shape. On next load, `OnLoad` repopulates
  the in-memory state from sidecars and the promotion is naturally
  re-run on the next merge action — the promotion is idempotent so
  this is safe. (If the user never triggers another merge, the
  recording stays in its pre-promotion shape forever, which is
  identical to the current world. No regression.)

- **Placeholder path via the orchestrator.** The existing post-
  `Durable1Done` sequence is `RpReap → MarkerCleared → Durable2Done`
  (`MergeJournalOrchestrator.cs:235-249`); `MarkerCleared` is the
  step that nulls `ParsekScenario.ActiveReFlySessionMarker` and
  must NOT be retired or replaced. The new phase slots **between
  `RpReap` and `MarkerCleared`**, giving final sequence
  `Durable1Done → RpReap → RelativePromotion → MarkerCleared →
  Durable2Done`. `RunMerge` invokes the helper between the existing
  RpReap work and the existing marker-clear step, so the helper has
  the marker available (it needs `marker.SessionId`,
  `ActiveReFlyRecordingId`, and the active target's vessel pid).
  The helper's in-memory section flips and flat-list rewrites ride
  the existing `durable2` save (`MergeJournalOrchestrator.cs:248`)
  onto disk in one atomic step. No new durable save is added; no
  extra I/O cost.

  Also extend three things that come with a new phase:
  1. `MergeJournal.Phases.RelativePromotion` — the persisted phase
     string in `MergeJournal.cs:62-73`, plus `IsPostDurablePhase`
     must include it (line 95-102).
  2. `MergeJournalOrchestrator.Phase.RelativePromotion` — the
     in-memory test-injection enum at `MergeJournalOrchestrator.cs:74`.
  3. `MaybeInject(Phase.RelativePromotion)` immediately after the
     helper returns in `RunMerge`. Tests use `FaultInjectionPoint
     = Phase.RelativePromotion` (the live test seam at
     `MergeJournalOrchestrator.cs:95`) to simulate a crash *inside*
     the new step (vs. before / after).

  **Crash recovery.** A crash anywhere in the post-Durable1 /
  pre-Durable2 window means:
  - On disk: journal phase is `Durable1Done` (the last phase that
    had a durable save; `RpReap`, `RelativePromotion`, and
    `MarkerCleared` advances are in-memory only). Sidecars hold
    the pre-promotion shape. The active marker is **still alive on
    disk** because `ParsekScenario`'s last save was at durable1,
    before MarkerCleared nulled it.
  - On reload: `RecordingStore` repopulates from sidecars to the
    pre-promotion in-memory state. `ActiveReFlySessionMarker`
    deserializes back to its alive form. `RunFinisher` detects a
    post-Durable1 phase and walks forward through
    `CompleteFromPostDurable`.

  **Critical finisher-side change.** The existing finisher pattern
  (`CompleteFromPostDurable`, `MergeJournalOrchestrator.cs:355-393`)
  only walks phase markers forward — it does NOT re-execute the
  work for `RpReap` or `MarkerCleared`. That works for those phases
  because their work is either idempotent state mutation
  (`MarkerCleared` nulls a field) or one-shot cleanup that the
  reaper handles separately (`RpReap`'s RP file deletion). The
  promotion helper is different: its work is load-bearing on the
  in-memory state captured by the eventual `durable2` save. If the
  finisher just sets `phase=RelativePromotion` without calling the
  helper, the next `durable2` write will persist pre-promotion
  sidecars — defeating the entire purpose.

  Therefore `CompleteFromPostDurable` MUST invoke the helper when
  walking from `Durable1Done` (or `RpReap`) past
  `RelativePromotion`. The finisher reads
  `scenario.ActiveReFlySessionMarker` (still alive on disk per
  above), resolves the tree from `journal.TreeId` via the inline
  `CommittedTrees` scan, looks up the active target recording from
  `tree.Recordings[marker.ActiveReFlyRecordingId]`, and calls
  `SupersedeCommit.PromoteParentChainRelativeToAbsolute`. The
  helper is idempotent so a second invocation on already-promoted
  data is a structured "nothing to promote" log line and zero
  mutation. Then the finisher advances through `MarkerCleared`
  (which nulls the marker — the marker is no longer needed because
  promotion just used it) and `Durable2Done` (which fires a
  `finisher-durable2` save), landing the promoted sidecars on disk.

  This is a deliberate deviation from the no-op finisher pattern
  for the existing intermediate phases; document the deviation in
  the orchestrator code comment so a future refactor doesn't
  silently drop the helper invocation. Alternative considered:
  fold the helper into `RpReap`'s body (no new phase), but then
  the test seam is `Phase.RpReap` for both the existing RP work
  and the new promotion — harder to debug regressions in one vs
  the other. The explicit phase wins on (a) clear log-grep landmark,
  (b) targeted fault injection, (c) the redrive contract being
  obvious from the orchestrator code.

#### Why not unify the two paths instead?

In-place vs placeholder is a deliberate architectural choice
documented at `MergeDialog.cs:417-441` — the in-place branch
intentionally bypasses the orchestrator because it has different
finalization needs (no provisional add/remove, transient origin
metadata, single durable save). Routing in-place through the
orchestrator would be a much larger refactor and out of scope for
this plan. The shared-helper approach is the minimal correct change.

The optimizer pass is invoked via the helper rather than from
`RecordingStore.RunOptimizationPass` because it's Re-Fly-specific
state (active target id + pid + tree) and shouldn't run on plain
merges or periodic optimizer ticks.

### Helper extraction

`MergeDialog.CollectActiveReFlyParentChainTerminalTipIds` already walks
the parent-chain ancestry but returns *terminal tips* of single-parent
ancestor chains. We need a slightly broader walk — every recording in the
parent-chain ancestry, not just the terminal tip — so we can hit
intermediate chain segments that also carry RELATIVE sections to the
booster.

Extract a new pure helper:

```csharp
internal static HashSet<string>
    EffectiveState.CollectActiveReFlyParentChainRecordingIds(
        RecordingTree tree,
        string activeReFlyTargetId);
```

Same predicate as the existing tip collector, but emits all chain members
of every parent chain it walks (via `CollectSameChainRecordingIds`). The
existing tip collector becomes a thin filter over this richer set. Tests
for the existing collector continue to pass with no behavior change; new
tests pin the broader walk.

### v7 format-version handling

No bump. v7 already supports mixed reference frames in a single recording
— a recording can hold one promoted ABSOLUTE section (no shadows) next to
an unpromoted RELATIVE section (with shadows). Per-section serialization
in `RecordingStore.SerializeTrackSections` already gates `ABSOLUTE_POINT`
writes on `referenceFrame == Relative` (line 5202-5210), so promoted
sections naturally stop emitting them. `IsAcceptableSidecarVersionLag`
contracts are unchanged.

### CHANGELOG / todo entries

CHANGELOG entry for v0.8.4 (or next version):
> Re-Fly merge now permanently promotes parent-chain upper-stage RELATIVE
> trajectory sections to ABSOLUTE during the merge journal, eliminating
> the dead anchor metadata and halving the post-merge sidecar size for
> those sections.

Open a new entry in `docs/dev/todo-and-known-bugs.md` titled "Post-merge
parent-chain relative-to-absolute promotion" referencing this plan, and
close it in the implementation PR.

## Tests

All unit tests; no in-game runtime test required (the conversion is pure
data transformation).

### `RecordingOptimizerReFlyPromotionTests` — new file

1. **Promotes single matching section.** Parent-chain recording with one
   RELATIVE section anchored to the active Re-Fly target, full shadow
   coverage. Assert section is now ABSOLUTE, `anchorVesselId == 0`,
   `frames` count matches the original `absoluteFrames` count, original
   anchor-local metre values are gone, flat `Recording.Points` entries in
   the section's UT range now hold the absolute shadow values.

2. **Skips partial-shadow section.** RELATIVE section where
   `absoluteFrames.Count < frames.Count`. Assert section stays RELATIVE
   and a `Verbose` skip line is logged with `skippedPartialShadow=1`.

3. **Skips section anchored to different vessel.** Recording with two
   RELATIVE sections — one anchored to the Re-Fly target (promotes),
   one anchored to a station PID (stays RELATIVE). Assert mixed final
   state.

4. **Skips recording outside parent-chain.** Sibling recording with
   identical RELATIVE shape but not in the parent-chain walk. Assert
   no promotion.

5. **Idempotent.** Run the pass twice. Second run reports `promoted=0`
   for every recording.

6. **Flat-list rewrite alignment failure.** Synthetic recording where
   `frames` and `Points` UT ranges don't align (constructed test
   fixture). Assert the section flip rolls back, no partial state, and
   a `Warn` line is logged.

7. **Recording with all-promoted sections.** Recording whose every
   RELATIVE section is in scope. Assert all promoted, `MarkFilesDirty`
   called once.

8. **Anchor pid 0 is skipped.** RELATIVE section with
   `anchorVesselId == 0` (e.g., recorder bug, or an old recording where
   anchor was never set). Assert no promotion attempt.

9. **Preserved scenario — station docking section stays RELATIVE.**
   Parent-chain recording with two RELATIVE sections: one anchored to
   the active Re-Fly target's pid (promotes), one anchored to a station
   pid that is itself a separate ghost recording in the same tree
   (stays RELATIVE with full anchor metadata intact). Assert the
   station section's `anchorVesselId`, `frames`, and `absoluteFrames`
   are unchanged. This pins the docking/rendezvous preservation
   invariant — the section the user explicitly cares about is
   untouched.

10. **Preserved scenario — looped section anchored to non-Re-Fly
    vessel.** Recording with `LoopAnchorVesselId == station_pid`,
    looped-segment RELATIVE section anchored to `station_pid`. Active
    Re-Fly target's pid is different. Assert the section stays
    RELATIVE and the loop continues to point at the station anchor.

11. **Same-PID loop section IS promoted.** Parent-chain recording
    with `LoopAnchorVesselId == activeReFlyTargetPid` AND looped
    RELATIVE section anchored to the same pid. Assert the section is
    promoted to ABSOLUTE, `anchorVesselId == 0`, the loop now uses
    the absolute frames (per the rule documented in the scope
    section). This pins the policy decision and prevents a future
    refactor from silently skipping same-pid loops.

12. **Boundary-seeded section frame does not trigger rollback.**
    Synthesize a parent-chain recording where the promoted RELATIVE
    section starts with a boundary-seeded frame (frame[0] duplicates
    the previous section's last UT, but the flat `Recording.Points`
    list has only one entry at that UT). Assert: section is
    promoted, the boundary frame's flat-list overwrite is skipped
    (because the previous section is NOT being promoted), all other
    section frames map cleanly to flat-list entries by UT, no
    rollback. Asserts on log line: `pointsRewritten = frames.Count - 1`
    when the boundary case applies.

13. **Two consecutive promoted sections share the boundary frame.**
    Same recording with two adjacent RELATIVE sections both anchored
    to the active target. Assert: both sections promoted, the shared
    boundary frame's flat-list entry is overwritten exactly once
    (either section can claim it; pick the second so the first
    section's frames don't write over the second section's start
    point — see splice-algorithm step 2 in the plan body).

### `EffectiveStateTests` additions

1. **`CollectActiveReFlyParentChainRecordingIds` returns all chain
   members.** Topology: parent chain head→middle→tip, child chain
   head→tip (active target). Expected: all four parent chain members,
   no child chain members.

2. **Multi-parent branch points are skipped.** Same predicate gate as
   the existing terminal-tip collector. (This test already exists for
   the tip collector; mirror it for the broader walk.)

3. **Active target with no parent chain returns empty set.**

### `MergeJournalOrchestratorTests` additions

1. **`RelativePromotion` phase fires between `RpReap` and
   `MarkerCleared` (placeholder path).** Placeholder Re-Fly merge
   (NOT in-place) with parent-chain recordings to promote. Assert
   journal advances
   `Durable1Done → RpReap → RelativePromotion → MarkerCleared →
   Durable2Done`, the helper logs the structured promotion summary
   between the `RpReap` and `MarkerCleared` log landmarks, and the
   `durable2` save fires AFTER the in-memory promotion landed (so
   the on-disk sidecars carry the promoted shape). The
   `MarkerCleared` step still runs and still nulls
   `ParsekScenario.ActiveReFlySessionMarker` — assert the marker
   is null after `RunMerge` returns. This pins that the new phase
   does NOT skip or replace `MarkerCleared`.

2. **Crash INSIDE `RelativePromotion` redrives the pass on reload.**
   Use the existing `FaultInjectionPoint = Phase.RelativePromotion`
   seam (live name at `MergeJournalOrchestrator.cs:95`) so
   `MaybeInject` throws AFTER the helper has run but BEFORE the
   next `AdvancePhase(MarkerCleared)`. Equivalent variant: throw
   from inside the helper itself via a hook on
   `RecordingOptimizer.RunReFlyParentChainAbsolutePromotionPass`.
   Both must produce the same recovery behavior, so include one
   test for each variant (drives both the "advance happened but
   helper completed" and "advance didn't happen because helper
   threw" paths through the same finisher).

   Expected on-disk persisted state right after the crash:
   - Journal phase = **`Durable1Done`** — the last phase that had
     a durable save. `RpReap` / `RelativePromotion` advances are
     in-memory only.
   - `ParsekScenario.ActiveReFlySessionMarker` = **alive** (last
     persisted at the durable1 save, before MarkerCleared would
     null it).
   - Sidecars = pre-promotion shape.

   Reload: `RecordingStore` repopulates from sidecars to RELATIVE.
   `ActiveReFlySessionMarker` deserializes back alive. `RunFinisher`
   sees `Durable1Done` and routes to `CompleteFromPostDurable`,
   which walks forward through `RpReap` → `RelativePromotion`
   (**finisher invokes the helper using the still-alive marker
   and `journal.TreeId`**, not just an AdvancePhase no-op — see
   the "Critical finisher-side change" note in the plan body) →
   `MarkerCleared` → `Durable2Done` (`finisher-durable2` save).
   Final on-disk state: promoted sidecars, journal cleared, marker
   null. Assert the helper's INFO line reports `promoted > 0` on
   the redrive (NOT "nothing to promote") because the
   rebuilt-from-sidecar state still carried the unpromoted RELATIVE
   sections.

3. **Crash AFTER helper completes but BEFORE `durable2` save.**
   This pins the post-promotion / pre-Durable2 window. Two
   distinct injection points exercise different orchestrator
   branches:

   3a. `FaultInjectionPoint = Phase.MarkerCleared`. Fires after
      the helper ran AND after `MarkerCleared` advance + marker
      null in memory, but BEFORE `AdvancePhase(Durable2Done) /
      durable2` save. On disk: phase=`Durable1Done`, marker still
      alive (last save was durable1 before in-memory marker null).
      Reload runs the finisher; helper re-executes (idempotent —
      but the sidecars are pre-promotion because durable2 never
      fired, so it does real work again). Final state = promoted.

   3b. `DurableSaveForTesting = label => throw` for `label ==
      "durable2"`. Fires inside the durable2 save call itself.
      In-memory state at moment of throw: phase has been advanced
      to `Durable2Done`, marker is null, sections are promoted.
      But none of that is on disk — the save itself failed. On
      disk: phase=`Durable1Done`, marker alive, sidecars
      pre-promotion. Reload behaves identically to 3a; the
      redrive runs the helper again and the `finisher-durable2`
      save lands the promoted shape.

   These are NOT duplicates of #2: the orchestrator's catch and
   log paths between "helper threw" (#2 helper-throw variant),
   "MaybeInject(MarkerCleared) threw" (3a), and "DurableSave
   threw" (3b) all differ, and a future refactor that moves the
   helper invocation could regress one path while passing the
   others. Test 3 is intentionally NOT injecting at `Phase.RpReap`
   — that fires before the helper runs and is already covered by
   the "fall-through" semantics of test 2's helper-throw variant.

4. **Reload from `Durable2Done` does NOT redrive promotion.** Run
   `RunMerge` end-to-end successfully through `durable2` save,
   then crash before the journal-clear / `durable3` save. On disk:
   journal phase = `Durable2Done`, sidecars are already promoted,
   marker is cleared (durable2 captured the post-MarkerCleared
   state). Reload: `RunFinisher` sees `Durable2Done`. The existing
   `CompleteFromPostDurable` does NOT walk earlier post-Durable1
   phases from `Durable2Done` — assert the finisher only runs the
   final clear / journal cleanup and returns. Specifically assert
   `RecordingOptimizer.RunReFlyParentChainAbsolutePromotionPass`
   is **never invoked** on this reload path (use a test-side hook
   counter to detect a phantom call). The helper's idempotency on
   already-promoted data is covered by optimizer-pass test 5
   (direct second-run), so we don't double-cover it here.

### `MergeDialogTests` / in-place branch wiring

These pin the P1 review fix — the in-place branch must call the
shared promotion helper.

1. **In-place continuation merge promotes parent-chain sections.**
   Set up an in-place continuation merge with a parent-chain recording
   carrying RELATIVE sections anchored to the active target's pid.
   Run `TryCommitReFlySupersede` end-to-end (in-place branch). Assert
   the parent-chain recording's RELATIVE sections are now ABSOLUTE,
   `MarkFilesDirty` was called, and the structured INFO log line
   `[Supersede] in-place parent-chain absolute promotion: …` appears.

2. **In-place continuation merge with no parent-chain logs "nothing
   to promote".** In-place merge whose tree has no parent chain (the
   active target has no parent branch points). Assert the helper is
   still invoked and logs `nothing to promote` — this is the audit
   landmark that proves the wiring fired.

3. **Placeholder path also promotes via the orchestrator.** Same
   topology but non-in-place; assert the orchestrator's
   `RelativePromotion` phase fired and the same end state was
   reached. Together with #1 this pins "both paths run the pass."

4. **In-place finalization throws after promotion.** Inject a hook
   that throws inside the `persistent.sfs` durable save block. Assert
   the in-memory promotion is still applied (the in-memory state
   matches the user's expectations), the catch block logs the
   failure, and the on-disk sidecars retain their pre-promotion shape
   (the recording's `MarkFilesDirty` flag is still set so the next
   successful save will rewrite them). This pins the documented
   behavior: a failed durable save doesn't corrupt anything; it just
   defers the on-disk rewrite.

### `SessionMergerTests` / `RecordingOptimizerTests` regression

Existing v7 shadow-frame round-trip and merge-overlap tests should keep
passing. No changes expected.

## Out of scope

- **Old recordings without v7 shadows.** A v6 recording with only
  anchor-local frames and no shadows can never be promoted by this
  pass. That is fine — those recordings predate this code path; they
  continue to use the existing recorded-anchor reconstruction logic
  (which works for them because their anchor wasn't a Re-Fly target).
  The pass logs `skippedPartialShadow` for them and moves on.

- **Non-Re-Fly merges.** Plain `MergeCommit` without an active session
  marker doesn't trigger the pass. The dead-anchor concern only applies
  when an anchor vessel has been superseded, which only happens in
  Re-Fly merges.

- **Looped sections anchored to non-Re-Fly vessels.** Logistics
  routes that loop on a station or base — the typical
  `LoopAnchorVesselId == station_pid` case — stay RELATIVE because
  their `anchorVesselId` doesn't match the active Re-Fly target's
  pid. The scope filter handles this naturally; test 10 pins it.
  The same-pid loop case (a parent-chain recording that loops on the
  re-flown vessel's pid) IS promoted — see the scope filter's
  "Same-PID loop sections ARE promoted" note and test 11 for the
  rationale and pinning.

- **Docking and rendezvous sections.** RELATIVE sections anchored to
  station/base/partner-vessel pids stay RELATIVE under the scope
  filter (item 2). Test 9 pins the mixed-section recording case
  (one promotable + one preserved on the same recording) so a
  future change can't silently widen the filter.

- **In-place editing of the recorder.** `FlightRecorder` keeps writing
  v7 shadows as before. The pass only runs on already-committed
  recordings during merge.

## Risks

- **Flat-list / sections de-sync.** The most likely bug. Mitigation:
  the alignment-check rollback in step 3 of the pass, plus the
  flat-list-rewrite-alignment-failure test. We could also consider
  computing flat-list overwrites first, validating, and only then
  flipping the section header — that's more code but safer; defer the
  decision to implementation review.

- **Storage savings less than expected if many recordings have only
  partial shadows.** Worth measuring once on a real save — sample
  a long parent-chain recording, count v6→v7 promotion sections vs.
  fully-v7 sections, see how much the pass actually trims.

- **Future loaders that synthesize a recording from sidecar without
  re-running the optimizer pass.** The promotion is durable on disk —
  once the sidecar is rewritten, nothing needs to re-run. The pass is
  one-shot per merge.

## Open questions

1. ~~Is there a meaningful UX difference between "the upper stage ghost
   continues to render via the absolute shadow path forever" (current
   PR #604 behavior) vs "the upper stage ghost is now a plain absolute
   recording" (this plan)? Functionally no, but the plan eliminates a
   class of future bug — worth confirming with the user before we
   build it.~~ **Resolved (2026-04-26):** user confirmed the
   absolute-only contract for the post-merge upper-stage case;
   docking/rendezvous/looped logistics scenarios stay RELATIVE per
   the scope filter above.

2. Should the pass also run during the `RecordingOptimizer` periodic
   tick (with a guard that requires an active Re-Fly marker)? Probably
   not — adds complexity, and the merge is the only natural promotion
   point. Defer.

3. **Sub-section trimming for partially-affected RELATIVE sections.**
   If an upper-stage RELATIVE section spans a UT range that crosses
   the rewind UT (frames before the rewind are valid against the
   preserved booster timeline; frames after are invalid against the
   replaced one), the all-or-nothing promotion this plan describes
   throws away the pre-rewind anchor-local fidelity even though it's
   still correct. A sub-section split — promote post-rewind frames
   only, keep pre-rewind RELATIVE — is theoretically possible but
   likely overkill: in practice the upper-stage was decoupled BEFORE
   the rewind UT, so its RELATIVE-to-booster section ended at
   decouple time and doesn't span the rewind UT at all. Confirm with
   a real save before adding the split logic; defer otherwise.
