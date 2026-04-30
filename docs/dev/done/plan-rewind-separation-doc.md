# Plan — Post-Implementation Design Doc for Rewind to Separation

Drafted as the spec the Write agent will consume. The existing pre-implementation spec at `docs/parsek-rewind-separation-design.md` (v0.5.2, 1085 lines, written before code existed) is retained as-is for historical reference; the new doc is an independent file that describes what actually shipped.

## Deliverable

**New file:** `docs/parsek-rewind-separation.md` (no `-design` suffix to distinguish from the pre-impl spec).

**Target length:** 1400–1800 lines. Pre-impl spec is 1085; this doc removes the per-phase sequencing and adds scenarios + philosophy, so net similar.

**Ancillary updates in the same commit:**
- `docs/user-guide.md` — add a short (~25-line) "Rewind to Separation" section aimed at players.
- `docs/roadmap.md` — move Phase 12 "Rewind to Separation (v0.9, in design)" from active/planned into Completed under v0.9, with a one-line summary. Cross-reference the new design doc.
- `docs/parsek-architecture.md` — add one-line entries for the new subsystems (`RewindInvoker`, `MergeJournalOrchestrator`, `EffectiveState`, `LoadTimeSweep`) under the subsystem design docs index so new devs can navigate into the new doc.
- `docs/dev/todo-and-known-bugs.md` — strike the "Rewind to Separation (v0.9)" section entirely (all Phase 1-14 entries are shipped). Move any leftover follow-ups (ERS/ELS index-refactor TODOs, wider tombstone scope) into a "v0.9 follow-ups" or "Rewind to Separation follow-ups" block with explicit descriptions, or delete if they duplicate the Known Limitations section of the new design doc.
- `CHANGELOG.md` — no change (v0.9.0 release-note block already written by Phase 14).

Commit on `docs/rewind-final-design` branch in the `Parsek-rewind-doc/` worktree. Not pushed.

## Audience + tone

**Primary:** developers (KSP modders, contributors, security/review readers) who want to understand what the feature is, why it exists, how it behaves at a gameplay level, and how the code is wired.

**Secondary:** public readers (potentially released when Parsek is open-sourced). Wording must not reveal internal orchestration specifics (no agent names, no phase-by-phase orchestration history) but may reference commits / PRs / branches in a normal git-aware way.

**Tone:** matches the peer design docs — specification-grade prose, numbered edge cases, file-level code references, ASCII diagrams where helpful. Formal but not academic. Front-loaded with vision/philosophy/gameplay narrative before diving into data model and behavior.

## Structure

The doc follows the house-style template surfaced by the peer design docs. Peer convention: §1 is "Introduction", Design Philosophy is §2, Terminology precedes Mental Model, Gameplay Scenarios live in an appendix (see `parsek-logistics-routes-design.md:933`).

1. **Introduction** — one page. Problem statement (player perspective: recording ended badly, player couldn't re-fly a crashed sibling without losing the successful half), scope (what v1 covers and doesn't), who benefits, relationship to prior features (Flight Recorder, Game Actions).
2. **Design Philosophy** — 3-7 numbered principles matching the "Design Philosophy" section in `parsek-flight-recorder-design.md` and `parsek-logistics-routes-design.md`. Candidate principles:
   1. Correct visually, minimal, efficient — borrowed from the project-wide principle, applied here.
   2. Append-only history — the recording tree never shrinks; supersede is additive.
   3. Narrow v1 semantics — tombstoning only kerbal deaths (plus bundled rep), sticky career state.
   4. Crash-recoverable — every merge step is journaled; partial states resume cleanly.
   5. Atomic phase 1+2 — provisional-recording creation and session-marker write in one synchronous block.
   6. Player-visible opt-in — re-fly requires explicit click; feature never happens behind the player's back.
3. **Terminology** — concise (20-40 lines). Defines ERS, ELS, Rewind Point (RP), Child Slot, Session-Suppressed Subtree, Unfinished Flight, Supersede, Tombstone, Re-fly Session, Provisional Recording, Merge Journal. Distinguishes Parsek's terms from KSP's.
4. **Mental Model** — ASCII tree diagrams showing:
   - Life of a mission with one staging split, one side crashes, player re-flies the crashed side.
   - Tree evolution across full flight + re-fly + re-rewind cycle.
   - Session-suppressed subtree (forward-only, mixed-parent halt).
   This is the "how does the feature work conceptually" section. Prose heavy, visual.
6. **Data Model** — concrete shipped types with file references and serialization keys. Each type gets a short paragraph + field table. Covers:
   - `MergeState` enum — `Source/Parsek/MergeState.cs`
   - `RewindPoint` — `Source/Parsek/RewindPoint.cs` (fields, ConfigNode keys, PID maps, child slots)
   - `ChildSlot` — `Source/Parsek/ChildSlot.cs`
   - `RecordingSupersedeRelation` — `Source/Parsek/RecordingSupersedeRelation.cs`
   - `LedgerTombstone` — `Source/Parsek/GameActions/LedgerTombstone.cs`
   - `ReFlySessionMarker` — `Source/Parsek/ReFlySessionMarker.cs` (6 durable fields)
   - `MergeJournal` — `Source/Parsek/MergeJournal.cs` (phases enum)
   - New fields added to `Recording.cs` (`MergeState`, `CreatingSessionId`, `SupersedeTargetId`, `ProvisionalForRpId`)
   - New field on `BranchPoint.cs` (`RewindPointId`)
   - New field on `GameAction.cs` (`ActionId` + legacy migration)
   - ConfigNode layout of `ParsekScenario` additions (`REWIND_POINTS`, `RECORDING_SUPERSEDES`, `LEDGER_TOMBSTONES`, `REFLY_SESSION_MARKER`, `MERGE_JOURNAL`)
   - For each type, enumerate the ConfigNode VALUE KEYS (lowercase-first, as emitted by `SaveInto`) alongside the node name — peer doc convention shown in `parsek-game-actions-and-resources-recorder-design.md` Data Model tables.
   - Directory layout: `saves/<save>/Parsek/RewindPoints/<rpId>.sfs` durable store, plus the transient copy `saves/<save>/Parsek_Rewind_<sessionId>.sfs` written to save-root by `RewindInvoker.cs:620` to work around KSP `GamePersistence.LoadGame` not supporting subdirectory paths (deleted after post-load consumption).
   - Disambiguation: `ReFlySessionMarker` has seven fields; six are validated by `MarkerValidator.Validate` (`MarkerValidator.cs:29`) and `InvokedRealTime` is informational-only (not a failure mode). Call this out explicitly in the field table.
7. **Behavior** — numbered sections, code-referenced:
   - 7.1 Multi-controllable split detection (`SegmentBoundaryLogic.IsMultiControllableSplit`, `RewindPointAuthor.Begin`, `BackgroundRecorder` integration)
   - 7.2 Rewind point capture — coroutine, scene guard, warp-to-0, PID maps, root-save-then-move
   - 7.3 Unfinished Flights UI group — ERS-filtered virtual group, non-hideable, non-drop-target
   - 7.4 Invocation — preconditions (5 gates), dialog, reconciliation capture, quicksave copy to save-root, KSP scene reload
   - 7.5 Post-load pipeline — `RewindInvokeContext` consumption, Restore → Strip → Activate → atomic marker/provisional write
   - 7.6 Session suppression — closure walk, ghost filter hook, crew dual-residence carve-out
   - 7.7 Merge — `MergeDialog.MergeCommit` → `MergeJournalOrchestrator.RunMerge` → staged phases
   - 7.8 Terminal kind classifier — `Immutable` vs `CommittedProvisional` per terminal state
   - 7.9 Tombstone emission — v1 narrow scope (KerbalDeath + bundled rep via 1s UT pairing)
   - 7.10 RP reap — eligible criteria, file deletion
   - 7.11 Tree discard purge — invariant 7, the only purge path
   - 7.12 Revert-during-re-fly — Harmony prefix on `FlightDriver.RevertToLaunch` via `RevertInterceptor.cs`, 3-option dialog (`ReFlyRevertDialog.cs`)
   - 7.13 Load-time sweep — journal finisher trigger, marker validation (six of `ReFlySessionMarker`'s seven fields; `InvokedRealTime` is informational), zombie cleanup, orphan warnings
   - 7.14 Crash recovery matrix — `MergeJournal.Phase` enum has nine values (`Source/Parsek/MergeJournal.cs:53-63`); on load, five pre-Durable1 phases trigger rollback, four post-Durable1 phases trigger complete-remaining
8. **Edge Cases** — 30-ish cases pruned from the pre-impl §7's 48, each with status:
   - Entries with dedicated shipped tests → **Shipped (test)**.
   - Entries covered by integration/in-game tests only → **Shipped (integration)**.
   - Entries explicitly deferred to future versions → **Deferred (v1 limitation)**.
   - Entries N/A after design evolution → **N/A** with one-line reason.
   Each entry: scenario + expected behavior + status + test reference (class name + test method).
9. **What Doesn't Change** — explicit list. Career state (contracts, milestones, facilities, strategies, tech, science), vessel-destruction rep penalties, science rewards, legacy saves (ActionId auto-migrated, MergeState auto-migrated from binary), standalone-ghost playback, existing chain semantics outside a re-fly session.
10. **Backward Compatibility** — pre-v0.9 saves: how the one-shot migrations run, what absent ConfigNode sections mean (empty lists / null singletons), whether downgrading is possible (answer: no, post-v0.9 saves with supersede relations won't load cleanly on v0.8.x — call this out). Include the specific migrations:
    - `GameAction.ActionId` legacy-id generation (deterministic hash) at load, one-shot Info log (`Source/Parsek/GameActions/Ledger.cs`)
    - `Recording.MergeState` tri-state mapping from legacy binary, one-shot Info log (`Source/Parsek/RecordingTree.cs` loader)
    - Stray `SupersedeTargetId` on `Immutable` recordings — logs Warn and clears (design §5.5)
11. **Known Limitations / Future Work** —
    - The 13 ERS-exempt files + their inline TODO markers; the index-to-recording-id refactor deferred beyond v0.9.
    - `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` — pre-existing Unity-gated skip.
    - Wider tombstone scope (v2): contract + milestone tombstoning when safe.
    - Cross-tree supersedes — explicit halt in `EffectiveRecordingId` walk; v1 doesn't produce them.
    - End-to-end automated in-game RunInvoke test — stubbed due to scene-reload not being drivable under xUnit.
    - Background split RP capture relies on cached PID payload; a joint-break during high warp on a truly unloaded vessel still may not trigger (brief inline note).
12. **Diagnostic Logging** — full log-tag catalog. **The Write agent must derive the catalog via `grep -r "ParsekLog\.\(Info\|Warn\|Verbose\)" Source/Parsek/ --include="*.cs"` filtered to Rewind-to-Separation source files (RewindInvoker, RewindPointAuthor, RewindInvokeContext, ReconciliationBundle, PostLoadStripper, SupersedeCommit, TombstoneEligibility, TombstoneAttributionHelper, TerminalKindClassifier, MergeJournalOrchestrator, RewindPointReaper, TreeDiscardPurge, ReFlyRevertDialog, RevertInterceptor, LoadTimeSweep, MarkerValidator, SessionSuppressionState, UnfinishedFlightsGroup, EffectiveState, RewindPointDiskUsage, modified parts of MergeDialog.cs, ParsekScenario.cs, RecordingStore.cs, CrewReservationManager.cs).** The candidate list of tags expected (verified from shipped code): `Rewind`, `RewindUI`, `ReFlySession`, `Supersede`, `LedgerSwap`, `MergeJournal`, `LoadSweep`, `UnfinishedFlights`, `CrewReservations`, `Merger`, `RevertInterceptor`. Do NOT invent tags that don't appear in the grep output (the pre-impl spec's candidate tags like `RewindSave`, `Reap`, `Strip`, `Tombstone`, `ERS`, `ELS` did not ship as separate tag names — they're sub-categories under the bracketed tags above). For each surveyed tag: example line + which decision points fire it. Helps debuggers reconstruct a session from `KSP.log`.
Followed by:

- **Appendix A — Gameplay Scenarios** — 4-6 concrete play sessions walking through Happy path (staging → crash one side → re-fly → merge landed), EVA re-fly, Docking merge, Revert-during-re-fly (3-option dialog), Crash-quit-resume mid-re-fly (session marker survives, post-load pipeline resumes). Each scenario: 5-10 bullet steps describing what the player sees. Matches the scenario-simulation style from `development-workflow.md` Step 2 and the appendix in `parsek-logistics-routes-design.md:933`.

---

13. **Testing** — summary of the testing surface:
    - Unit test classes (list, ~30 rewind-scoped classes + peer classes they touch). Write agent must verify exact class names by globbing `Source/Parsek.Tests/*Rewind*.cs`, `*Supersede*.cs`, `*Tombstone*.cs`, `*MergeJournal*.cs`, `*EffectiveState*.cs`, `*SessionSuppression*.cs`, `*PostLoadStripper*.cs`, `*RewindInvoker*.cs`, `*RewindPointAuthor*.cs`, `*ReconciliationBundle*.cs`, `*TreeDiscardPurge*.cs`, `*RewindPointReaper*.cs`, `*LoadTimeSweep*.cs`, `*MarkerValidator*.cs`, `*UnfinishedFlights*.cs`, `*ReFlyRevertDialog*.cs`, `*RecordingSupersedeRelation*.cs`, `*LedgerTombstone*.cs`, `*CrewReservationRecompute*.cs`, `*DiskUsageDiagnostics*.cs`, `*RenameOnUnfinishedFlight*.cs`, `*RewindPointRoundTrip*.cs`, `*BranchPointRewindPointId*.cs`, `*ReFlySessionMarker*.cs`, `*MergeJournalRoundTrip*.cs`, `*LegacyMigration*.cs`, `*ActionIdMigration*.cs`, `*ChildSlotEffectiveRecordingId*.cs`, `*MergeCrashRecoveryMatrix*.cs`, `*BackgroundSplitRp*.cs`, `*MultiControllableClassifier*.cs`, `*AtomicMarkerWrite*.cs`, `*GrepAudit*.cs`.
    - In-game test classes (list ~17, briefly describe each — `PartPersistentIdStabilityTest` called out as the critical precondition probe).
    - Grep-audit CI gate: `scripts/grep-audit-ers-els.ps1` + `scripts/ers-els-audit-allowlist.txt` + `GrepAuditTests.GrepAudit_AllRawAccessIsAllowlisted`
    - Reviewer sign-off condition from the pre-impl spec v0.5 (crash recovery matrix) — how `MergeCrashRecoveryMatrixTests` + `MergeJournalOrchestratorTests` satisfy it.

## User-guide section (separate file: `docs/user-guide.md`)

New subsection under the appropriate parent (Flight Recording or its own top-level). Length: 20-30 lines. Content:

- What it is (1 sentence): "Parsek now captures rewind points at multi-controllable splits so you can re-fly a crashed sibling without losing the successful one."
- When it triggers (1 paragraph): at staging, undocking, or EVA when 2+ resulting vessels are controllable. An RP + quicksave is saved automatically.
- How to use it (bullets):
  - Open the Recordings window. Look for the "Unfinished Flights" group (appears automatically when you have a crashed sibling).
  - Click Rewind on the row you want to re-fly. Confirm in the dialog.
  - The game reloads you into the moment of the split; fly the crashed sibling.
  - When your re-fly ends (landing, orbit, or crash), click Merge in the merge dialog that appears.
  - Career state (contracts, milestones, tech, facilities) is unchanged by supersede. Only kerbal deaths in the retired sibling are reversed.
  - Revert-during-re-fly: hitting the stock Revert-to-Launch button shows a dialog with Retry / Full Revert / Continue Flying.
- Where to watch: Settings → Diagnostics → "Rewind point disk usage" shows size + count.

Do NOT include design rationale, code references, or implementation details in the user-guide section — that's the design doc's job.

## Roadmap update (`docs/roadmap.md`)

Move the existing "Phase 12: Rewind to Separation (v0.9, in design)" entry into Completed under v0.9 with a one-line summary: "Rewind to Separation — re-fly unfinished missions after multi-controllable splits (design doc: `docs/parsek-rewind-separation.md`)." Keep the pre-existing structure (whatever Completed/In-Progress columns already exist).

## Architecture index update (`docs/parsek-architecture.md`)

Add entries under the subsystem design docs index:
- "Rewind to Separation — `docs/parsek-rewind-separation.md`" (primary doc)
- List the subsystems introduced: `RewindInvoker`, `MergeJournalOrchestrator`, `EffectiveState`, `LoadTimeSweep`, `TreeDiscardPurge`, `RewindPointReaper`, `RevertInterceptor`, `SessionSuppressionState`, `UnfinishedFlightsGroup`

## What NOT to put in the doc

- Per-phase agent orchestration history (Phase 1-14 implementation workflow)
- References to `Parsek-rewind-pN/` worktrees or branches
- Review-cycle counts ("v0.5.2 after 4 review passes")
- Model/agent references

Those belong in PR description or git history, not in the published design doc. The pre-impl doc can keep its review history since it's archived.

## Source material the Write agent must consult

- The pre-impl doc `docs/parsek-rewind-separation-design.md` (as a structural reference, NOT a source of truth for what shipped)
- Peer design docs (`parsek-flight-recorder-design.md`, `parsek-game-actions-and-resources-recorder-design.md`, `parsek-logistics-routes-design.md`) for tone/structure
- The shipped code in `Source/Parsek/` — every data-model and behavior claim must be verifiable by reading a named file (Write agent is expected to read/grep them directly rather than rely on this plan's summaries)
- `CHANGELOG.md` v0.9.0 block for the user-facing one-liner
- `scripts/ers-els-audit-allowlist.txt` — count the actual exemption entries to cite the correct number (plan previously said 13; verify)

The Write agent should NOT rely on a summary embedded in this plan; instead, it must read the canonical sources directly so every claim is grounded in live code.

## Workflow for writing this

1. Explore/Audit: done.
2. **Plan review (opus)** — an opus agent reviews THIS plan against the peer docs and audit, flags gaps / bad structure / missing sections.
3. Fix plan based on review (orchestrator).
4. **Write (opus)** — an opus agent drafts `docs/parsek-rewind-separation.md`, `docs/user-guide.md` section, `docs/roadmap.md` + `docs/parsek-architecture.md` updates in the `Parsek-rewind-doc/` worktree on branch `docs/rewind-final-design`.
5. **Final review (opus)** — another opus agent reads the finished doc cold + the shipped code, verifies every data-model and behavior claim, reports issues.
6. Fix (orchestrator or fix agent) if the review found issues.
7. Commit in logical units on `docs/rewind-final-design`. Merge to `feat/rewind-staging` after the merge-from-main agent completes.

## Hard constraints

- No emoji anywhere.
- No Co-Authored-By lines in commits.
- Plain ASCII; UTF-8 with §/—/— as established in peer docs but no emoji or special-pictograph Unicode.
- Every data-model or behavior claim must cite a file path at least.
- Public-safe wording: no agent references, no orchestration insiders, no "Phase N of M" language in the doc body (the Internals section in CHANGELOG captures that already).
