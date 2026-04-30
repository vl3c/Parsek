# Rewind to Separation - Resume Plan

**One-page handoff document for picking this feature back up after the v0.8.2 release.** Tells you exactly where everything is and what happens next.

**Target release:** Parsek v0.9 (Phase 12 in the roadmap).

---

## TL;DR

- **Design doc done**, reviewed 4 times, no blocking issues. Lives at `docs/parsek-rewind-separation-design.md` on branch `design/rewind-staging` (7 commits, v0.1 -> v0.5.2). Pushed to GitHub; no PR yet.
- **Two open docs PRs** (#341, #342) — small README/roadmap updates that advertise the feature. Mergeable any time.
- **No code yet.** Step 4 of the workflow (Explore + Plan + Implement + Review) hasn't started.
- **Resume point:** dispatch Explore agents against `Source/Parsek/` against the design doc, then a Plan agent, then 13 sequenced implementation phases.

---

## Current state on GitHub

| Branch | Commits | PR | Status |
|---|---|---|---|
| `design/rewind-staging` | 7 | none yet | Pushed, unmerged. Contains only the design doc. |
| `docs/announce-rewind-staging` | 4 | [#341](https://github.com/vl3c/Parsek/pull/341) | Open. README + roadmap announcement. |
| `docs/rewind-naming` | 1 (stacked on #341) | [#342](https://github.com/vl3c/Parsek/pull/342) | Open. `Rewind` -> `Rewind to launch` rename + EVA copy tweak. |

Local worktrees (optional cleanup later):
- `Parsek-rewind-staging-design/` -> `design/rewind-staging`
- `Parsek-announce-rewind-staging/` -> `docs/announce-rewind-staging`
- `Parsek-rewind-naming/` -> `docs/rewind-naming`

---

## What the design delivers (briefest possible)

A "Rewind Point" written at every split that produces 2+ controllable vessels (staging, undock, EVA). Unmerged siblings (BG-crashed while the player flew the other half) appear in a new "Unfinished Flights" group. Invoking one loads the quicksave, strips sibling vessels to ghosts, and lets the player fly the sibling that was abandoned. On merge, the new recording commits additively with a `RecordingSupersede(old, new)` relation record; the old BG-crash stays on disk, filtered out of user-facing lookups. v1 tombstones only `KerbalDeath` ledger events; contract/milestone/facility/strategy/tech state stays sticky (KSP won't re-emit retired events).

Full details in `docs/parsek-rewind-separation-design.md` §1.1 (scope), §3 (ERS/ELS model), §6.6 (merge semantics), §7 (48 edge cases).

---

## Review history

| Version | Driver | Outcome |
|---|---|---|
| v0.1 | Initial draft | Internal review found 8 critical issues (MergeState contradiction, replace-in-place violates principle 10, etc.) |
| v0.2 | Internal Opus review fixes | External review #1 found more architectural issues (duplicated split state, contract re-emission impossibility, etc.) |
| v0.3 | External review #1 fixes + ERS/ELS model | Internal Opus review #2 surfaced semantic-layer issues |
| v0.4 | Internal Opus review #2 + new architectural cuts | External review #2 still found P1 blockers (crashed-re-fly re-rewindability, marker rescue shape mismatch, journal recovery boundaries) |
| v0.5 | External review #2 full absorption (narrow-scope reframe) | External review #3: **no blocking findings**. Three P2/P3 cleanup issues only. |
| v0.5.1 | P2/P3 cleanups (contract subsystem naming, rootPart persistentId, ERS example) | - |
| v0.5.2 | User feedback: make speculative-RP lifecycle explicit in §6.1 | - |

Reviewer sign-off on v0.5: *"If the runtime tests around atomic phase-1/phase-2 invocation and the merge crash-recovery matrix are added and pass, this is good to implement."*

---

## Next steps (Step 4 of `development-workflow.md`)

### Step 4a: Explore (dispatch multiple agents in parallel)

Explore agents with `subagent_type=Explore`. Each agent reads the design doc and a targeted slice of the codebase. One message with multiple tool calls = parallel execution.

Subsystems to map:
1. **Recording/Tree model** — `FlightRecorder.cs`, `Recording.cs`, `RecordingStore.cs`, `RecordingTree.cs`, `BranchPoint.cs`, `SegmentBoundaryLogic.cs`.
2. **Scenario + save/load** — `ParsekScenario.cs` (additions: `RewindPoints`, `RecordingSupersedes`, `LedgerTombstones`, `ReFlySessionMarker`, `MergeJournal`), `RewindContext.cs`, save-dir structure under `saves/<save>/Parsek/`.
3. **Ledger** — `GameActions/` folder: `GameAction`, `Ledger`, `RecalculationEngine`, 8 resource modules, `LedgerOrchestrator`, `GameStateEventConverter`. Check if `ActionId` already exists as stable/immutable or needs addition.
4. **Ghost walker + physical-visibility subsystems** — `GhostPlaybackEngine.cs`, `GhostMapPresence.cs`, `WatchModeController.cs`, chain-tip and claim logic. These need `SessionSuppressedSubtree` filtering.
5. **Crew reservations** — `CrewReservationManager.cs`, kerbal walk logic for ERS integration + the `§3.3.1` live-re-fly carve-out.
6. **Split detection + classifier** — `ParsekFlight.DeferredJointBreakCheck`, `DeferredUndockBranch`, EVA handler, `SegmentBoundaryLogic.ClassifyJointBreakResult`.
7. **Vessel spawn + strip** — `VesselSpawner.cs`, `FlightGlobals.Vessels` enumeration, `GhostMapPresence.ghostMapVesselPids` guard.
8. **Merge dialog + UI** — `MergeDialog.cs`, `UI/RecordingsTableUI.cs`, `GroupHierarchyStore.cs`, group-hierarchy store conventions.
9. **Raw-consumer grep-audit surface** — list every file that currently does `.CommittedRecordings` or `.Actions` direct access (for the prerequisite ERS/ELS conversion phase).

### Step 4b: Plan

After Explore returns, dispatch a Plan agent with `subagent_type=Plan`. Input: design doc `§12` (13 phases) + explore findings. Output: a phased task list for TaskCreate.

Current §12 sequencing (verbatim from design doc):

1. Data model + legacy migration (ActionId, tri-state MergeState, both PID maps)
2. ERS/ELS shared utility (tombstone-only ELS filter)
3. Grep-audit conversion phase (convert raw consumers)
4. RP creation + deferred quicksave + scene guard + warp-to-0 + root-save-then-move
5. Unfinished Flights UI (read-only; cannot hide)
6. Rewind invocation: reconciliation + strip + activate + atomic phase 1+2 marker/provisional
7. SessionSuppressedSubtree wiring
8. Merge: supersede relations + subtree closure
9. Merge: v1 tombstone-eligible scope + LedgerTombstones
10. Merge: journaled staged commit
11. RP reap + tree discard purge
12. Revert-during-re-fly dialog
13. Load-time sweep: journal finisher + marker validation + spare set + zombie cleanup + orphan log + stray-field log
14. Polish

Phases 1-5 ship as "feature preview" (no gameplay change; RPs captured, group visible, rewind button disabled). Phase 6+ unlocks the feature progressively.

### Step 4c-f: Implement + Review cycles

For each phase the Plan produces:
- Dispatch an implementation agent (`subagent_type=general-purpose`, isolation=worktree for independent phases)
- Dispatch a review agent after each phase
- Fix in a separate agent if the review found issues
- Repeat per phase

Reviewer conditional (from v0.5 sign-off): the implementation MUST include runtime tests for:
- **Atomic phase-1/phase-2 invocation** — verify no save can capture intermediate state between provisional-recording creation and marker write
- **Merge crash-recovery matrix** — inject exceptions at the 5 points in §6.6 and verify the finisher in §6.9 step 2 completes correctly for each

See design doc §11.5 for the full in-game test list and §11.7 for the grep-audit CI gate.

---

## Things I'd flag as highest-risk for implementation

- **Atomic phase 1+2 marker write** — §5.6 demands one-frame atomicity. Any code path that yields, awaits, or defers between provisional-recording creation and marker write breaks the invariant silently.
- **Post-load strip identification** — §6.4 step 2-4. PidSlotMap primary + RootPartPidMap fallback + ghost-ProtoVessel guard + leave-unrelated-vessels-alone. Easy to mis-identify and over-strip.
- **Journal finisher idempotency** — §6.6 + §6.9 step 2. The recovery matrix must produce the same result no matter where the previous crash happened. Test with deliberate exception injection.
- **Grep-audit prerequisite** — Step 3 of the sequence. Every existing file that walks `CommittedRecordings` or `Ledger.Actions` directly needs conversion to ERS/ELS before the main feature lights up. Miss one and you'll get silent state drift the main feature can't repair.
- **Subtree supersede closure** — §3.3 forward-only + mixed-parent halt. Dock/board merges in the tree are DAG joins; the closure must halt at any node with a parent outside the subtree.

---

## If you want a fresh second opinion before starting implementation

The v0.5.2 tip has had 4 review rounds but no review against the v0.5.1/v0.5.2 cleanup commits specifically. Low-value for architecture, non-zero value for "did anything slip between versions." Optional.

Prompt template for that is in the git history of this worktree (search commit messages for "third-pass" / "fourth-pass") or reconstruct from the review-prompt pattern: `§12` says what was added, §6.6 has the journal matrix, §6.1 now has the speculative-RP block (v0.5.2 change worth spot-checking).

---

## Quick resume commands

```bash
# Get latest of everything
cd Parsek && git fetch --all

# Continue work in the design worktree (adjust design doc if needed)
cd Parsek-rewind-staging-design
# -- branch: design/rewind-staging, tip: latest on origin

# Or start implementation on a fresh feature branch off main
cd Parsek
git worktree add ../Parsek-feat-rewind-staging -b feat/rewind-staging origin/main
cd ../Parsek-feat-rewind-staging
# read the design doc, dispatch Explore agents, then Plan
```
