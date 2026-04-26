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
- A RELATIVE section on a recording that is itself the looped section
  for the recording's `LoopAnchorVesselId`, even if pid matches —
  edge-case sanity check, because looped sections by design depend on
  the live anchor pose and we don't want to silently break a loop.
  (In practice the parent-chain ancestry contains the supersedeable
  ancestor recordings, not the active Re-Fly continuation itself, so
  this check is defense-in-depth.)
- Any RELATIVE section in a recording NOT in the parent-chain
  ancestry.
- Any RELATIVE section in a v6 recording (or v7 recording with
  partial-shadow coverage) — no shadow data to promote against.

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
    List<Recording> recordings,
    string activeReFlyTargetRecordingId,
    uint activeReFlyTargetVesselPid)
```

The pass returns the number of recordings it promoted (for logging) and:

1. Resolves the parent-chain ancestry from `activeReFlyTargetRecordingId`
   using the same predicate as
   `MergeDialog.CollectActiveReFlyParentChainTerminalTipIds`
   (`docs/dev/plans/refly-postmerge-relative-to-absolute.md:1`-style walk:
   collect chain-mates of the active target via `ChainId`, then walk
   `ParentBranchPointId` to single-parent ancestor branch points, then
   resolve each parent's chain terminal). The function lives in
   `MergeDialog.cs` today; we extract a pure helper into
   `EffectiveState.cs` (so optimizer code can call it without taking a
   `MergeDialog` dependency) and call from both sites.

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

   - For every section it promoted, finds the corresponding UT range in
     `Recording.Points` (binary search by `ut`) and overwrites those entries
     with the absolute-shadow values (which were captured from the original
     `BuildTrajectoryPoint` call before `ApplyRelativeOffset` mutated the
     copy).
   - Asserts that the index ranges align (matching counts, monotonic UTs,
     no gaps). On mismatch, rolls back the section flip for that section
     and emits a `Warn` line — partial state is not allowed to ship.

4. Calls `recording.MarkFilesDirty()` so the sidecar gets rewritten with
   the new shape.

5. Emits one structured `Info` summary per recording: `parent-chain absolute
   promotion: rec={id} promoted={n} skippedPartialShadow={k}
   skippedNonMatchingAnchor={m} pointsRewritten={p} sessionId={…}`. A
   pass-level `Info` line aggregates totals.

### Wiring into the Re-Fly merge

`MergeDialog.MergeCommit` (`Source/Parsek/MergeDialog.cs:238`) currently
calls `RecordingStore.RunOptimizationPass()` at line 254, then
`TryCommitReFlySupersede()` at line 265. Two questions to settle:

- **Where in the journal to run the new pass?** The exploration suggested
  post-`RpReap` so a half-completed merge rollback can never observe the
  promoted-but-not-committed state. That is the right call. The new pass
  runs as a **new step in `MergeJournalOrchestrator`** after `RpReap`
  (`Source/Parsek/MergeJournalOrchestrator.cs:235`), as part of the same
  durable save that normally writes the post-merge `persistent.sfs`.
  Crash recovery: if we crash after the promotion runs but before the
  durable save lands, the in-memory state has the promoted sections but
  the on-disk `.prec` files still have the pre-promotion shape; on next
  load we redrive `OnLoad` → re-populate `RecordingStore` from sidecars →
  the in-memory state matches the on-disk state again. The promotion is
  idempotent (running it twice on an already-promoted section is a no-op
  because the relative section is gone), so re-running it in the journal
  finisher's redrive path is safe.

- **Does this need a new `MergeJournal.Phases` enum value?** Yes — add
  `RelativePromotion` between `RpReap` and the implicit final cleared
  state. `RunFinisher` learns to redrive from `RelativePromotion` (re-run
  the pass — idempotent — then clear). Alternative: fold it into
  `RpReap`'s body and don't journal it as a separate phase. We pick the
  explicit phase because (a) journal phases are cheap, (b) it makes the
  redrive contract obvious from the orchestrator code, (c) it gives us a
  log-grep landmark for the operation.

The optimizer pass is invoked from the new orchestrator step rather than
from `RecordingStore.RunOptimizationPass` because it's Re-Fly-specific
state (active target id + pid) and shouldn't run on plain merges or
periodic optimizer ticks.

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

1. **`RelativePromotion` phase is reached after `RpReap`.** In-place
   continuation merge with parent-chain recordings to promote. Assert
   journal advances through `RelativePromotion` and clears.

2. **Redrive from `RelativePromotion` re-runs the pass and is
   idempotent.** Inject a crash hook before the phase clears; reload;
   `RunFinisher` should re-execute the pass (which finds nothing to
   promote because the previous run already landed in memory) and
   advance to cleared.

3. **Crash before `RelativePromotion` does NOT promote.** Inject hook at
   `RpReap` clear; assert the parent-chain recordings are still
   RELATIVE on disk, and reload's redrive completes the promotion.

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

- **Looped sections in general.** Looped segments are intentionally
  preserved by the scope filter (see scope item 2 — "looped section
  anchored to non-Re-Fly vessel" stays RELATIVE). The narrow case
  where a parent-chain recording's looped section happens to anchor
  to the active Re-Fly target's pid is the only situation where a
  loop's anchor frame would change post-pass; that case is covered
  by the same alignment as the rest of the promotion (the live
  anchor pose is dead anyway because the booster's recording is
  superseded). Practically this case is unlikely — looped logistics
  routes loop on stations/bases, not on a soon-to-be-re-flown
  ascent stage — but the scope item-2 test pins it explicitly.

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
