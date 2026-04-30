# Re-Fly post-merge: promote parent-chain RELATIVE sections to ABSOLUTE

## Status

Plan only. The original dependency on PR #604
(`fix-refly-upper-stage-absolute`) is now satisfied on `main`: format v7
introduced `TrackSection.absoluteFrames`, and current `main` has since
advanced the recording format to v10. This branch was merged with
`origin/main` on 2026-04-30.

After that merge, this plan should be read as a durable post-merge
canonicalization and storage-cleanup proposal, not as the only runtime
visual fix. Current `main` already has runtime safeguards that reduce the
urgency of this pass:

- `ParsekFlight.TryGetAbsoluteSectionPlaybackFrames` makes playback and
  watch-position resolution prefer section-local ABSOLUTE frames instead
  of falling through to adjacent flat `Recording.Points` samples.
- `ParsekFlight` / `RelativeAnchorResolution` now detect stale live
  relative anchors and prefer recorded anchor poses when the live anchor
  has drifted far from the recorded pose.
- The active Re-Fly relative-anchor bypass is now broader than the
  original parent-chain-only shape: same-PID sibling and cousin victims can
  be protected at runtime.

The remaining value is still real, but narrower: promote the
high-confidence parent-chain subset once the merge makes its anchor-local
copy dead state, keep flat fallback data in sync, drop redundant shadow
payloads, and remove one class of future reader / tooling footgun.

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
2. `activeReFlyTargetVesselPid != 0u`. A zero active-target pid is
   malformed / unresolved identity, not a valid anchor match.
3. `section.anchorVesselId == activeReFlyTargetVesselPid`. (The anchor
   is exactly the re-flown vessel.)
4. `section.absoluteFrames != null` AND
   `section.absoluteFrames.Count == section.frames.Count` AND
   `Count > 0`. (Full v7 shadow coverage; partial-shadow sections are
   skipped with a Verbose log.)

**Explicitly NOT promoted:**

- A RELATIVE section whose `anchorVesselId` is a different vessel pid
  (a station, a base, a docked partner that isn't the re-flown
  vessel). Even if the recording is in the parent-chain ancestry, that
  section's anchor relationship is unaffected by the merge.
- Any RELATIVE section with `anchorVesselId == 0`, even if the active
  Re-Fly target's vessel pid is also zero. Zero is "not set" and is
  never treated as an equality match.
- Any RELATIVE section in a recording NOT in the parent-chain
  ancestry.
- Any RELATIVE section in a v6 recording (or v7 recording with
  partial-shadow coverage) — no complete shadow data to promote
  against. These are skipped explicitly and remain on the runtime
  recorded-anchor / fallback playback path; the pass does not claim to
  eliminate their dead-anchor risk.

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
    IReadOnlyList<Recording> recordings,
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

`recordings` is `IReadOnlyList<Recording>` (matching the public type
of `RecordingStore.CommittedRecordings` at `RecordingStore.cs:314`)
so callers can pass `RecordingStore.CommittedRecordings` directly
without going through a backing-list accessor that doesn't exist.
The pass MUTATES individual `Recording` objects (flipping
`TrackSection.referenceFrame`, replacing `frames`, splicing
`Recording.Points` entries) but does NOT add to or remove from the
outer collection — `IReadOnlyList<Recording>` is sufficient.

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
     from SamplePosition"). The number of flat entries a promoted
     section rewrites therefore depends on boundary ownership, not just
     `frames.Count`: a seeded `startUT` is still one existing flat
     entry owned by the new section, while a shared `endUT` is owned by
     the following section and is excluded from this section's rewrite.
     A naive section-wide count check would roll back valid
     promotions.
   - **Boundary ownership rule follows `FindTrackSectionForUT`.**
     `TrajectoryMath.FindTrackSectionForUT` treats all non-final
     sections as `[startUT, endUT)` and the final section as
     `[startUT, endUT]`. Therefore an exact shared boundary UT
     belongs to the section STARTING at that UT, not to the previous
     section ending there.
     - A promoted section always owns its `startUT` flat entry when
       that entry exists, even when it was seeded from the previous
       section.
     - A promoted section owns its `endUT` entry only when no following
       TrackSection starts at the same UT. If the next section shares
       the boundary, the next section owns that flat entry.
     - Interior frame UTs are owned by the promoted section.
     This "lookup-owner" rule keeps flat-list repair aligned with the
     runtime section dispatcher. A shared boundary is still overwritten
     at most once: by the next section if it is promoted, or by nobody
     if that next section remains RELATIVE and therefore still owns a
     relative-coordinate boundary point. This can leave the previous
     section's nominal end boundary flat entry in the next section's
     coordinate contract; that is correct only because compliant readers
     must first dispatch the UT through `FindTrackSectionForUT` before
     interpreting flat point coordinates.
   - **Splice algorithm (two-pass to stage decisions before
     mutation).** Compute all ownership decisions from the
     PRE-promotion section metadata, then apply mutations.
     Otherwise the first promoted section's flip changes the neighbor
     metadata seen by later sections and can make the shared-boundary
     ownership decision depend on mutation order.

     Pass 1 — plan: snapshot the pre-promotion section list and
     promotion eligibility before any mutation. For each section that
     meets all promotion guards, compute:
     - `claimsStartUT = true`. Exact section starts dispatch to the
       current section, so a seeded `frames[0]` boundary must be
       repaired by this section when this section promotes. This remains
       true even if the previous section stays RELATIVE: the previous
       non-final section's dispatch interval excludes the exact boundary.
     - `claimsEndUT = true` iff there is no next TrackSection whose
       `startUT` matches this section's `endUT` within the UT
       tolerance. When the next section shares the boundary, the next
       section owns that flat entry under `FindTrackSectionForUT`.
     Build the per-section `flatRange` from the snapshot:
     `flatRange = [section.startUT,
                   claimsEndUT ? section.endUT : lastUTBeforeEndUT]`
     (right endpoint resolved via binary search on `Recording.Points`
     for the last UT strictly before `section.endUT` when
     `claimsEndUT` is false).

     Pass 2 — execute: for each promoted section, in increasing
     `startUT` order:
     1. Flip the section header in place
        (`section.referenceFrame = Absolute`,
         `section.frames = section.absoluteFrames`,
         `section.absoluteFrames = null`,
         `section.anchorVesselId = 0u`).
     2. Binary-search `Recording.Points` for entries with `ut` in
        the section's pre-computed `flatRange`. For each matched
        flat entry, find the section frame with the same `ut`
        (within `1e-6` tolerance) and overwrite the flat entry's
        `latitude/longitude/altitude/rotation/velocity` with the
        section frame's value (which is now the absolute-shadow
        value after the header flip).
   - **Validation invariants.** Before any rewrite lands:
     - Every section frame at UT in `flatRange` must find a matching
       flat-list entry within tolerance.
     - Every flat-list entry inside `flatRange` must have a matching
       section frame.
     - UTs in the flat list within `flatRange` must be strictly
       monotonic. The shared end-boundary duplicate, if any, is
       excluded by the half-open `flatRange` right-end when
       `claimsEndUT` is false.
     - `pointsRewritten` reported per section equals the count of
       successful overwrites — exactly `frames.Count` when
       `claimsEndUT` is true, exactly `frames.Count - 1` when it is
       false (the shared end-boundary frame is omitted because the
       next section owns it).
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
  Recording provisional)`** also lacks a tree parameter. Add a small
  helper and use it in BOTH the no-crash `RunMerge` path and the
  load-time finisher so there is a single lookup contract:

  ```csharp
  internal static RecordingTree FindCommittedTreeById(string treeId)
  {
      if (string.IsNullOrEmpty(treeId)) return null;
      for (int i = 0; i < committedTrees.Count; i++)
          if (string.Equals(committedTrees[i]?.Id, treeId, StringComparison.Ordinal))
              return committedTrees[i];
      return null;
  }
  ```

  `RunMerge` resolves once near the top:

  ```csharp
  RecordingTree treeForRunMerge =
      RecordingStore.FindCommittedTreeById(provisional.TreeId)
      ?? RecordingStore.FindCommittedTreeById(marker.TreeId);
  ```

  We deliberately do NOT change `RunMerge`'s public signature because
  the orchestrator is called from multiple places and a signature
  change ripple-effects more callers than the in-place branch's case.

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

  Also extend four things that come with a new phase:
  1. `MergeJournal.Phases.RelativePromotion` — the persisted phase
     string in `MergeJournal.cs:62-73`, plus `IsPostDurablePhase`
     must include it (line 95-102).
  2. `MergeJournalOrchestrator.Phase.RelativePromotion` — the
     in-memory test-injection enum at `MergeJournalOrchestrator.cs:74`.
  3. **Explicit `RunMerge` advance.** After the existing RpReap
     work block (line 235-241) and BEFORE the existing
     `AdvancePhase(scenario, MergeJournal.Phases.MarkerCleared)`
     call (line 243), insert in `RunMerge`:
     ```csharp
     // NEW: invoke promotion helper while marker is still alive.
     SupersedeCommit.PromoteParentChainRelativeToAbsolute(
         scenario.ActiveReFlySessionMarker, provisional, treeForRunMerge);
     AdvancePhase(scenario, MergeJournal.Phases.RelativePromotion);
     MaybeInject(Phase.RelativePromotion);
     ```
     The advance is the load-bearing part — without it, the
     persisted journal phase never transitions through
     `RelativePromotion` in the no-crash path, and recovery from
     a `RelativePromotion`-tagged journal would never happen.
     `treeForRunMerge` is resolved at the top of `RunMerge` via
     `RecordingStore.FindCommittedTreeById(provisional.TreeId) ??
     RecordingStore.FindCommittedTreeById(marker.TreeId)` (see the
     shared promotion step tree-lookup notes above).
  4. Tests use `FaultInjectionPoint = Phase.RelativePromotion`
     (the live test seam at `MergeJournalOrchestrator.cs:95`) to
     simulate a crash *inside* the new step (vs. before / after).

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

  **Finisher-side wiring (matches existing pattern).** The existing
  `CompleteFromPostDurable` (`MergeJournalOrchestrator.cs:355-393`)
  already re-executes real work for the post-Durable1 phases — not
  just walking markers. From `Durable1Done` it runs
  `TagRpsForReap(scenario.ActiveReFlySessionMarker, scenario)` and
  `RewindPointReaper.ReapOrphanedRPs()` before advancing to
  `RpReap` (lines 360-366); from `RpReap` it nulls
  `scenario.ActiveReFlySessionMarker`, calls
  `BumpSupersedeStateVersion`, and emits the `End reason=merged`
  log before advancing to `MarkerCleared` (lines 368-381). The new
  helper invocation slots into this same pattern: when
  `journal.Phase == RpReap`, the finisher runs the helper BEFORE
  the marker-clear block (so the marker is still alive when the
  helper reads it), then continues with the existing marker-clear
  work, then advances to `MarkerCleared`. This is NOT a deviation
  from the existing finisher behavior — it's the same "re-execute
  load-bearing work + advance the phase" structure already used by
  the RpReap branch.

  Concretely the new finisher code (net472-compatible —
  `Dictionary.GetValueOrDefault` does NOT exist on this target
  framework, so use `TryGetValue`). The structure inserts a NEW
  block transitioning `RpReap → RelativePromotion`, splits the
  existing `RpReap` block's marker-null work to a NEW
  `RelativePromotion` block, AND adds a defensive helper
  invocation for the rare path where the journal was persisted
  with `Phase = RelativePromotion` directly:

  ```csharp
  // Block 1 — EXISTING: Durable1Done -> RpReap (RP cleanup).
  if (fromPhase == MergeJournal.Phases.Durable1Done)
  {
      TagRpsForReap(scenario.ActiveReFlySessionMarker, scenario);
      RewindPointReaper.ReapOrphanedRPs();
      AdvancePhase(scenario, MergeJournal.Phases.RpReap);
      stepsDriven++;
  }

  // Block 2 — NEW: RpReap -> RelativePromotion (helper).
  // Marker must be alive here; it is, because (a) durable1 saved
  // marker-alive, and (b) reaching this block means the in-memory
  // marker-null work hasn't run yet (that's now in Block 3).
  if (journal.Phase == MergeJournal.Phases.RpReap)
  {
      RunPromotionHelper(scenario, journal);  // see helper inline below
      AdvancePhase(scenario, MergeJournal.Phases.RelativePromotion);
      stepsDriven++;
  }

  // Block 3 — MOVED FROM PRIOR `RpReap` block + DEFENSIVE helper:
  // RelativePromotion -> MarkerCleared (marker null).
  if (journal.Phase == MergeJournal.Phases.RelativePromotion)
  {
      // Defensive: if we entered the finisher with phase already at
      // RelativePromotion (no-crash path never persists this — the
      // next durable save advances to Durable2Done first — but
      // an external mid-run save could) the helper hasn't yet
      // landed promoted sidecars on disk. Re-run idempotently.
      // When entering via Block 2 instead, the helper just ran;
      // this re-run is a "nothing to promote" no-op. Cost: one
      // dictionary lookup per parent-chain recording.
      if (fromPhase == MergeJournal.Phases.RelativePromotion)
      {
          RunPromotionHelper(scenario, journal);
      }

      // EXISTING marker-null work (was inside the old `RpReap`
      // block at MergeJournalOrchestrator.cs:368-381).
      if (scenario.ActiveReFlySessionMarker != null)
      {
          string provisionalId =
              scenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId
              ?? "<no-id>";
          ParsekLog.Info("ReFlySession",
              $"End reason=merged sess={sessionId} provisional={provisionalId}");
      }
      scenario.ActiveReFlySessionMarker = null;
      scenario.BumpSupersedeStateVersion();
      AdvancePhase(scenario, MergeJournal.Phases.MarkerCleared);
      stepsDriven++;
  }

  // Block 4 — EXISTING: MarkerCleared -> Durable2Done (deferred save).
  if (journal.Phase == MergeJournal.Phases.MarkerCleared)
  {
      AdvancePhase(scenario, MergeJournal.Phases.Durable2Done);
      DurableSave("finisher-durable2", persistSynchronously: false);
      stepsDriven++;
  }

  // RunPromotionHelper inline (deduplicated):
  static void RunPromotionHelper(ParsekScenario scenario, MergeJournal journal)
  {
      var marker = scenario.ActiveReFlySessionMarker;
      // If RelativePromotion is somehow persisted with a null marker,
      // this is the same unrecoverable imprecise-scope corner as
      // MarkerCleared-with-pre-promotion-sidecars; see Out of scope.
      if (marker == null) return;
      RecordingTree tree = RecordingStore.FindCommittedTreeById(journal.TreeId);
      Recording activeTarget = null;
      if (tree?.Recordings != null
          && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
      {
          tree.Recordings.TryGetValue(
              marker.ActiveReFlyRecordingId, out activeTarget);
      }
      if (tree != null && activeTarget != null)
      {
          SupersedeCommit.PromoteParentChainRelativeToAbsolute(
              marker, activeTarget, tree);
      }
  }
  ```

  **Behavioral contract changes from the existing finisher:**
  - The marker-null + `End reason=merged` log + `BumpSupersedeStateVersion`
    work moves from `if (journal.Phase == RpReap)` to `if (journal.Phase
    == RelativePromotion)`. Existing tests that crash at `Phase.RpReap`
    and assert marker-null still pass because in the same finisher run,
    Block 2 advances RpReap → RelativePromotion and Block 3 runs the
    marker-null work; the end state is identical to today's. Update
    log-line assertions if any test pinned the exact "marker null
    happens during RpReap-block" log ordering — the new ordering is
    helper INFO line → marker-null INFO line.
  - A journal that persists `Phase = RelativePromotion` is now a
    recoverable state (per `IsPostDurablePhase`). The defensive
    re-invocation in Block 3 ensures redrive promotes sidecars even
    in that path.
  - A journal that persists `Phase = RelativePromotion` with
    `ActiveReFlySessionMarker == null` is treated as the same
    unrecoverable imprecise-scope corner as `MarkerCleared` with
    pre-promotion sidecars: the helper deliberately returns without
    guessing, Block 3 still advances marker-clear bookkeeping, and
    the broader load-time `Warn` described in "Out of scope" is the
    audit signal. Do not invent a fuzzy recovery path here; the
    precise filter needs marker fields.
  - A journal that persists `Phase = MarkerCleared` with on-disk
    sidecars STILL pre-promotion is an unrecoverable corner case
    (marker is gone on disk, helper has nothing to read). This
    requires an external mid-run save that lands `MarkerCleared` —
    not produced by `RunMerge` itself, since durable2 is the next
    save and durable2 advances to `Durable2Done` BEFORE saving. We
    do not handle this case; document it in the orchestrator code
    comment and add a `Warn` log on the rare `MarkerCleared`-with-
    dirty-sidecars combination if it's ever observed. This is
    explicitly out of scope for this plan; see "Out of scope"
    below.

  The implementation contract for the new finisher block layout:
  - **`Durable1Done` branch** (Block 1): KEEPS the existing
    `TagRpsForReap` + `RewindPointReaper.ReapOrphanedRPs` work
    unchanged.
  - **`RpReap` branch** (Block 2): NEW; runs ONLY the promotion
    helper, then advances to `RelativePromotion`. Does NOT touch
    the marker. The helper requires the marker, so it must run
    BEFORE the marker-null work in Block 3.
  - **`RelativePromotion` branch** (Block 3): TAKES OVER the
    marker-null + `BumpSupersedeStateVersion` + `End reason=merged`
    log work that previously lived in the `RpReap` branch. ALSO
    re-invokes the helper defensively when entering at
    `fromPhase == RelativePromotion`.
  - **`MarkerCleared` branch** (Block 4): unchanged from today —
    advances to `Durable2Done` and fires the deferred
    `finisher-durable2` save.

  Existing tests that today crash at `Phase.RpReap` and assert
  marker-null afterward still pass because the same finisher run
  reaches Block 3 via `journal.Phase == RpReap` → Block 2 helper
  invocation → `AdvancePhase(RelativePromotion)` → Block 3
  marker-null. The end state is unchanged; only the log-line
  ordering shifts (helper INFO line precedes marker-null INFO
  line). Add a code comment in `CompleteFromPostDurable` naming
  this contract so a future refactor that pulls Block 2 out
  separately doesn't accidentally re-attach marker-null to the
  RpReap branch.

  Alternative considered: fold the helper into `RpReap`'s body
  (no new phase), reusing the existing block. We rejected this so
  the test seam `Phase.RelativePromotion` is targeted to the new
  promotion work alone and not conflated with the existing RP
  cleanup — clearer log-grep landmark and fault-injection
  semantics.

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

`MergeDialog.CollectActiveReFlyParentChainTerminalTipIds` is useful prior
art but is NOT enough as the implementation template. The helper must use
the #614-safe parent-chain traversal shape from
`GhostMapPresence.IsRecordingInParentChainOfActiveReFly`: walk
single-parent branch points transitively AND bridge optimizer-created
chain segments through same-`ChainId` / `ChainBranch` predecessors. A
terminal-tip-only helper can miss older ancestor recordings after
optimizer splitting.

Extract a new pure helper:

```csharp
internal static HashSet<string>
    EffectiveState.CollectActiveReFlyParentChainRecordingIds(
        RecordingTree tree,
        string activeReFlyTargetId);
```

Implementation shape:

- Resolve the active target inside the explicit `tree`.
- Build an `activeChainIds` exclusion set from the active target's
  same-chain members. These ids are traversal context only; do not emit
  them as parent-chain promotion candidates.
- Seed the walk from every active-chain member's `ParentBranchPointId`
  and chain predecessor.
- When visiting a branch point, proceed only if it has exactly one parent
  recording id; multi-parent branch points remain out of scope.
- When visiting a queued recording id that is in `activeChainIds`, only
  enqueue its `ParentBranchPointId` and chain predecessor; do not emit it.
- When visiting a queued recording id outside `activeChainIds`, emit every
  recording in that ancestor's same chain (`ChainId` + `ChainBranch` +
  `TreeId`), then enqueue each emitted recording's `ParentBranchPointId`
  and chain predecessor so grandparents and optimizer-split ancestors are
  reached.

The existing terminal-tip collector becomes a thin filter over this richer
set. Tests for the existing collector continue to pass with no behavior
change; new tests pin the broader walk and the active-chain exclusion.
The implementation PR must run the existing terminal-tip collector tests
with zero expectation changes so the richer helper does not broaden the
merge-dialog spawn-default behavior by accident.

Current `main` note: runtime bypass policy in
`RelativeAnchorResolution.ShouldBypassLiveAnchorForActiveReFly` is broader
than this helper's parent-chain promotion scope. Keep this plan's
promotion scope conservative for the first implementation. Persisting the
broader "any non-active victim section anchored to the active Re-Fly PID"
runtime policy is plausible follow-up work, but it needs separate product
policy and regression tests for docking / logistics-loop cases where
RELATIVE anchoring is intentionally live.

### Format-version and codec handling

No bump. v7 already supports mixed reference frames in a single recording,
and current `main` is v10. A recording can hold one promoted ABSOLUTE
section (no shadows) next to an unpromoted RELATIVE section (with
shadows).

The relevant serializer code moved during the storage refactor:

- Text codec: `TrajectoryTextSidecarCodec.SerializeTrackSections` gates
  `ABSOLUTE_POINT` text nodes on `referenceFrame == Relative` and
  `recordingFormatVersion >= RecordingStore.RelativeAbsoluteShadowFormatVersion`.
  Promoted ABSOLUTE sections naturally stop emitting text shadows.
- Binary codec: `TrajectorySidecarBinary.WriteTrackSections` still writes
  the `absoluteFrames` point-list slot for v7+ binaries regardless of
  reference frame. The promotion pass should clear / null the promoted
  section's `absoluteFrames` so the binary sidecar emits an empty shadow
  list for that section.
- Section-authoritative write decisions now route through
  `TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory`
  and the binary writer's `RecordingStore.ShouldWriteSectionAuthoritativeTrajectory`
  wrapper. The flat-list splice remains useful because
  `HasTrackSectionPayloadMatchingFlatTrajectory` with
  `allowRelativeSections: true` rebuilds from `TrackSection.frames` and
  compares against `Recording.Points`.

`IsAcceptableSidecarVersionLag` contracts are unchanged.

### CHANGELOG / todo entries

CHANGELOG entry for v0.8.4 (or next version):
> Re-Fly merge now permanently promotes parent-chain upper-stage RELATIVE
> trajectory sections to ABSOLUTE during the merge journal, eliminating
> dead anchor metadata and reducing post-merge sidecar size for those
> sections.

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
    refactor from silently skipping same-pid loops. This is the test
    pin for the open-question #1 resolution: post-merge upper-stage /
    same-active-pid relationships become absolute-only, while
    docking/rendezvous/looped logistics on non-Re-Fly anchors remain
    RELATIVE.

12. **Boundary-seeded section frame does not trigger rollback;
    promoted next section owns the boundary.** Synthesize a parent-chain
    recording with two adjacent RELATIVE sections — section A
    (NOT in scope, anchored to a different non-Re-Fly pid) and
    section B (in scope, anchored to the active Re-Fly target).
    Section B's `frames[0]` is a boundary-seeded duplicate of
    section A's last UT, but flat `Recording.Points` has only one
    entry at that UT (the original sample, written when section A
    was active and now carrying section A's RELATIVE coordinates).
    Assert:
    - Section B is promoted; section A is left RELATIVE.
    - The flat-list entry at the shared boundary UT **IS
      overwritten** by section B's promotion because
      `TrajectoryMath.FindTrackSectionForUT` dispatches an exact
      shared-boundary UT to section B (`[startUT,endUT)` for
      non-final section A).
    - `claimsStartUT == true` for section B; reported
      `pointsRewritten == frames.Count` when section B has no
      shared successor boundary.
    - All other section B frames map cleanly to flat-list entries
      by UT; no rollback.

13. **Two consecutive promoted sections — boundary owned by the
    NEXT section.** Same recording but BOTH sections anchored
    to the active Re-Fly target (both promote). Assert:
    - Both sections promoted to ABSOLUTE.
    - The shared boundary UT's flat-list entry is overwritten
      **exactly once, by section B** (the next section, starting
      at that UT — `claimsEndUT == false` for section A because
      section B starts at A's end, and `claimsStartUT == true` for
      section B).
    - Section A's reported `pointsRewritten == A.frames.Count - 1`;
      section B's reported `pointsRewritten == B.frames.Count`;
      together they cover the union of UT ranges with no double-
      count.
    - The flat-list entry at the boundary UT after promotion
      matches section B's first absolute-shadow value (which by
      `SeedBoundaryPoint` construction equals section B's
      promoted `frames[0]` value — pinning the test to the next-section
      owner is both semantically correct and matches the
      `FindTrackSectionForUT`-aligned ownership rule documented in the splice
      algorithm).

14. **Promoted previous section with RELATIVE next section leaves the
    boundary for the next section.** Synthesize adjacent RELATIVE
    sections where section A is anchored to the active Re-Fly target
    and promotes, while section B is anchored to a station pid and
    remains RELATIVE. Assert:
    - Section A promotes; section B remains RELATIVE.
    - `claimsEndUT == false` for section A because section B starts at
      A's `endUT`.
    - The shared boundary flat-list entry is NOT overwritten by section
      A and still contains section B's relative-coordinate first frame.
    - `TrajectoryMath.FindTrackSectionForUT` at the exact boundary UT
      returns section B, so a compliant reader interprets that retained
      relative flat entry under section B's RELATIVE contract. This pins
      the mirror case so later cleanup does not "fix" the orphan-looking
      boundary into the wrong coordinate frame.

### `EffectiveStateTests` additions

1. **`CollectActiveReFlyParentChainRecordingIds` returns all chain
   members.** Topology: parent chain `parentHead → parentMiddle →
   parentTip` (3 recordings on the parent chain), child chain
   `childHead → childTip` with `childHead` being the active Re-Fly
   target (2 recordings). Expected: all 3 parent-chain members
   (`parentHead`, `parentMiddle`, `parentTip`), no child-chain
   members.

2. **Multi-parent branch points are skipped.** Same predicate gate as
   the existing terminal-tip collector. (This test already exists for
   the tip collector; mirror it for the broader walk.)

3. **Active target with no parent chain returns empty set.**

4. **Transitive grandparent is reached.** Topology:
   `grandparent -> parent -> active`. Expected: both grandparent
   and parent chain members are emitted. This mirrors the #614
   parent-chain predicate and prevents a terminal-tip-only walk from
   missing older ancestor recordings.

5. **Optimizer-split chain predecessor bridges to the root.** Active
   target is an optimizer-created chain tip whose `ParentBranchPointId`
   is on the chain head. Expected: the helper walks the active chain
   predecessor / same-chain context to discover the parent branch point,
   emits the parent-chain recordings, and does NOT emit the active
   target's own chain members as promotion candidates.

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
   `MarkerCleared` → `Durable2Done`.

   **Important: `finisher-durable2` is a DEFERRED save, not a
   synchronous one.** `MergeJournalOrchestrator.cs:386` calls
   `DurableSave("finisher-durable2", persistSynchronously: false)`,
   which short-circuits at `MergeJournalOrchestrator.cs:422-426`
   without invoking `GamePersistence.SaveGame` (the load-time
   finisher avoids re-entering KSP's save path). The
   in-memory promotion is therefore in `RecordingStore` with
   `MarkFilesDirty()` set on the affected recordings, BUT the
   on-disk sidecars and `persistent.sfs` still hold the
   pre-promotion shape until the next real save (a quicksave,
   scene change, or Parsek's recording-side dirty-sidecar
   writer).

   Test assertions therefore split into two phases:
   - **Immediately after `RunFinisher` returns**: assert
     in-memory state — `RecordingStore.CommittedRecordings` shows
     promoted ABSOLUTE sections, `recording.FilesDirty == true`
     for affected recordings, journal is in-memory-only and
     marker is null in memory. The helper's INFO line reports
     `promoted > 0` on the redrive (NOT "nothing to promote")
     because the rebuilt-from-sidecar state still carried the
     unpromoted RELATIVE sections.
   - **After triggering an explicit save** (call
     `GamePersistence.SaveGame` directly via the test's
     `SaveGameForTesting` hook, or simulate a quicksave):
     assert on-disk state — sidecars now ABSOLUTE, journal
     cleared on disk, marker null on disk.

   This mirrors the existing test pattern for the deferred
   `finisher-durable2` checkpoint and is the only correct way to
   verify the redrive for this phase. Adding a real synchronous
   durable save in the finisher path is out of scope for this
   plan (it would change the existing finisher's load-time
   contract for ALL post-Durable1 phases, not just the new one).

3. **Crash AFTER helper completes but BEFORE `durable2` save.**
   This pins the post-promotion / pre-Durable2 window. Two
   distinct injection points exercise different orchestrator
   branches:

   3a. `FaultInjectionPoint = Phase.MarkerCleared`. Fires after
      the helper ran AND after `MarkerCleared` advance + marker
      null in memory, but BEFORE `AdvancePhase(Durable2Done) /
      durable2` save. On disk: phase=`Durable1Done`, marker still
      alive (last save was durable1 before in-memory marker null).
      Reload runs the finisher; helper re-executes on the
      rebuilt-from-sidecar RELATIVE state (idempotent — does real
      work because sidecars are pre-promotion). Test assertions
      follow the same two-phase pattern as test 2:
      in-memory-promoted+dirty after the finisher; on-disk-
      promoted only after an explicit subsequent save (the
      `finisher-durable2` is deferred, see test 2's note).

   3b. `DurableSaveForTesting = label => throw` for `label ==
      "durable2"`. Fires inside the durable2 save call itself.
      In-memory state at moment of throw: phase has been advanced
      to `Durable2Done`, marker is null, sections are promoted.
      But none of that is on disk — the save itself failed. On
      disk: phase=`Durable1Done`, marker alive, sidecars
      pre-promotion. Reload behaves identically to 3a (same
      two-phase assertion pattern; `finisher-durable2` is
      deferred and does not write disk in the finisher tick).

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

5. **Recovery from a persisted `Phase=RelativePromotion`.** The
   no-crash path never persists this (the next durable save
   advances to `Durable2Done` first), but external `OnSave` calls
   during the in-memory window can. Two distinct disk states are
   reachable depending on whether the sidecar rewrite succeeded
   or failed during the original save; both must redrive
   correctly.

   `ParsekScenario.OnSave` writes dirty tree sidecars in
   `SaveTreeRecordings` BEFORE persisting the journal state, so
   the realistic external-save outcome is "promoted sidecars +
   Phase=RelativePromotion journal." Test 5a covers that. Test 5b
   handcrafts the unusual "pre-promotion sidecars +
   Phase=RelativePromotion journal" combo to exercise the
   defensive helper invocation explicitly.

   **5a — natural external-save case (helper is a no-op on
   reload).** Set up: run `RunMerge` to the point where Block 2
   advances to `RelativePromotion` in memory. Trigger
   `ParsekScenario.OnSave` (the test's
   `SaveGameForTesting`-driven path) to persist a real save —
   dirty sidecars are written first (now ABSOLUTE), then the
   journal (`Phase=RelativePromotion`). Crash before `RunMerge`
   advances further. On reload:
   - `journal.Phase == RelativePromotion` (persisted).
   - `scenario.ActiveReFlySessionMarker` is **alive**.
   - Sidecars are **already ABSOLUTE** (the save's
     `SaveTreeRecordings` step landed them).

   `RunFinisher` calls `CompleteFromPostDurable(fromPhase=
   RelativePromotion)`. Block 3 fires; the defensive
   `if (fromPhase == RelativePromotion)` clause re-invokes the
   helper, which finds **no RELATIVE sections matching the
   filter** and reports `nothing to promote` — the audit
   landmark. Marker null + bump + log + advance through
   `MarkerCleared` and `Durable2Done` runs as normal. Final
   state is consistent (sidecars ABSOLUTE, marker null, journal
   cleared after subsequent save). Assert the helper INFO line
   reports `promoted == 0` and finder still drives the journal
   to completion.

   **5b — handcrafted sidecar-write-failure case (helper does
   real work on reload).** This requires a test fixture that
   manufactures the unusual "Phase=RelativePromotion + sidecars
   still RELATIVE" disk state. Use a lower-level sidecar-fail hook;
   do not handcraft a `persistent.sfs` fixture unless the hook proves
   impossible.

   `SaveTreeRecordings` (`ParsekScenario.cs:578-605`) calls
   `RecordingStore.SaveRecordingFiles(rec)` per dirty recording inside
   its loop and treats a `false` return as a soft failure — logs a
   `WARNING: File write failed for tree recording '{name}'` and
   continues; `tree.Save(treeNode)` still serializes the
   `RECORDING_TREE` metadata into the scenario node, and `OnSave`
   keeps going to write `MERGE_JOURNAL`. Add a test seam at the
   `SaveRecordingFiles` level (e.g. an `internal static Func<Recording,
   bool> SaveRecordingFilesForTesting` hook scoped per recording id) so
   the test can return `false` for the parent-chain recording
   specifically. Result on disk: pre-promotion `.prec` (skipped this
   save), correct `RECORDING_TREE` metadata, `MERGE_JOURNAL` with
   `Phase=RelativePromotion`, marker still alive (the marker-null block
   hasn't run in memory yet at this point in `RunMerge`).

   Do NOT throw from a `SaveTreeRecordingsForTesting`-style wrapper
   hook: `SaveTreeRecordings` does not catch, so an exception would
   propagate up and abort `OnSave` BEFORE the `MERGE_JOURNAL` is
   written, defeating the fixture. Similarly, do NOT make a
   `SaveTreeRecordings`-level hook a no-op: that skips
   `tree.Save(treeNode)` and the loaded tree on reload would be missing
   its `RECORDING_TREE` metadata, breaking the test setup unrelatedly.

   On reload:
   - `journal.Phase == RelativePromotion`.
   - `scenario.ActiveReFlySessionMarker` is **alive**.
   - Sidecars are **still RELATIVE** (the manufactured failure).

   Block 3's defensive helper invocation finds RELATIVE sections
   in the freshly-loaded recording and promotes them. Assert
   helper INFO line reports `promoted > 0`. Two-phase assertion:
   in-memory promoted+dirty after the finisher; on-disk promoted
   only after an explicit subsequent save (same pattern as tests
   2-3). **Without the defensive branch**, this test fails — the
   helper would never re-run on the RelativePromotion-tagged
   journal; after the next save the on-disk state would be
   inconsistent (marker null, journal cleared, sidecars
   RELATIVE). Pin this so a future refactor can't silently drop
   the defensive branch. Tests 5a and 5b are both required coverage;
   do not drop either, because Block 3's defensive branch otherwise
   has no no-crash production path that exercises both sidecar states.

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
  pass. That does NOT mean the dead-anchor footgun is eliminated for
  those recordings: a save recorded before v7 can be loaded after the
  upgrade and then Re-Flown, producing a matching parent-chain
  RELATIVE section with no `absoluteFrames` payload to copy. The
  implementation must leave those sections RELATIVE, log the skip with
  enough identity to audit it (`rec`, section index, anchor pid,
  `skippedPartialShadow` / `legacyNoShadow`), and keep the existing
  runtime recorded-anchor / fallback playback path available for them.
  This optimizer pass is a durable cleanup for fully-shadowed v7 data,
  not a migration for legacy RELATIVE payloads.

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

- **Recovery from persisted markerless post-promotion phases with
  pre-promotion sidecars.** The marker is null on disk by definition
  at `MarkerCleared`, and a third-party / mid-frame save could also
  theoretically persist `Phase=RelativePromotion` after the marker was
  cleared by some external path. In either markerless state, the
  finisher cannot run the precise scope-filter check (it has no
  `ActiveReFlyRecordingId` / `activeReFlyTargetVesselPid` to match
  section anchors against — those fields live on the marker, not in
  the journal). The no-crash path can't produce this state (the helper
  runs in Block 2 / `RpReap` → `RelativePromotion` BEFORE
  `MarkerCleared` advance), and there's no in-orchestrator save that
  lands `MarkerCleared` before `Durable2Done`. If a third-party mod's
  mid-frame `SaveGame` lands either combination, the implementation
  cannot recover the helper invocation precisely.

  **Implementable contract.** Because the precise scope-filter
  check is unavailable post-marker-clear, the implementation
  emits a **broader** `Warn` log when, on load, either
  `journal.Phase == MarkerCleared` OR
  (`journal.Phase == RelativePromotion` AND
  `ActiveReFlySessionMarker == null`), AND the tree referenced by
  `journal.TreeId` contains ANY recording with at least one RELATIVE
  TrackSection that has a non-zero `anchorVesselId` AND a populated
  `absoluteFrames` payload. This is a superset of the actually-broken
  case — false positives are possible (a station-anchored RELATIVE
  section in an unrelated recording would also trip the warning) — but
  a `Warn` is just an audit signal, not a hard error, and the
  false-positive case is rare enough to live with. The Warn message
  names the tree, the recording id(s), the section indices, and the
  markerless journal phase so a human reviewer can confirm whether the
  marker-clear was justified.

  Mitigation if the Warn ever fires in practice: a separate
  follow-up plan extends `MergeJournal` with `ActiveReFlyRecordingId`
  + `ActiveReFlyVesselPid` fields (alongside `SessionId` and
  `TreeId`), so the finisher can do the precise scope-filter
  check even after marker clear and re-run the helper as a
  proper recovery path. We defer that to the follow-up because
  the trigger is rare and adding journal fields requires its own
  forward-compat / backward-compat story.

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
