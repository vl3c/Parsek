# Parsek against replay theory: traps catalogue and design audit

Date: 2026-11-04. Code base: main at b2182df. Research only: nothing was built, flown or changed.
Living copy: https://claude.ai/artifact/Ku6mxeWM3EMLmTtx9vk3p5

## Summary

The big architectural decisions are the ones the literature recommends, and 49 of the 53 catalogue items come out sound or sound with a caveat. Parsek records outputs rather than inputs, keeps the ledger as a replayed log, stores sidecars content-addressed with a mark-and-sweep collector, uses a redo journal for merges, and keys identity on the launch guid. The risk sits at the seams theory warns about: the moment a multi-step commit is interrupted, the moment a save runs mid-operation, and the moment a recording crosses a frame or body boundary.

The audit found 13 new defects that are not in the todo file. One is confirmed and paradox-class, two are high-impact but rare, and the rest are medium or low:

1. **A merge interrupted at Begin leaves part of the attempt committed (confirmed).** The rollback removes only the re-flown recording, but the Begin save already holds the committed attempt tree, its ledger rows and the crew swap. Payloads and EVA crew from the attempt stay committed with their credit. No crash is needed: an exception in the Begin save or the fork migration, then any scene change, reaches it.
2. **The save has no fail-closed path (plausible).** If a save step throws, the rest of Parsek's save is skipped, including the ghost strip. Whether stock keeps writing the file after that is unverified. If it does, the Parsek node is partial and ghost map vessels can load as real ones.
3. **A physics-speed SOI crossing makes no section boundary (plausible).** One section spans Kerbin and the Mun, so the optimizer's split-on-burn rule cannot fire and the section is labelled with the first body. Each sample still resolves on its own body, so the ghost does not visibly jump.
4. **Loop replay re-plants a real flag (confirmed).** This happens every cycle after the player removes the flag.

Three structural risks have no bug behind them yet. Third-party mods' scenario state comes back at the rewind point and is never repaired. Nothing compares a spawned vessel against the recording's end state. The "rows are written once" promise is broken in practice by rollout adoption, which rewrites a row's UT and capture order.

Four places pull against V's rulings and are listed for a decision, not overridden: never-reduce versus first-wins under retroactive insertion, append-only versus in-place re-tags, ghosts-never-touch-state versus the CommNet and contract exposure, and reject-only schema versus dropping part of a tree.

## Method

Each item is a known trap from the literature in the table below (area K, not owning the engine, comes from modding practice rather than one paper), checked against the design docs and the code on main (commit b2182df, 2026-11-04). Code claims cite file and line; nothing was built, flown or changed.

| Area | Literature the traps come from | What it maps to in Parsek |
| --- | --- | --- |
| A. Record and replay | rr, VMware record/replay, game replay files, Lamport clocks | Flight recorder, ghost playback, spawn at end |
| B. Event sourcing | Fowler, Young (CQRS), Kleppmann (DDIA ch. 11) | The game-actions ledger and its recalculation |
| C. Bitemporal data | Snodgrass, Jensen (valid time vs transaction time) | Rewind and Re-Fly writing rows about the past |
| D. Branching histories | Git object model, three-way merge, CRDTs (Shapiro 2011), OT | Recording trees, supersede rows, Re-Fly merges |
| E. Optimistic simulation | Jefferson's Time Warp (1985): state saving, anti-messages, GVT | Rewind points, tombstones, seals |
| F. Persistence and recovery | Write-ahead logging, ARIES, atomic rename, multi-file commit | .sfs plus sidecars, merge journal, load sweeps |
| G. Identity | Entity identity vs equality, Ship of Theseus under split and merge | persistentId vs launch guid, dock and undock |
| H. Numerics and frames | Floating origin, rotating frames, interpolation across discontinuities | Krakensbane, Zup drift, Relative vs Absolute sections |
| I. Host lifecycle | Reentrancy, observer-mutates-collection, teardown order | Scenario module destroy and load, index-keyed state |
| J. Verification | Model-based testing, differential testing, independent oracles | xUnit, harness lanes, ledger oracle, analyzer |

Verdicts use four grades. **Sound**: the design meets the trap head on. **Sound with caveat**: right decision, with a residual edge to watch. **Risky**: a plausible failure path exists and nothing mechanical guards it. **Wrong**: a verified defect or a decision the theory says will fail.

V's standing rulings (never reduce earned value, no paradoxes, append-only timeline, Re-Fly losses-only, quickload restores the save exactly, no backward compatibility before release) are taken as fixed. Where theory pulls against one, it is listed under the tensions section rather than called a bug.

## Findings ranked by risk

Thirteen new defects, none in the todo file as of b2182df. IDs stay stable for the fix threads; the Risk column carries the ranking after the independent review. "Confirmed" means the failing code path was read end to end. "Plausible" means the code shape is there but reaching it depends on a timing or a stock behaviour not yet proven live. Risk weighs impact against how often a player can reach the path.

| # | Finding | Theory it breaks | Evidence | Status | Risk |
| --- | --- | --- | --- | --- | --- |
| R1 | The Begin rollback removes only recordings tagged with the session id, which is the provisional alone. The Begin save already holds the committed attempt tree, its ledger rows, the crew swap and the attempt's live vessels, so the rollback cannot be an inverse. Payloads and EVA kerbals from the attempt stay committed and keep their ledger rows. The rewind design's recovery table says the Begin save holds only the journal. A sibling window: `CommitPendingTree`, the ledger filing and the crew swap run before any journal exists at all. | Write-ahead logging: undo must invert everything written before the log record. Time Warp: an anti-message for every message sent. | `MergeDialog.Commit.cs:117-138`, `MergeJournalOrchestrator.cs:225` and `:422-488`, `RecordingStore.cs:4215-4280`; doc line 1152 | Confirmed; reachable in-session (a throw in the Begin save or the fork migration, then any scene change runs the finisher) | High impact, rare |
| R2 | `OnSave` writes its phases in sequence and rethrows mid-sequence, with nothing in a `finally`. The ghost map strip is phase 15 of 16, so any earlier throw skips it and the save stamp. Whether stock keeps saving after a throwing `OnSave` is unverified. If it does, the Parsek node is partial (missions, ledger and rewind state may be missing) and ghost ProtoVessels land in persistent.sfs. The missing load-side backstop is deliberate. | Torn checkpoint; replay isolation; fail closed. | `ParsekScenario.cs:1541-1560`, `GhostMapPresence.cs:1027-1036` | Plausible (stock behaviour after a throw unknown) | High impact, rare |
| R3 | `OnVesselSOIChanged` returns unless on rails, so a burn or 1x coast across an SOI never closes the section. One section spans two bodies: the optimizer's "SOI traversal while burning splits" rule cannot fire, the section is labelled with the first body, and the atmosphere and altitude reseed and the finalization refresh are skipped. Playback still resolves each sample on its own body. | Record and replay: attach and detach points must be explicit. | `FlightRecorder.cs:12055`, `:5953-5960` (comment says off-rails is unreachable); `RecordingOptimizer.cs:2365-2373` | Plausible (needs a 1x SOI lane to prove stock fires the event unpacked) | Low-medium |
| R4 | Relative-frame metre offsets are also appended to the flat `Points` list. In a coverage gap with no orbit segment, playback falls back to the flat list and calls `GetWorldSurfacePosition` on them, which puts the ghost inside the planet. The sidecar writer substitutes body-fixed frames and the reader heals the flat list, so only recordings still in memory from the current session are exposed, at UTs between sections. | Typed frames: a value must not be readable without its frame tag (the exact CLAUDE.md hazard). | `FlightRecorder.cs:9757`, `:10610`; `ParsekFlight.cs:22964`, `:24039` | Plausible | Medium |
| R5 | Loop and overlap replays create a real, saved flag vessel each cycle. Dedup is "a flag within 1 m", so after the player recovers or deletes the flag, the next cycle plants a new one. | Replay must not repeat a side effect. Effects need an idempotency key, not a proximity test. | `GhostPlaybackLogic.cs:2912-3028`, `:1266` (loop reset of the flag cursor), `GhostVisualBuilder.cs:7630`, `ParsekKSC.cs:1316`, `:1375` | Confirmed (against the code's own stated intent) | Medium |
| R6 | During a deferred EVA or undock split the recorder is parked in `pendingSplitRecorder`. An `OnSave` in that window treats it as "outsider state" and skips the buffer flush, so an F5 there saves the tree without the parent's recent points or the branch. | Fuzzy snapshot: a checkpoint taken mid-transaction captures a state that never existed. | `ParsekFlight.cs:9542-9547`, `:12857`; `ParsekScenario.cs:2685-2709` | Plausible (the harness defers around it; players do not) | Medium-low |
| R7 | The science cap walks in subject units, but the pool gets units times the row's frozen gain multiplier. If the difficulty slider changed between two collections, a re-collection inserted earlier caps out the later, larger-multiplier row and the pool drops. | Monotonicity under retroactive insertion fails for non-monotone functions. | `ScienceModule.cs:488-500` | Plausible (needs a mid-career difficulty change) | Low |
| R8 | An unknown ledger action type id is logged and left as `ScienceEarning` (0), then re-saved as type 0. A row whose parse throws is dropped and the drop is saved. | Schema evolution: unknown records pass through untouched. | `GameAction.cs:1465-1476`, `Ledger.cs:1005-1011` | Confirmed (downgrade only) | Low |
| R9 | A cold load resolves sidecars from the head and `_versions` but not from unfiled `.incoming.` backups. These are filed only at the end of the same load, so a ghost is missing for one session. | Recovery must read every location the writer can leave data in. | `SidecarVersions.cs:399-420`, `ParsekFileCollector.cs:228`, `SidecarFileCommitBatch.cs:348-362` | Plausible (needs a locked file or crash mid-write) | Low |
| R10 | `mergeState` parses without `Enum.IsDefined`, and an unknown name falls back to `Immutable`. A missing `recordingId` keeps a random Guid instead of rejecting the recording. | Reject what you cannot read; never default to the most committed state. | `RecordingTreeRecordCodec.cs:919`, `:490-492` | Confirmed | Low |
| R11 | Nothing enforces at most one supersede row per old recording id. Both walkers take the first match, so a duplicate would make the slot tip depend on list order. | A branch ref must be a function. | `EffectiveState.cs:127-135`, `:313-323`; `SupersedeCommit.cs:388` | Latent (same class as filed todo C5) | Low |
| R12 | `RestoreFromSnapshotForTesting` clears and refills the committed list with no index notifications (it does bump `StateVersion`). It runs around in-game tests in FLIGHT with ghosts alive. | Observer contract: every mutation of an index-keyed list notifies. | `RecordingStore.cs:4973-4975` | Plausible (test runner only) | Low |
| R13 | Spawn coordinates are written with `ToString("R")` and no culture. | Invariant serialization (house rule). | `VesselSpawner.cs:1271-1273` | Confirmed (safe in game thanks to the "en" pin) | Hygiene |

The documentation also drifts from the code in six places. Each one would mislead the next reviewer:

- Rewind design line 1152 (the Begin rollback, see R1).
- Rewind design 2.2: "no other field is rewritten" on a committed recording, while the optimizer and name store rewrite fields.
- Game-actions 2.1: raw values from KSP are "written once and never changed", while rollout adoption rewrites a row's UT and capture order (re-tags change attribution only).
- Flight recorder 15.2 versus 15.5: ghosts "are not vessels" (hedged with "unless explicitly registered") versus ProtoVessel ghosts.
- Ghost rendering 15.6 "SOI Transition Mid-Burn" promises a hard SOI checkpoint on both sides, which an off-rails crossing never produces (R3).
- Flight recorder 13.6 and 13.9: no overlap check for unloaded spawns, while the code nudges them apart.

## Full catalogue with verdicts

Fifty-three traps across eleven areas: 19 sound, 30 sound with caveat, 4 risky, none wrong at the design level. The four risky items each sit on top of a finding above.

| ID | Known trap | What Parsek decided | Verdict |
| --- | --- | --- | --- |
| **A. Record and replay** | | | |
| A1 | Re-simulating a nondeterministic engine on playback diverges | Playback only interpolates recorded points or evaluates recorded Kepler segments. Predicted tails are flagged `isPredicted` and never merged with recorded ones. | Sound with caveat: a predicted atmospheric tail copies stock's unloaded deletion, not a loaded descent |
| A2 | State captured only at the edges goes stale before use | Per-sample pose and velocity. Parts, crew, resources and modules come from one snapshot, refreshed only while the vessel lives. | Sound with caveat: a background vessel that dies before finalize spawns from its split-time snapshot |
| A3 | Replayed objects visible to the live system cause effects | Twelve guarded exposure paths: off-rails, colliders, CommNet node, active vessel, targeting, CheckKill, SOI, Tracking Station, save budget, save strip. | Sound with caveat: see R2, R5 and tension T3 |
| A4 | Without a replay-versus-record check, divergence is silent | Offline analyzer invariants INV1-INV12, also in game. Spawn-death loop guard. | Sound with caveat: nothing compares a spawned vessel with the recording's end state |
| A5 | Gaps at attach and detach are interpolated as motion that never happened | Checkpoints at rails, focus, warp and scene changes. Gaps are implicit; debris is retired, not clamped. | **Risky**: a gap with no orbit segment lerps silently (R3, R4) |
| A6 | Wall clock or frame count used where game time is needed | UT everywhere. Real time only for timeouts and stamps: spawn retries, the stock-action intent TTL, the collector's 14-day quarantine, journal and marker stamps, FX smoothing. | Sound |
| A7 | Materializing a replay into a world that changed since | Bounding-box check and walkback when loaded. Nudge apart when unloaded. Spawn-death guard. | Sound with caveat: no check for unloaded vessels in the same orbital slot; docs lag code |
| **B. Event sourcing** | | | |
| B1 | Editing events in place instead of compensating | Tombstones are true compensating events. Re-tags, rollout adoption (UT and cseq rewritten), held-window re-dating and legacy repair edit rows in place. | **Risky**: tension T1; the doc overstates immutability |
| B2 | A stored projection becomes the authority | Full replay every recalculation. A ground-truth harness diffs against KSP's own save. | Sound with caveat: `StockPoolReference.Counted` is a stored per-row projection read back |
| B3 | Same-timestamp events need a total, stable order | Sorted by UT, then earning class, cseq, Sequence; stable `OrderBy`. | Sound with caveat: unsequenced legacy rows fall back to list position |
| B4 | Double capture, or none while unsubscribed | Dedup on the capture sequence and channel. Leak rows backstop the three pools. | Sound with caveat: no backstop for non-pool events fired between OnDestroy and OnLoad |
| B5 | Replaying fires side effects that are captured again | Every patch runs under `SuppressionGuard`. Pool writes use `TransactionReasons.None`. | Sound |
| B6 | Old readers mangle unknown event types | A version gate loads an empty ledger on mismatch. Reserved ids are dropped. | Sound with caveat: unknown types are coerced (R8) |
| B7 | Two writers to one store lose or double-count updates | Patch-to-target against an incremental reference. Leaks are filed as `StockPoolAdjustment` rows. | Sound with caveat: a recompute that changes a past row's effect is absorbed as a leak (tension T2) |
| B8 | Derived events must follow their cause | Conversion legs bind to their cause and rescale. Death penalties are re-tagged with the death. | Sound with caveat: a leg with a missing cause applies as captured |
| B9 | The log grows without bound and replay cost grows with it | Full O(n log n) replay per recalculation, cached per `StateVersion`. Not per frame. | Sound for now: watch long careers |
| **C. Bitemporal data** | | | |
| C1 | Mixing valid time with transaction time | UT is valid time and cseq is transaction order. Take-back and floors read cseq. | Sound with caveat: science rows get their cseq at commit, not capture |
| C2 | Retroactive insertion lowers later values when functions are not monotone | Re-Fly is checked by a dominance re-walk. Rewind-to-Launch relies on construction (frozen amounts, first-wins). | **Risky**: R7 and tension T2 |
| C3 | Rows dated with the wrong clock | Science rows now use capture UT (fixed). Re-dating is limited to the held window and rollout adoption. | Sound |
| **D. Branching histories** | | | |
| D1 | Rewriting history breaks external references | Supersede is a separate relation. The optimizer re-tags ledger rows and repoints roots, and refuses merges across supersede rows. | Sound with caveat: supersede, tombstone and slot ids are not remapped; safe today only because the predicates are complementary |
| D2 | Conflicts need one deterministic winner per unique resource | Earliest effective row wins. Click-blocks guard purchases. Guid identity and holds guard vessels. Re-Fly blocks switching. | Sound with caveat: the last tie-break is commit order |
| D3 | A three-way merge needs a valid base | No stored Parsek base. Gates re-run against the current ledger at the click. `CanInvoke` refuses a missing, future or foreign rewind point. | Sound |
| D4 | Garbage collection must never delete reachable data | Mark and sweep from every .sfs and backup plus memory. Fails closed, quarantines for 14 days, skips during an open journal. | Sound |
| D5 | Exactly one HEAD per slot, cycle-safe | `EffectiveTipRecordingId` walks chain and supersede with one shared visited set. | Sound |
| D6 | Merge results depend on merge order | ERS and ELS are set operations. Admissibility gates read the ledger as it stands at each merge. | Sound with caveat: serializable but not commutative, which is acceptable |
| D7 | A ref must name one target | Nothing enforces one supersede row per old id. | Sound with caveat: R11 |
| **E. Optimistic simulation (Time Warp)** | | | |
| E1 | Rollback needs a full state save | The rewind point saves stock state. Parsek state is carried from memory. Stock is repaired by recalculation and holds. | Sound with caveat: third-party scenario modules come back at the rewind point and are never repaired |
| E2 | Every effect sent needs an anti-message on rollback | Ledger removal, crew unreserve, name epoch undo and recalculation all exist. | **Risky**: R1 is the one path without its anti-message |
| E3 | Irrevocable output must wait for global commit | No Parsek file is destroyed before durability. GameEvents to other mods and screen messages cannot be recalled. | Sound with caveat: tension T5 |
| E4 | Paradoxes: an effect before its cause | Value gate, loss swap, switch block, claimed-vessel hold, dominance world clause, committed-future click-blocks. | Sound with caveat: two known gaps already filed |
| E5 | A multi-step commit must survive a crash at every step | The journal is a redo log. Every post-Begin phase re-runs idempotently. | Sound with caveat: the Begin rollback is not a true inverse (R1) |
| **F. Persistence and recovery** | | | |
| F1 | Torn writes and missing-file windows | Temp file plus `File.Replace`, with batch rollback for sidecars. | Sound with caveat: no fsync anywhere, the same as stock |
| F2 | An older snapshot paired with newer shared file bytes | Saves name content hashes. Replaced bytes are kept under `_versions`. A cold load resolves the named version. | Sound: the most important item, done right; edge in R9 |
| F3 | A checkpoint taken mid-transaction | One thread, so saves fall on frame boundaries. `OnSave` flushes the recorder. | Sound with caveat: R6 |
| F4 | Silent defaults on missing or unknown fields | Generation gate rejects; `Enum.IsDefined` on most enums. | Sound with caveat: R10, and partial trees drop rejected members (T6) |
| F5 | Culture-dependent number formats | Invariant culture with "R" on every codec site. | Sound (R13 is the one exception) |
| F6 | Dangling references across files | Failed sidecars keep their hash. Unserializable trees carry the last good node. Orphans are logged. | Sound |
| F7 | Two writers to one save file | Parsek no longer writes quicksave.sfs. The persistent.sfs writes are named and few. | Sound with caveat: an unannounced load by another mod keeps memory's state (T4) |
| F8 | Carrying memory across a restore | Save-file loads are cold. Memory is carried only on scene changes and named in-session loads. | Sound |
| **G. Identity** | | | |
| G1 | Reused ids mistaken for the same object | `persistentId` is craft-baked, so identity is gated on the per-launch guid at every adoption site checked. | Sound |
| G2 | Identity across split and merge | Dock-absorb rows and undock links. A merged vessel carries the dominant identity. | Sound with caveat: same-craft docks resolve on the initiator branch only |
| **H. Numerics and frames** | | | |
| H1 | Floating origin and Krakensbane offsets | Corrected velocity at every sample site. | Sound |
| H2 | Caching a rotating-frame angle | Zup is computed per pass and never cached. | Sound |
| H3 | Interpolating between samples in different frames or bodies | Relative and checkpoint sections dispatch to their own resolvers. | Sound with caveat: cross-body sample pairs lerp in world space (R3); Hermite is off and mixes frames |
| **I. Host lifecycle** | | | |
| I1 | Handlers running after teardown; static state leaking across saves | `OnDestroy` unsubscribes everything. A save-folder change resets 13 named classes under a test gate. | Sound with caveat: a new static outside those 13 is not caught |
| I2 | Index-keyed observers not told about shifts | Every insert and remove goes through the notification primitive. | Sound with caveat: R12 |
| **J. Verification** | | | |
| J1 | The operation-by-state space explodes | `LoadReconcilePolicy` enumerates 22 state categories by load kind, gated in xUnit. | Sound with caveat: loads only; the operation alphabet (FZ-1 lane) is still open |
| J2 | Hand-written cases miss interleavings | Seeded property fuzzers for recalculation, effective state, Re-Fly value and routes. Planted bugs turn them red. | Sound |
| J3 | An oracle written from the code it checks | The ledger ground-truth harness diffs against KSP's own save. The Re-Fly fuzzer oracles come from the design text. | Sound |
| J4 | Invariants checked only offline | Analyzer rules also run in game as `RecordingInvariants`. | Sound |
| J5 | Incremental state drifts from a full recompute | Leak measurement on every patch, plus the ground-truth harness. | Sound |
| **K. Not owning the engine** | | | |
| K1 | Hidden global state in the host and its mods | Stock pools, contracts, roster, tech and facilities are re-patched. Mods' scenario modules are out of scope (KCT and RP-1 ruled out). | Sound with caveat: undocumented for players with other stateful mods |
| K2 | Nondeterminism and patch fragility | Outputs are recorded, not inputs. Harmony targets are pinned by decompile and by tests that check the stock target exists. | Sound |

## Where theory pulls against a ruling

None of these overrides a ruling. Each names what the theory predicts, so V can confirm the trade or adjust it.

| # | Ruling | What theory says | Where it shows | Suggested stance |
| --- | --- | --- | --- | --- |
| T1 | Append-only timeline; rows written once | Event stores stay immutable and correct by compensating events, never by edits | Ledger re-tags, rollout adoption rewriting UT and cseq, optimizer merges and splits of committed recordings | Keep the edits, but restate the invariant precisely: a row's identity and value never change, while its attribution may be re-homed. Rollout adoption's cseq rewrite is the one edit worth replacing with a new row. |
| T2 | Never reduce earned value; earliest wins | First-wins, caps and curves are not monotone, so inserting an earlier row can lower a later value. Only a check, not construction, guarantees monotonicity. | Re-Fly has the dominance check. Rewind-to-Launch has none. R7 shows one concrete drop. A smaller earlier price can also demote a larger later one. | **Ruled by V 2026-11-04:** a later earning of a once-only reward tops up to its own amount, so the total is the larger of the two (earn 15 at T1, rewind, earn 10 at T0: T0 pays 10 and T1 pays 5). This closes R7 and the price-demotion case by construction; built in the codec-cleanup fix thread. |
| T3 | Ghosts never modify game state | Replayed objects must be invisible to every live consumer | Ghosts keep their recorded vessel type with Owned discovery, so vessel-counting contracts (Contract Configurator) can see them; the CommNet control point is a ruled exception; flags are real by design (R5) | Confirm the CommNet exception stays. Treat contract visibility as a risk to measure with one lane. |
| T4 | A quickload restores the save exactly | A restore with memory carried across is a second source of truth | A load another mod triggers without stock announcing it is classified in-session and keeps memory's lists. The canary only warns; the load design's risk table accepts this with the canary as mitigation. | Fine for stock. For mods, consider treating an unannounced stamp mismatch as cold. |
| T5 | No time-travel paradoxes | Time Warp can cancel only the messages it sent. Output already delivered is irrevocable. | GameEvents already delivered to other mods; mods' scenario modules come back at the rewind point while Parsek keeps the committed future | Not fixable without owning those mods. State the scope in the user guide and the mod notes. |
| T6 | No backward compatibility; reject other schema generations | Rejected data should pass through unread, not vanish | A whole rejected tree is kept inert, but a tree with only some members rejected drops them on the next save, and the collector later removes their files. The GC3 todo entry already records this as an accepted scope limit. | Keep the rejected members inert the same way, or accept the loss in writing. |

## Independent review

A fresh reviewer with no context from the audit re-read every finding against the code on 2026-11-04 and tried to refute it. None was refuted. R1 was upgraded (reachable in-session, no crash needed). R2, R3 and R4 were narrowed: stock's behaviour after a throwing save is unknown, playback resolves cross-body samples correctly, and the sidecar codec heals the flat list on disk. The review added the pre-journal commit window to R1, the whole-`OnSave` fail-closed question to R2, and a sixth doc drift. Counts, J1 and A6 were corrected. Spot checks of B5, D4, D5, F5, G1 and I1 held.

## Proposed follow-up threads

Five fix threads and one decision round, in priority order. All fit the bugs-before-release priority, and none needs a design change.

1. **Fix the interrupted-merge rollback (R1).** Widening the rollback to the attempt-owned set the Discard paths use (`CollectReFlyAttemptOwnedRecordingIds`) is one option; driving Begin forward instead, or writing the journal before `CommitPendingTree`, may be cleaner because the Begin save already holds the committed tree. Cover the pre-journal window too. Correct the design's recovery table, and add a cell to `MergeCrashRecoveryMatrixTests` with a decoupled payload.
2. **Harden the save path (R2, R6).** Give `OnSave` a fail-closed shape as a whole: strip ghost ProtoVessels first or in a `finally`, and decide what a partial Parsek node should do. Flush the parked split recorder in `SaveActiveTreeIfAny`, or defer the save state the way the harness already does.
3. **Close frame and body seams in the recorder (R3, R4).** Open a section boundary on an off-rails SOI change, and make the flat-point fallback refuse or dispatch Relative points. Add one lane: a 1x burn through the Mun SOI.
4. **Make flag replay idempotent (R5).** Key planted flags on recording id and event index, and plant once per real run, never per loop cycle.
5. **Codec and invariant hygiene batch (R8 to R13).** Guard `mergeState` and missing ids, pass unknown ledger types through, read `.incoming.` backups on a cold load, add an analyzer rule for one supersede row per old id, notify on the test snapshot restore, and add culture to the spawn coordinates.
6. **Decision round for V.** T2 is ruled (top-up to the larger amount, see the tensions table). Still open: T6 (partial-tree drop), and a player-facing scope note for third-party mod state (T5, K1). The scope note is written: user guide "Other Mods and Rewinds" and `docs/dev/mod-compatibility-notes.md` "Any mod with its own saved state" (2026-11-04).

The theory also names two investments that would catch whole bug classes rather than single bugs. The first is the FZ-1 lane already filed: seeded random sequences over the full operation alphabet (record, rewind, Re-Fly, merge, discard, save, load, quit), with invariants after each step. The second is a post-spawn divergence check that logs when a materialized vessel's orbit, crew or resources differ from the recording's end state.
