# PR review: bug/extrapolator-destroyed-on-subsurface

Reviewer: independent agent. Date: 2026-04-25. Scope: 41 commits ahead of `origin/main`, ~82 files / +7157 / -2707. Build green, 8605/8605 xUnit pass.

## TL;DR

Ship pending one Important fix (in-place continuation merge has no journal-based crash recovery). The fix series is internally consistent, well-tested at the xUnit layer, and documentation matches the shipped code. No critical issues blocking ship; no AI-attribution / no-verify violations in the 41 commit messages.

## Critical

None.

## Important

- **`Source/Parsek/MergeDialog.cs:354-464` — in-place continuation merge bypasses `MergeJournalOrchestrator` and writes one un-journaled `GamePersistence.SaveGame` at the end.** If the process dies between `provisional.MergeState = Immutable` (line 399) and the `SaveGame` at line 444 — power loss, KSP crash on reap, hard quit — the persisted save still has the marker present, the recording's `MergeState == CommittedProvisional`, and no journal phase to drive recovery. Reload state is consistent (the new `MarkerValidator` carve-out at `MarkerValidator.cs:108-130` accepts in-place + `CommittedProvisional`), but the player must re-confirm the merge dialog. Not strictly correctness-broken, but the rest of the re-fly merge path is journaled for exactly this reason (see design §6.6 / `MergeJournalOrchestrator.RunFinisher`). Either (a) write a synthetic single-phase journal entry around the in-place flip + reap, or (b) document this as an accepted v1 simplification in `docs/parsek-rewind-to-separation-design.md` §6.6 (where the journal contract lives) — currently the design doc doesn't mention the carve-out at all.

## Nits / follow-ups

- **`Source/Parsek/EffectiveState.cs:689-716` `EnqueueChainSiblings`** — chain siblings are filtered by `ChainId` + `ChainBranch` but NOT `TreeId`. Chain segments from `RecordingOptimizer.SplitAtSection` always live in the same tree (verified — splits operate on a single live recording), so cross-tree leakage cannot happen today. Add a `string.Equals(cand.TreeId, rec.TreeId, StringComparison.Ordinal)` clause as defense-in-depth in case a future feature ever moves chain segments across trees.

- **`Source/Parsek/ParsekFlight.cs:2285` `IsTrackableVesselType`** — function has zero production callers (only the unit tests reference it). The EVA promotion is correct in `IsTrackableVessel` (the live-vessel path); the type-only helper exists for future unit-test convenience but is dead in production. Either delete it or wire it into the live-vessel function head as a fast-path. Low priority.

- **`Source/Parsek.Tests/SplitEventDetectionTests.cs:35-50` (renamed `IsTrackableVesselType_EVA_ReturnsTrue`)** — pins the type-only branch, but the live-vessel `v.parts` loop branch in `IsTrackableVessel` (the actually-called path) cannot be exercised from xUnit. Add an in-game test in `Source/Parsek/InGameTests/RuntimeTests.cs` that spawns or finds an EVA kerbal and asserts `IsTrackableVessel(eva) == true`. Without this, a future regression that gates the EVA branch behind a stricter check (e.g. requiring `KerbalEVA` module presence) would silently break the destroyed-EVA → Unfinished Flight pipeline that motivated commit `c03c0b82`.

- **`Source/Parsek/EffectiveState.cs:174` log line** — `terminalRec.TerminalStateValue` is dereferenced after `IsTerminalCrashed(terminalRec)` returned false. `terminalRec` is non-null in this codepath (origin `rec` is gated non-null upstream and `ResolveChainTerminalRecording` always returns non-null when given non-null input), so this is safe today, but the log message would NPE if either invariant ever changed. Replace with `terminalRec?.TerminalStateValue` for cheap insurance.

- **`Source/Parsek/RewindInvoker.cs:586-610` `WaitForFlightReadyAndInvoke`** — the 300-frame timeout (~5 s at 60 fps) is hard-coded; observed worst-case async load is ~1.4 s per the comment. For modded saves with hundreds of vessels the load time can exceed 5 s and the deferred path will time out, deleting the temp quicksave and aborting Rewind. Consider raising to 1800 frames (30 s) or making it configurable in `ParsekConfig`.

- **PR description says "RecordingFinalizationCacheProducer was poisoning live recordings (gated to NullSolver only)"** — actually the gate lives in `IncompleteBallisticSceneExitFinalizer.cs:267-296` (the `snapshot.FailureReason != None && != NullSolver` early-return). `RecordingFinalizationCacheProducer.cs` was not modified in this branch. Doc claim is slightly misleading; the fix is real.

- **`docs/parsek-rewind-to-separation-design.md` §1 / §1.1** — the doc was tightened to say "destruction or loss" only, and §7.31 now spells out that stable-end siblings are explicitly out of scope. Good. But §6.6 (the merge journal contract) was not updated to mention the in-place continuation carve-out introduced in `MergeDialog.cs:354-464`. Add a §6.6.x sub-section so a future reader of the design doc isn't surprised that some merges skip the journal entirely.

## What I verified

- `dotnet test` on `Source/Parsek.Tests`: 8605/8605 pass, 0 skipped, 9 s. (`Failed: 0, Passed: 8605, Skipped: 0`).
- 41 commit messages scanned for `co-authored|claude|generated with|--no-verify` — zero hits. AI-attribution hard rule respected.
- `IsTrackableVessel` callers traced: `BackgroundRecorder.cs:485, 665`; `ParsekFlight.cs:3254, 3444, 7116`; `SegmentBoundaryLogic.cs:276`. EVA-promotion is upgrade-safe at every call site (an EVA being trackable is the correct answer for split classification, RP-slot eligibility, debris filtering, and joint-break controller capture).
- `EnqueueChainSiblings` revisitation guard: `result.Contains(cand.RecordingId)` early-returns before re-adding/enqueueing, so the BP walk runs at most once per sibling.
- Chain-sibling expansion does NOT widen tombstone scope improperly: `CommitTombstones` consumes the same closure but is type-narrow via `TombstoneEligibility.IsEligible` (KerbalDeath + bundled rep penalty only). A chain-sibling segment is by construction the same vessel, so deaths credited to its TIP are correctly retired with the HEAD's supersede — this is the intended widening.
- `ValidateSupersedeTarget` reasoning chain has unit coverage in `SupersedeCommitTests.cs:835` (all four reason strings exercised).
- In-place continuation atomic write covered by `AtomicMarkerWriteTests.cs:523, 671`. MarkerValidator carve-out covered by `LoadTimeSweepTests.cs` (three tests: in-place CommittedProvisional preserved, placeholder + CommittedProvisional cleared, in-place + Immutable still cleared).
- Self-supersede cleanup covered by `LoadTimeSweep.RemoveSelfSupersedes` at `LoadTimeSweep.cs:281-313`; defense-in-depth at `SupersedeCommit.AppendRelations:130-149`; caller-side guard at `MergeDialog.cs:354-464`. Three layers; correct.
- `RecalculateAndPatch(double.MaxValue)` does set `bypassPatchDeferral=true` per `LedgerOrchestrator.cs:1164-1170`. The PR comment is accurate.
- `MergeJournalOrchestrator.cs` not modified — `RunFinisher` recovery contract for journaled paths is intact.
- Five chain-expansion tests (`SessionSuppressedSubtreeTests.cs:267,304,336,373,410`) cover head/tip/three-segment/different-branch/BP-descendant variants.
- `RecordingFinalizationCacheProducer.cs`, `RecordingFinalizationCache.cs`, `RecordingFinalizationCacheApplier.cs` not modified — the live-recording-poisoning fix lives in the finalizer's transient-failure gate, not the cache files.
- Deleted placeholder-redirect block in `MergeDialog.TryCommitReFlySupersede` confirmed gone (only docstring references remain at `RewindInvoker.cs:695`, `SupersedeCommit.cs:133`).
- CHANGELOG entries are 1-line per item per the hard rule (verified by inspecting the diff).
- `docs/dev/todo-and-known-bugs.md` items 19-23 match the shipped code.
