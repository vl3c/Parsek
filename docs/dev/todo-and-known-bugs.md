# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18. Closed during archival: PR #307 career-earnings-bundle post-review follow-ups (all four fixes confirmed in code — `PickScienceOwnerRecordingId`, `DedupKey` serialization, `FundsAdvance` in ContractAccepted, `MilestoneScienceAwarded` field), #337 (same-tree EVA LOD culling — fix shipped in PR #260, stale), #368 / #367 / #364 (PR #240 / #242 / #229 follow-ups — done).

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## Priority queue — deterministic-timeline correctness

The four top-of-queue correctness fixes (#431, #432, #433, #434) shipped in the v0.8.2 cycle. Remaining follow-up: retire `MilestoneStore.CurrentEpoch` as the legacy work-around (now redundant with purge-on-discard + ghost-only event filtering). See #431's notes in `done/todo-and-known-bugs-v3.md`.

---

# Known Bugs

## 480. `FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` / `FailedActivation_DoesNotEmitEvent` NRE ~2ms into SPACECENTER run on a career save with an activatable stock strategy

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt` + `KSP.log:9471-9474`.

```
[01:20:32.161] [VERBOSE][TestRunner] Running: FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents
[01:20:32.163] [WARN][TestRunner]    FAILED: ... - Object reference not set to an instance of an object
[01:20:32.169] [VERBOSE][TestRunner] Running: FlightIntegrationTests.FailedActivation_DoesNotEmitEvent
[01:20:32.170] [WARN][TestRunner]    FAILED: ... - Object reference not set to an instance of an object
```

~2ms from `Running:` → `FAILED:` on both, so the NRE fires very early in the test body. Save is career (`saves/c1/persistent.sfs` shows `Mode = CAREER`), so the career-mode and `StrategySystem.Instance != null` guards at `RuntimeTests.cs:3915-3932` both pass. The NRE happens further in — likely around `FindActivatableStockStrategy()` (`:3891-3907`) reading `strategy.Config.Name` when `Config` is momentarily null, `SnapshotFinancials()` (`:3847-3855`, defensive — probably not it), or `strategy.Activate()` call path (`:3975`) throwing in stock code for some reason.

**Concern:** these are both `#439` Phase A regression tests for `StrategyLifecyclePatch`. A failure here means one of: (a) the patch is throwing and bypassing the expected StrategyActivated emission, (b) the test helpers are fragile against the particular career-save state, (c) stock's `Strategy.Activate()` itself NREs on this save shape. Without a stack trace the three can't be distinguished from the log alone — the test runner reports `ex.Message` only.

**Fix:** first widen the test runner's failure capture so we get the stack trace. Add one line to whatever catches the test exception (grep for `FAILED:` emit site in `InGameTestRunner.cs`) to log `ex.ToString()` at WARN instead of just `ex.Message` — a stack trace turns this into a 5-minute fix instead of a week of guessing. Once the stack lands, root-cause the NRE:

- If it's `strategy.Config.Name` → tighten the null guard in `FindActivatableStockStrategy` to also require `s.Config.Name != null`.
- If it's inside `StrategyLifecyclePatch` postfix → the patch is throwing in a stock code path it didn't previously handle; fix the patch.
- If it's inside stock's `Activate()` → log a skip with the offending strategy's configName so future investigation has the signal, and move on.

Separately: the same save-state shape may make #439 Phase A behaviour unreliable in production, not just in the test harness. If the post-fix investigation reveals `StrategyLifecyclePatch` is the thrower, that's a shipped bug, not just a test fail.

**Files:** `Source/Parsek/InGameTests/InGameTestRunner.cs` (add `ex.ToString()` to the FAIL log), `Source/Parsek/InGameTests/RuntimeTests.cs:3891-3907` (possibly harden `FindActivatableStockStrategy`), `Source/Parsek/Patches/StrategyLifecyclePatch.cs` (if the postfix is implicated).

**Scope:** Small after the stack trace lands. Investigate first — don't patch blindly.

**Dependencies:** none (the other StrategyLifecycle work is on main already).

**Status:** TODO. Priority: medium — test regression on main, user-visible if the underlying NRE also fires during normal career play.

---

## 479. `FlightIntegrationTests.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty` fails in FLIGHT — `sit` field not refreshed from the live vessel after stable-terminal re-snapshot

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt:18, 41`.

```
FAIL  FlightIntegrationTests.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty (1.0ms)
      Snapshot sit field must be refreshed from the live vessel, not preserved from the stale source
```

**Concern:** the #289 re-snapshot invariant is that when `FinalizeIndividualRecording` runs on a stable-terminal recording (`TerminalStateValue` set) with a live active vessel, the recording's `VesselSnapshot` gets replaced by a fresh snapshot from that vessel, and `sit` should reflect the vessel's actual situation (LANDED/SPLASHED/etc.), not the stale "FLYING" from the original snapshot. The test at `RuntimeTests.cs:3219-3286` builds a recording with `TerminalStateValue = Landed` and a stale `sit=FLYING` snapshot, invokes `FinalizeIndividualRecording(rec, ..., isSceneExit: true)`, then asserts `sit != "FLYING"` — and that assertion fails (`:3276-3277`). So the current code either (a) doesn't replace the snapshot at all, (b) replaces it with a fresh snapshot whose `sit` was also written as FLYING (bug in `BackupVessel()` or equivalent), or (c) replaces it but doesn't persist the new `sit` value.

Corresponding post-#289 re-snapshot path in `ParsekFlight.cs` is around `:6917-6928` (the `backfilled TerminalOrbitBody=` logs visible in earlier collected logs confirm this path fires). Check whether the path calls `vessel.BackupVessel()` and writes the returned ConfigNode to `rec.VesselSnapshot`, or whether it only updates specific fields and skips `sit`.

**Concern (downstream):** if the re-snapshot keeps the stale FLYING sit, the spawn path at `VesselSpawner` (`ShouldUseRecordedTerminalOrbitSpawnState`, `:707`) or `SpawnAtPosition`'s situation override (`:317-320`) will receive a recording whose snapshot sit contradicts the terminal state — the spawner already has defensive overrides for this shape (`#176 / #264` per code comments), but the re-snapshot path fighting them is a separate source of drift and may silently persist the wrong sit to the sidecar (next load sees FLYING).

**Fix:** trace the re-snapshot invocation site and confirm it calls `vessel.BackupVessel()` fully, then writes the result to `rec.VesselSnapshot` (the full ConfigNode, not field-by-field). If it already does that, check whether `BackupVessel()` for a LANDED-situation vessel actually emits `sit = LANDED` (some stock KSP snapshot paths capture from a cached state that may still read FLYING for one frame after situation transition — a `yield return null` / physics-frame wait before the re-snapshot closes that). Add an explicit `sit` override on the fresh snapshot derived from `rec.TerminalStateValue` so the stored value always matches the declared terminal, regardless of when the snapshot capture fires relative to KSP's situation-update tick.

Test should keep passing once the path writes a consistent `sit`; no other assertion in the test needs changes.

**Files:** `Source/Parsek/ParsekFlight.cs` (re-snapshot path near `:6917`), possibly `Source/Parsek/VesselSpawner.cs` (`BackupVessel` usage), `Source/Parsek/InGameTests/RuntimeTests.cs:3219-3286` (no changes — the test is correct as-is).

**Scope:** Small. Likely a 5-line fix to force-set `sit` from the terminal state after `BackupVessel()`.

**Dependencies:** #289 original fix (shipped). This is the regression test catching a hole the original fix left.

**Status:** TODO. Priority: medium-high — silent sidecar drift on every stable-terminal finalize with a mismatched-sit live vessel, reloads see stale FLYING, affects spawn decisions downstream.

---

## 478. `RuntimeTests.MapMarkerIconsMatchStockAtlas` runs in EDITOR / MAINMENU / SPACECENTER where `MapView.fetch` doesn't exist — should be scene-gated to FLIGHT + TRACKSTATION only

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt:15, 21, 24, 434-438`.

```
[MapView]
  RuntimeTests.MapMarkerIconsMatchStockAtlas
    EDITOR         FAILED  (0.1ms) — MapView.fetch should exist — test requires flight or tracking station scene
    FLIGHT         PASSED  (0.5ms)
    MAINMENU       FAILED  (1.5ms) — MapView.fetch should exist — test requires flight or tracking station scene
    SPACECENTER    FAILED  (0.2ms) — MapView.fetch should exist — test requires flight or tracking station scene
    TRACKSTATION   PASSED  (3.4ms)
```

**Concern:** the `[InGameTest(Category = "MapView", ...)]` attribute at `RuntimeTests.cs:511-512` has no `Scene =` property, which defaults to `InGameTestAttribute.AnyScene = (GameScenes)(-1)` (`InGameTestAttribute.cs:18,21`). The test body requires `MapView.fetch` (available only in FLIGHT and TRACKSTATION per KSP's scene model) and correctly asserts its existence, but surfaces that assertion as a FAIL rather than a skip. Net effect: 3 of 5 scenes report FAIL for a test that is *expected* to only run in 2 scenes.

The `InGameTestAttribute` only supports a single `GameScenes` value; it can't express "FLIGHT OR TRACKSTATION" directly. Two valid fixes:

1. **Extend the attribute to accept a scene set.** Add a `GameScenes[] Scenes` property (or convert `Scene` to a `[Flags]`-like mask) and update `InGameTestRunner` scene-filter logic to match if any listed scene equals the current scene. More invasive; future-proofs other tests.
2. **Skip at the top of the test body** (`RuntimeTests.cs:513-…`) when `HighLogic.LoadedScene` is not FLIGHT or TRACKSTATION: `if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.TRACKSTATION) { InGameAssert.Skip("requires MapView scene"); return; }`. One-method change, keeps other callers of the attribute unaffected.

Option 2 is the cheapest and matches what several other tests already do internally (see `StrategyLifecycle` tests at `:3915-3932` for the skip pattern). Option 1 is worth doing only if a batch of other tests would benefit.

**Fix:** option 2 — add the scene skip at the top of `MapMarkerIconsMatchStockAtlas`. Optionally also audit other `Category = "MapView"` / `Category = "TrackingStation"` tests for the same scoping issue; grep `InGameTest\(Category = "\(MapView\|TrackingStation\)"` and verify each either sets `Scene = GameScenes.FLIGHT` / `TRACKSTATION` or skips internally.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs:513` (add skip), optionally `Source/Parsek/InGameTests/InGameTestAttribute.cs` if option 1 is chosen.

**Scope:** Trivial. 3-line skip + audit of adjacent tests.

**Dependencies:** none.

**Status:** TODO. Priority: low — pure test hygiene, no user-visible impact. But 3 false FAILs per test run drowns the signal in the report and should be closed.

---

## 477. Ledger walk over-counts milestone rewards — post-walk reconciliation `expected` sum is a 2× / 3× multiple of the actual stock enrichment

**Source:** `logs/2026-04-19_0117_thorough-check/KSP.log`. Worked example for one Mun/Flyby milestone:

```
19799: [INFO][GameStateRecorder] Milestone enriched: 'Mun/Flyby' funds=13000 rep=1.0 sci=0.0
22085: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, funds): MilestoneAchievement id=Mun/Flyby expected=26200.0  (← 2× actual)
22086: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, rep):   MilestoneAchievement id=Mun/Flyby expected=3.0      (← 3× actual)
22087: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, sci):   MilestoneAchievement id=Mun/Flyby expected=1.0      (← actual is 0.0)
```

Same shape across `RecordsSpeed`, `RecordsAltitude`, `RecordsDistance` (hundreds of WARNs with expected 14400 / 9600 while stock gives 4800 per trigger — the Post-walk reconcile summary reports `actions=24, matches=0, mismatches(funds/rep/sci)=24/14/6` meaning ZERO of 24 actions matched; every single one had funds over-counted, 14 had rep over-counted, and 6 had sci incorrectly expected when the enrichment shows `sci=0.0`).

**Concern:** the reconciliation WARN phrasing ("no matching event") was misleading me earlier — on re-read, `CompareLeg` (`LedgerOrchestrator.cs:4248-4310`) sums `summedExpected` from `SumExpectedPostWalkWindow` which collects *every matching leg* across actions within the coalesce window. If the ledger holds multiple actions for the same milestone-at-same-UT (e.g. one per recording finalize pass, or one per recalc that re-replayed the same enrichment), `summedExpected` becomes N × the actual stock reward even though stock only fired it once. The reconciliation then correctly flags the mismatch — but the real bug is upstream: **the ledger is emitting a `MilestoneAchievement` action more than once per stock milestone fire**.

The duplicate emissions correlate with `actionsTotal` growing across recalcs even when no new stock events happened (`RecalculateAndPatch: actionsTotal=32 → 32 → 32 → 39` over a ~20ms span near `:01:07:54`). Each bump is a recording's commit path replaying its enrichment into the ledger without dedup.

This supersedes / refines #462 (prior observation was "double-count for a single milestone"; #477 is the general case across every milestone). #469's "zero-match" shape is a *different* manifestation of the same coupling — there the `events` list simply doesn't reach `CompareLeg`, so observed=0 while expected is the N× sum. Fix #477 first; depending on the root cause, #469 may resolve simultaneously.

**Fix:** trace the emission path. Two hypotheses, in priority order:

1. **Duplicate action insertion.** Every `LedgerOrchestrator.NotifyLedgerTreeCommitted` (or wherever `MilestoneAchievement` actions are created from recording state) re-inserts the action without checking whether an equivalent action already sits in the ledger. Expected fix: dedup by `(MilestoneId, UT, RecordingId)` — if the same triple is already present, skip. Watch out for the repeatable-record semantics (`RecordsSpeed` can legitimately fire multiple times per session at different UTs; dedup must be per-UT, not per-id).
2. **Recalc replay re-walks committed recordings without clearing prior action copies.** Between `Post-walk reconcile: actions=32` and `actions=39`, seven actions were added without corresponding new stock events. If `RecalculateAndPatch` clears the ledger and re-walks, it should land on the same 32 actions; if it doesn't clear first, each recalc adds another copy. Verify the clear path in `RecalcEngine.cs` / `LedgerOrchestrator.RecalculateAndPatch` entry.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`NotifyLedgerTreeCommitted`, `RecalculateAndPatch`), `Source/Parsek/GameActions/MilestonesModule.cs` (or whichever module emits `MilestoneAchievement` actions). Test: xUnit seeding a recording with one `MilestoneAchieved` event, calling `NotifyLedgerTreeCommitted` twice (simulating double-commit), asserts the ledger action count does not double and post-walk reconcile reports `matches=1, mismatches=0`.

**Scope:** Medium. Finding the exact emit site is the work; once identified, the dedup or clear-first fix is ~5 lines.

**Dependencies:** read `#307 / #439 / #440 / #448` notes in `done/todo-and-known-bugs-v3.md` first — those touched the earnings-reconciliation path and clarify which side of the dedup is the correct place to land the fix.

**Status:** TODO. Priority: **high** — every career/science session produces 500+ false-positive WARNs that obscure real reconciliation signals. Blocks any improvement to the signal/noise ratio of `[LedgerOrchestrator]` logs.

---

## 476. Post-walk reconciliation runs in sandbox mode (where KSP does not track funds/science/rep) and floods the log with "store delta=0.0" and "no matching event" false positives

**Source:** `logs/2026-04-19_0117_thorough-check/KSP.log`. The session was sandbox mode (`Funding.Instance is null (sandbox mode) — skipping`, `ResearchAndDevelopment.Instance is null (sandbox mode) — skipping`, `Reputation.Instance is null (sandbox mode) — skipping`, all repeating throughout) yet:

```
[WARN][LedgerOrchestrator] Earnings reconciliation (funds): store delta=0.0 vs ledger emitted delta=72800.0 — missing earning channel? window=[15.1,79.9]
[INFO][LedgerOrchestrator] Post-walk reconcile: actions=24, matches=0, mismatches(funds/rep/sci)=24/14/6, cutoffUT=null
```

`actions=24, matches=0` every single reconcile sweep — because stock KSP doesn't fire the Funds/Science/Reputation changed events in sandbox, so the store has nothing to compare against. The reconciliation is doing work and producing noise that has no actionable meaning on this save.

**Concern:** `LedgerOrchestrator.RecalculateAndPatch` unconditionally runs `ReconcilePostWalkActions` (and the window-level variant that emits the `store delta=0.0 vs ledger emitted delta=N — missing earning channel?` lines) regardless of whether KSP's tracked state is available. In sandbox every reconcile fires the full set of "mismatch" WARNs because the comparison baseline is zero. This compounds with #477 (duplicate emissions) to produce 700+ WARNs per session on a sandbox save that should have no WARNs at all.

Same concern applies to any save where the relevant `*.Instance` accessor is null for a legitimate game-mode reason (sandbox, tutorial, scenario that disables the currency).

**Fix:** at the entry of `ReconcilePostWalkActions` and `ReconcileEarningsWindow` (`LedgerOrchestrator.cs` around `:430-451` and `:4230` onward), gate the reconciliation per-resource on the KSP singleton availability. Pseudocode:

```csharp
bool fundsTracked = Funding.Instance != null;
bool sciTracked   = ResearchAndDevelopment.Instance != null;
bool repTracked   = Reputation.Instance != null;
// ... skip fund/sci/rep legs individually when their tracker is null
if (!fundsTracked && !sciTracked && !repTracked) return;
```

Log a single one-shot VERBOSE `[LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds={f} sci={s} rep={r})` so the skip is observable without being repeated every recalc. Existing `PatchFunds: Funding.Instance is null (sandbox mode) — skipping` pattern at `KspStatePatcher.cs` is the template.

Per-leg gating (not whole-sweep) is the correct granularity — a save that disables only one currency should still reconcile the other two.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`ReconcilePostWalkActions`, `ReconcileEarningsWindow`, `CompareLeg` entry). Test: xUnit seeding a `Funding.Instance = null` state (if the test rig can stub it) or verifying via the log sink that no WARN fires when `RecalculateAndPatch` is called with the trackers disabled.

**Scope:** Small. ~15 lines of gate + one log line.

**Dependencies:** none. Independent of #477 — fixing this one also reduces the reproducibility noise around #477 and #469 since a sandbox session after this fix would emit zero reconciliation WARNs.

**Status:** TODO. Priority: medium — pure log-hygiene, but the hygiene payoff is large (a sandbox session goes from ~700 WARNs to 0).

---

## 475. Ghost whose recording terminates in Mun orbit spawns on a Kerbin-SOI-eject trajectory instead of in Mun orbit (post-rewind, map-view watch)

**Source:** user playtest report — "when recording a trip that ends in Mun orbit, after rewind when watching the ghost in map view, the ghost gets to the Mun encounter but then instead of spawning in Mun orbit, it spawns in a Kerbin SOI eject trajectory."

**Concern:** the ghost playback correctly walks orbit segments through the SOI change (user visually confirms "the ghost gets to the Mun encounter" in map view), but the *real vessel spawn* at the recording's terminal UT resolves the spawn body incorrectly. `VesselSpawner.SpawnOrRecoverIfTooClose` (`Source/Parsek/VesselSpawner.cs:499-502`) picks the body via:

```csharp
string spawnBodyName = useRecordedTerminalOrbit
    ? rec.TerminalOrbitBody
    : RecordingEndpointResolver.GetPreferredEndpointBodyName(rec);
CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == spawnBodyName);
```

Then passes `body` into `TryResolveRecordedTerminalOrbitSpawnState → TryBuildRecordedTerminalOrbitForSpawn` (`:520-522`, `:2566-2609`). If `rec.TerminalOrbitBody == "Kerbin"` but the stored orbit parameters (SMA ≈ Mun's semi-major axis of ~200km-13Mm range, inclination, eccentricity) were captured in Mun's reference frame, applying them to Kerbin creates an orbit whose apoapsis is well past Kerbin's SOI boundary — a "Kerbin SOI eject trajectory" is the exact shape you'd get.

Primary suspect: the capture site at `ParsekFlight.cs:7547`:

```csharp
rec.TerminalOrbitBody = orb.referenceBody?.name ?? "Kerbin";
```

The `?? "Kerbin"` fallback silently writes the wrong body when `orb.referenceBody` is momentarily null (e.g. during a mid-frame SOI transition snapshot, or during a rewind where the vessel's orbit reference is being rebuilt). If this fallback fires when the vessel *was* orbiting Mun, the recording records `TerminalOrbitBody="Kerbin"` + Mun-frame orbit params → spawn-time body lookup picks Kerbin → applies Mun-frame params against Kerbin → eject trajectory.

Secondary suspect (rewind interaction): the user's repro specifically mentions "after rewind". Rewind restores a save snapshot and may or may not re-finalise the recording. If the rewind restores terminal fields that were set at a pre-SOI-change capture point, the stored body could be Kerbin even though the final orbit segment is Mun. Check `ParsekFlight.FinalizeIndividualRecording` backfill path (`:6917, :6928` call sites for `PopulateTerminalOrbitFromLastSegment`) — that helper at `:7556-7574` is supposed to use the *last* `OrbitSegment`'s body, but the guard `if (!string.IsNullOrEmpty(rec.TerminalOrbitBody)) return;` (`:7559`) skips the correction when the wrong-body field is already populated from the `?? "Kerbin"` fallback.

Third possibility (orbit-frame coupling): the terminal orbit fields themselves could be in the wrong frame. Verify that `CaptureTerminalOrbit` walks `vessel.orbit` (which changes reference body across SOI) and not a stale cached orbit, and that its captured `orb.referenceBody` is the same body whose name is written at `:7547`. A race where `orb` is the Mun-frame orbit but `orb.referenceBody` is null (not yet assigned after a transition) would produce the mixed-frame record.

**Fix:** three-pronged, do them in order and stop when the repro dies:

1. **Kill the silent `?? "Kerbin"` fallback** at `ParsekFlight.cs:7547`. If `orb.referenceBody?.name` is null, WARN + skip the terminal-orbit capture entirely (leave the terminal-orbit fields empty so `ShouldUseRecordedTerminalOrbitSpawnState` returns false and spawn falls back to the last trajectory point, which DOES carry the correct `bodyName` per sample). Same for the last-segment backfill at `:7569` — segments carry `bodyName` directly so that path is already correct; just audit whether the segment bodyName is ever the pre-SOI body.
2. **Harden the guard in `PopulateTerminalOrbitFromLastSegment`**: the current guard at `:7559` refuses to overwrite an already-populated `TerminalOrbitBody` even if the last orbit segment disagrees with it. Change the guard to also re-run when the populated body does NOT match the last segment's body (indicating the original capture raced with an SOI change).
3. **Add a cross-check test**: xUnit that builds a `Recording` with orbit segments spanning Kerbin → Mun and a terminal-state capture that fires mid-transition (force `orb.referenceBody = null` to reproduce the original race), asserts `TerminalOrbitBody` ends up as `"Mun"` at finalization (either from the hardened capture path or from the segment-backfill).

**Files:** `Source/Parsek/ParsekFlight.cs:7547` (capture fallback), `:7559` (backfill guard), `Source/Parsek/VesselSpawner.cs:499-522` (consumer — may need defensive logging to flag the body mismatch shape when it happens in the wild). Test: new xUnit in `Source/Parsek.Tests/` seeded with a Kerbin-orbit + Mun-encounter segment chain and a terminal-orbit capture at the SOI boundary.

**Scope:** Small-to-medium. One-line fix at the capture site covers the most likely root cause; the backfill guard is a defence-in-depth add; the test is the interesting bit because it has to model the SOI-transition race.

**Dependencies:** none. Independent of all prior entries.

**Status:** TODO. Priority: high — reproducible by any career-mode Mun mission that uses rewind, lands the ghost on an eject trajectory with no mission outcome (vessel disappears into solar orbit), destroys the mission. Write a log-capture repro before shipping any fix.

---

## 474. Ghost audio sometimes plays in a single stereo channel instead of centered when the Watch button snaps the camera to the ghost

**Source:** user playtest report — "when hitting watch button and the camera goes to the ghost, sometimes the sound is only audible in a single speaker instead of stereo."

**Concern:** ghost `AudioSource`s are built with `spatialBlend = 1f` (fully 3D) at `Source/Parsek/GhostVisualBuilder.cs:2410` (one-shot) and `:2426` (engine/RCS loops). In Unity, 3D-spatialised audio pans based on the source position relative to the active `AudioListener` and the listener's orientation. When the watch target is `horizonProxy` (or `cameraPivot`) — resolved in `WatchModeController.GetWatchTarget` (`:1936-1944`) — the camera frames the ghost, but the ghost's `AudioSource` components are attached to individual part GameObjects (engines on stages, one-shot on the root) that sit at whatever world offset the snapshot geometry implies. If the camera orbit resolves to an angle where the ghost root sits roughly in front of the listener but the engine part sits clearly left or right of the listener's forward axis, the engine loop pans almost entirely to one stereo channel.

Stock KSP vessels do not exhibit this because the `AudioListener` follows `InternalCamera` / `FlightCamera` tracking the real vessel's `ReferenceTransform` at a vessel-centric framing angle, which keeps engines roughly centered in the listener's forward cone. The watch-mode retarget (`HandleLoopCameraAction` / `HandleOverlapCameraAction` → `SetTargetTransform(GetWatchTarget(...))`, `WatchModeController.cs:711-740`) swaps the target transform but does not renormalise the listener framing to match where the ghost parts actually are.

Symptom is probabilistic because it depends on (a) which ghost is being watched, (b) how the ghost orients at entry (ghost rotation at entry UT, snapshot root orientation), (c) the camera heading the watch mode lands on (which is either default or the remembered `WatchCameraTransitionState` — see #472). When it lines up unfavourably, the engine loop sits 60°+ off the listener's forward axis and Unity pans it hard to one side.

**Fix:** two non-exclusive options to investigate in this order:

1. **Pin the dominant engine `AudioSource`(s) to the ghost root (or cameraPivot) instead of the engine-part GameObject.** This moves the source to the ghost centroid so the listener framing keeps them centered. Downside: loses positional cues (stage audio separation), which may matter for multi-stage visuals. Easy win for the typical single-engine ghost.
2. **Reduce `spatialBlend` on engine loops** from `1.0f` to something like `0.6f-0.8f` so partial stereo spread remains but extreme panning is dampened. This is the cheapest change and matches what KSP stock does for loud vessel loops (check by inspecting `ModuleEnginesFX` AudioSource config via `ilspycmd` on `Assembly-CSharp.dll` — if stock uses `<1.0`, match it).

Secondary investigations worth running in the fix session:
- Confirm the active `AudioListener` when watching a ghost: it should be on the `FlightCamera` as usual. If something in watch-mode setup leaves a stale listener elsewhere (e.g. an `InternalCamera` listener that survived), the pan would derive from the wrong transform.
- Check `AudioSource.panStereo` is `0` on all ghost sources (`CreateGhostAudioSource` / `BuildOneShotAudioSource` never set it, so default `0` is expected — but assert in a log-capture test to pin it).
- The one-shot source at `BuildOneShotAudioSource` uses `spatialBlend = 1f` even though it's parented to the ghost root; same recommendation applies if the user reports one-shots also panning (e.g. decouple clack only in one speaker).

**Files:** `Source/Parsek/GhostVisualBuilder.cs:2410` / `:2426` (spatialBlend tweak), possibly `:2373` (engine source creation site — check where the source GameObject is parented) and `WatchModeController.cs` if the framing angle turns out to be the primary culprit. Test: in-game test that builds a single-engine ghost, enters watch mode, verifies the audio source's `panStereo` is 0 and checks framing angle between the `AudioListener.transform.forward` and the vector to the engine `AudioSource.transform.position` stays within ~45° for the default watch entry. xUnit cannot observe stereo mixing so the confirmation test has to be in-game.

**Dependencies:** touches the same audio pipeline as #465 (pause handling) and the same retarget path as #472 (camera pitch/heading). Fixing #472 first may indirectly reduce the probability of the unfavourable framing that triggers this symptom.

**Scope:** Small-to-medium. Option 2 (spatialBlend tuning) is a one-line change with an in-game test. Option 1 (reparent engine sources) is larger but structural.

**Status:** TODO. Priority: medium — intermittent, not data-destructive, but jarring enough the user flagged it on first playtest.

---

## 473. Gloops group in the Recordings window should be treated as a permanent root group — no `X` disband button, and pinned to the top of the list

**Source:** user playtest request.

**Concern:** the Gloops group is created by `RecordingStore.CommitGloopsRecording` at `Source/Parsek/RecordingStore.cs:394-409` and uses the constant `GloopsGroupName = "Gloops - Ghosts Only"` (`:63`). In `UI/RecordingsTableUI.cs:1725`, the disband-eligibility gate reads `bool canDisbandGroup = !RecordingStore.IsAutoGeneratedTreeGroup(groupName);` — so auto-generated tree groups get a single `G` button, but the Gloops group falls through to `DrawBodyCenteredTwoButtons("G", "X", …)` at `:1734`, exposing an `X` that invokes `ShowDisbandGroupConfirmation`. Disbanding the Gloops group would leave new Gloops commits either re-creating it on the next commit (`:408-409` re-adds the name when missing) or reverting to standalone, and there is no user story for disbanding a system-owned group.

Separately, root-group ordering is decided by `GetGroupSortKey` + the column's sort predicate in `RecordingsTableUI.cs:1077-1079`. The Gloops group ends up wherever its sort key lands among the user's trees/chains, which is inconsistent frame-to-frame as the user sorts by different columns.

**Fix:** two adjacent changes in `RecordingsTableUI.cs`:

1. **Hide the X for Gloops.** The cleanest option is to introduce a `RecordingStore.IsPermanentGroup(groupName)` predicate that returns true for both auto-generated tree groups and the Gloops group, then use that predicate at `:1725` instead of `IsAutoGeneratedTreeGroup`. Keeps the semantic ("this group is system-owned; do not offer disband") local to one place. Update `ShowDisbandGroupConfirmation` callers to not even receive a click for permanent groups (the `X` is simply absent, so this is already the case if the gate is fixed).
2. **Pin to top.** After the `rootItems` list is built (`:1055-…`), re-order so any item whose group name equals `GloopsGroupName` is moved to index 0 before the sort-column comparator is applied. Equivalent: inject a sentinel sort-key (e.g. always compare-first regardless of column) inside `GetGroupSortKey` when `groupName == GloopsGroupName`. Either works; the second is less code but less visible. Preserve the "top" position across all sort columns (date, status, duration, etc.) — Gloops sits above every other root even when the user sorts descending.

Edge case to confirm during fix: the `LegacyGloopsGroupName = "Gloops Flight Recordings - Ghosts Only"` rename path at `RecordingStore.cs:71` — make sure the rename happens on load before any UI sort/permanent-group check, so a legacy save does not briefly show a second "permanent" group.

**Files:** `Source/Parsek/UI/RecordingsTableUI.cs` (`:1725`, `:1055-1079`), `Source/Parsek/RecordingStore.cs` (new `IsPermanentGroup` helper next to `IsAutoGeneratedTreeGroup` at `:615`). Test: xUnit building a `rootGrps` list containing user groups + the Gloops group under every supported sort column, asserts Gloops is always the first element; separate test on `IsPermanentGroup` returns true for both Gloops and auto-tree groups.

**Scope:** Small. ~20 lines across two files + two tests.

**Dependencies:** none.

**Status:** TODO. Priority: low-medium — UI polish, no data-correctness impact. Batch with #471 (Gloops loop-default fix) since both touch the Gloops commit/display path.

---

## 472. Watch-mode camera pitch/heading jumps when playback hands off to the next segment within a recording tree (e.g. flying → landed)

**Source:** user playtest report — "when watching a recording, maintain the camera watch angle exactly the same when transitioning to another recording segment (right now it moves when vessel is going from flying to landed for example)."

**Concern:** inside a single tree (chain/branch) the active playback ghost changes at each segment boundary (flying recording ends, landed recording's ghost becomes the new camera target). `WatchModeController` has a `RetargetToNewGhost` handler (`Source/Parsek/WatchModeController.cs:711-721` for single-ghost and `:731-740` for overlap) that swaps the FlightCamera target transform to the new ghost's pivot. The swap is implemented via `FlightCamera.SetTargetTransform(GetWatchTarget(evt.GhostPivot))` — which snaps the camera to the new target but does NOT re-apply the remembered pitch/heading. Even though `WatchCameraTransitionState` (`:25`) captures `Pitch`/`Heading` in degrees and `RememberWatchCameraState` persists it (`:1012-1057`) for enter/exit transitions, the per-frame segment retarget skips that restoration path. Net effect: the camera yanks to whatever pitch/heading the new ghost's default orientation implies.

The existing loop-cycle-boundary code path (`CameraActionType.RetargetToNewGhost` inside `HandleLoopCameraAction`) has the same shape — if the bug reproduces at loop boundaries too, the fix covers both. Confirm during fix.

**Fix:** in both `RetargetToNewGhost` branches (`WatchModeController.cs:711-721`, `:731-740`), before the `SetTargetTransform` call, capture the current `flightCamera.camPitch` / `camHdg` (radians), then after the retarget re-apply them via the same `ApplyCameraState`-style helper used on watch-mode entry (`:486-525` writes pitch/heading back to the camera). The `WatchCameraTransitionState` struct already carries both fields in the right unit (degrees) — the helper path is ready; it just needs to fire on segment-boundary retargets too, not only on enter/exit.

Edge cases to cover in the test matrix:
- `HorizonLocked` mode (default on entry) — pitch/heading are relative to the horizon and must survive the target swap
- `Free` mode — same requirement, but the relative frame is the ghost's local frame
- Overlap retarget vs non-overlap — both code paths at `:711` and `:731`
- Loop cycle boundary — verify same issue/fix

**Files:** `Source/Parsek/WatchModeController.cs` (retarget sites + helper plumbing). Test: in-game test (xUnit cannot observe `FlightCamera`) that enters watch mode on a multi-segment tree recording, waits for the segment boundary to trigger retarget, asserts `flightCamera.camPitch` / `camHdg` remain within a small epsilon of their pre-retarget values.

**Scope:** Small — pitch/heading capture-and-reapply around each retarget call. Main complexity is the test harness driving a real segment boundary; if too coupled, add a test seam on `HandleLoopCameraAction` / `HandleOverlapCameraAction` to drive the `RetargetToNewGhost` branch directly from a synthetic `CameraActionEvent`.

**Dependencies:** none.

**Status:** TODO. Priority: medium — user-visible camera jerk every time playback crosses a segment/terminal state boundary.

---

## 471. Gloops recordings should not loop by default; commit path should set `LoopPlayback=false` and `LoopIntervalSeconds=0` (auto)

**Source:** user request — "gloops recordings should no longer be looped by default and their loop period should be set to auto when they are created."

**Concern:** `ParsekFlight.CommitGloopsRecorderData` (`Source/Parsek/ParsekFlight.cs:7757`) unconditionally sets `rec.IsGhostOnly = true; rec.LoopPlayback = true;` at `:7776-7777`. The file's doc comment at `:7720` even states "with looping enabled by default" — so the current behaviour is by design, but the design is wrong for the user's workflow: Gloops recordings are for one-off hand-captured moments, not automatic loops, and enabling looping by default spawns unwanted repeating ghosts until the player manually toggles the L button off. Loop interval is not explicitly set, so it defaults to `0` (the "auto" sentinel the playback path maps to `ParsekSettings.autoLoopIntervalSeconds`, see `ParsekFlight.cs:450` and `ParsekKSC.cs:842`).

**Fix:** change `ParsekFlight.cs:7777` from `rec.LoopPlayback = true;` to `rec.LoopPlayback = false;`. Add `rec.LoopIntervalSeconds = 0;` explicitly on the line below so the "auto" intent is visible in code (it is already the default, but a new reader should not have to know that `0` is the auto sentinel). Update the doc comment at `:7719-7720` to reflect the new behaviour (`committed as ghost-only; looping off by default, interval set to auto (0)`). Also update the class-level comment at `UI/GloopsRecorderUI.cs:9` which currently says "auto-committed to the Gloops group with looping enabled by default".

No schema change needed — existing recordings with `LoopPlayback=true` are not touched. Test: xUnit driving `CommitGloopsRecorderData` with a minimal `FlightRecorder`, asserts the committed recording has `LoopPlayback==false` and `LoopIntervalSeconds==0`.

**Files:** `Source/Parsek/ParsekFlight.cs:7776-7777` (setter + doc), `Source/Parsek/UI/GloopsRecorderUI.cs:9` (doc). Test: `Source/Parsek.Tests/` (new or existing Gloops test file).

**Scope:** Trivial. Two-line change + two comment updates + one test.

**Dependencies:** none.

**Status:** TODO. Priority: medium — small user-experience polish, no data-correctness risk.

---

## 470. `Funds` subsystem logs `FundsSpending: -0, source=Other` hundreds of times per session (134 lines in one 15-minute career run)

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log`. Top-of-list pattern in the deduplicated WARN/VERBOSE counts:

```
134 [Parsek][VERBOSE][Funds] FundsSpending: -0, source=Other, affordable=true, runningBalance=N, recordingId=(none)
```

**Concern:** every `RecalculateAndPatch` sweep (33 of them in this session) fans out to the per-module replay, and each module emits a `FundsSpending: -0` line for zero-delta entries inside the "Other" source bucket. Zero-delta spendings convey nothing a reader would ever act on, and at 4 per recalc × 33 recalcs = 132 lines, they bury the real entries. Adjacent modules already early-return on zero-delta (see the verbose threshold filters in `GameStateRecorder.cs`), so this one is the odd one out.

**Fix:** in the `Funds` subsystem's spending emit path, skip the log entirely when `Math.Abs(delta) < 0.5` (or exact zero — the threshold just needs to exclude `-0` / `+0`). Keep the event itself if any downstream consumer cares about zero-delta entries; only suppress the VERBOSE log. Grep for the `FundsSpending: ` format string in `Source/Parsek/GameActions/` to locate the emit site (most likely `FundsModule.cs` or similar).

**Files:** likely `Source/Parsek/GameActions/FundsModule.cs` (or whichever `.cs` owns the `[Funds]` subsystem tag). Test: log-assertion xUnit that submits a zero-delta spending, asserts no VERBOSE line hits the sink.

**Scope:** Trivial. One-line guard + one test.

**Dependencies:** none.

**Status:** TODO. Priority: low — pure log-hygiene. Bundle with any touch of the Funds module.

---

## 469. Post-walk reconciliation fails to find same-UT FundsChanged events that are demonstrably in the store — "no matching event keyed 'Progression'" warns fire on events that exist

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log`. 176 WARNs of this shape from one career session:

```
10399: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, funds): MilestoneAchievement id=FirstLaunch expected=800.0 but no matching FundsChanged event keyed 'Progression' within 0.1s of ut=57.2 -- missing earning channel or stale event?
```

**Concern:** the event the reconciliation cannot find is visibly in the store — same session records it explicitly at line 9425: `[GameStateStore] AddEvent: FundsChanged key='Progression' epoch=0 ut=57.2 (total=7)` (and line 9426 `FundsChanged +800 (Progression) → 23395`). So the event was captured with exactly the UT and key the reconcile filters on, yet `CompareLeg` (`Source/Parsek/GameActions/LedgerOrchestrator.cs:4248-4293`) reports `observedCount == 0`. Same-UT ScienceChanged `'ScienceTransmission'` at ut=204.4 fires in a separate subsystem (10260 `Suppressed ScienceChanged event (None) during timeline replay`) — some science WARNs are caused by replay-suppression, but the funds ones are not: they fire against events already committed to the store from pre-replay user play.

`Post-walk reconcile: actions=15, matches=0, mismatches(funds/rep/sci)=9/2/6, cutoffUT=null` at 10416 — 0 of 15 actions matched on this pass. Within a single session, identical actions go from "6 matches" → "0 matches" across recalc invocations 7ms apart, which is what initially suggested the `events` list passed into `CompareLeg` differs between calls.

**Fix:** trace what `events` list `ReconcilePostWalkActions` (around `LedgerOrchestrator.cs:4230`) actually hands to `CompareLeg`. Hypotheses in priority order:

1. **Pre-replay snapshot vs live store.** The post-walk hook may be comparing the post-replay in-memory action list against a pre-replay events snapshot (or vice versa). Verify the `events` parameter at the call site is `GameStateStore.Events` (or equivalent live view), not a captured `IReadOnlyList` taken before the walk began.
2. **Epoch filter.** `CompareLeg` does not check `e.epoch`. If the store is walked with epoch > 0 while the `FundsChanged` events were stored at `epoch=0`, same-UT same-key events may be silently skipped by an outer filter. Confirm by dumping the events the reconcile sees (add a one-shot VERBOSE just before the inner loop).
3. **`action.UT` source.** If `action.UT` drifts slightly from the event ut (e.g. the action is tagged with the recalc timestamp instead of the milestone emission ut), `Math.Abs(e.ut - action.UT) > 0.1` can fail. Unlikely here since WARN `ut` matches event `ut` to one decimal, but worth confirming on the full-precision values.

Once root cause is known, fix is local to `CompareLeg` or its call site. Test: xUnit harness that seeds `GameStateStore` with `FundsChanged key='Progression' ut=57.2 +800`, creates a `MilestoneAchievement action.UT=57.2`, invokes `ReconcilePostWalkActions`, asserts no WARN fires and the corresponding `Post-walk match: ...` VERBOSE does.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`CompareLeg`, `ReconcilePostWalkActions`, caller). Test: `Source/Parsek.Tests/` new file.

**Scope:** Small-to-medium. Investigation first, fix likely one-line once the event-source mismatch is identified.

**Dependencies:** none. Supersedes / subsumes #462 (the previous observation was double-count; this one is zero-count — same reconciliation code, different symptoms; root-cause fix here may close #462 simultaneously — re-test before closing either).

**Status:** TODO. Priority: high — drowns the log in false WARNs every session, erodes trust in the reconciliation subsystem. Related-but-distinct from #466 (suspicious-drawdown patch) and #467 (rep threshold); fix them together if the same investigation surfaces the source-list bug.

---

## 468. `ScienceEarning` reconcile anchor UT is vessel-recovery-time, but `ScienceChanged 'ScienceTransmission'` events are emitted at transmission-time earlier in the flight — the 0.1s window can never match

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log:10410-10415`.

```
[WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, sci): ScienceEarning id=mysteryGoo@KerbinSrfLandedLaunchPad expected=11.0 but no matching ScienceChanged event keyed 'ScienceTransmission' within 0.1s of ut=204.4
```

Paired with the actual capture sequence earlier in the same session:

```
9272: [GameStateRecorder] Emit: ScienceChanged key='ScienceTransmission' at ut=39.8
9273: [GameStateStore] AddEvent: ScienceChanged key='ScienceTransmission' ut=39.8
9488: [GameStateStore] AddEvent: ScienceChanged key='ScienceTransmission' ut=66.3
```

**Concern:** the `ScienceEarning` ledger actions created from a committed recording are timestamped to the vessel-recovery UT (here 204.4 — the recovery event), but the KSP `ScienceChanged` events fire whenever stock transmits/completes a science subject, which for an in-flight launch is typically 20-100 seconds into the flight, long before recovery. `CompareLeg`'s `Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds (0.1s)` gate then rejects the only events that could possibly match, and every recovered science subject produces a post-walk WARN.

Independent of #469 (where the event IS at the right UT and the reconcile still fails): this is the case where the event is at the wrong UT *for this particular leg*. Both show up in the same session; fixing one does not fix the other.

**Fix:** two options, pick based on the semantic of `ScienceEarning`:

1. **Anchor the action to transmission UT**, not recovery UT. If the ledger action is meant to reconcile with the per-subject transmission event, the action's UT should track the event's UT. This may require `ScienceModule.cs` (the emit site) to carry the per-subject transmission timestamp forward into the action instead of collapsing to recovery UT.
2. **Broaden the reconcile window for `ScienceEarning`** to cover the entire recording's UT span (e.g. accept any matching ScienceChanged event between recording start and recovery). Keep the 0.1s window for `MilestoneAchievement` where the instantaneous match is correct.

Option 1 is cleaner but touches the emit path; option 2 is localised to `CompareLeg` / `ReconcilePostWalkActions` in `LedgerOrchestrator.cs`.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (+ possibly `ScienceModule.cs`). Test: xUnit seeding a ScienceEarning at recovery UT and a ScienceChanged at an earlier UT within the same recording span, asserts no post-walk WARN.

**Scope:** Small-to-medium depending on option. Option 2 is ~20 lines; option 1 requires an action-schema nudge.

**Dependencies:** surface with #469 during the same investigation — root-cause signal will tell which option is right.

**Status:** TODO. Priority: medium. Currently produces 126+ WARNs per launch session.

---

## ~~467. `ReputationChanged` threshold filter rejects stock +1 rep awards — `Math.Abs(delta) < 1.0f` drops `0.9999995` rewards, breaking all records-milestone rep reconciliation~~

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log`.

```
9473: Added 0.9999995 (1) reputation: 'Progression'.
9476: [Parsek][VERBOSE][GameStateRecorder] Ignored ReputationChanged delta=+1.0 below threshold=1.0
```

**Concern:** stock KSP awards `0.9999995` reputation for Records* milestones (the `(1)` in the log is the rounded display value; the actual delta is `~1 − 5e-7`). `GameStateRecorder.cs:910` drops the event with `if (Math.Abs(delta) < ReputationThreshold)` where `ReputationThreshold = 1.0f` (`:222`). `0.9999995 < 1.0` is true, so the event never makes it into the store. The post-walk reconcile for the paired `MilestoneAchievement` rep leg then reports "no matching ReputationChanged event keyed 'Progression' within 0.1s" — in this session that produced all 44 rep-mismatch WARNs (`RecordsSpeed`, `RecordsAltitude`, `RecordsDistance` each firing two per recalc pass).

**Update (2026-04-19):** Fixed in the `#467` worktree. `OnReputationChanged` now keeps a small `0.001f` epsilon under the `1.0f` threshold so stock-rounded `0.9999995` awards still survive after cumulative-float subtraction (`old + reward - old` can land slightly below `0.9999995`, e.g. `0.99999x`). Added regression coverage in `Source/Parsek.Tests/GameStateRecorderResourceThresholdTests.cs` for both raw `+/-0.9999995`, the cumulative-float subtraction shape, and clear sub-threshold control cases.

**Original fix sketch:** one-line change in `Source/Parsek/GameStateRecorder.cs:910`:

```csharp
// Before:  if (Math.Abs(delta) < ReputationThreshold)
// After:   if (Math.Abs(delta) < ReputationThreshold - 0.001f)
```

Or lower `ReputationThreshold` to `0.5f` (any value strictly below stock's 0.9999995 — `0.5f` leaves headroom for other rounding cases while still filtering sub-integer noise). Pick the second form if you want one named constant doing the semantic work; the epsilon form is narrower but preserves the visible `1.0` threshold.

Similar care needed for `FundsThreshold = 100.0` and `ScienceThreshold = 1.0` — confirm stock never rewards *exactly* threshold values; if it does, apply the same epsilon trim.

**Files:** `Source/Parsek/GameStateRecorder.cs:910` (rep), possibly `:821` (funds) and the ScienceChanged analogue. Test: xUnit calling the onReputationChanged handler with delta `0.9999995f`, asserts the event is captured in the store (not dropped).

**Scope:** Trivial. One-line fix + one test + verify the twin thresholds.

**Dependencies:** none. Fixes the rep-mismatch tail of #469 specifically, though the underlying #469 investigation may also surface non-rep mismatches unrelated to this threshold.

**Status:** ~~TODO~~ Fixed for v0.8.3. Priority was high — shipped as a small recorder-side threshold hardening plus targeted unit coverage.

---

## 466. `RecalculateAndPatch` runs mid-flight with an incomplete ledger, patches funds DOWN to the pre-milestone target and destroys in-progress earnings

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log:9993`.

```
9993: [WARN][KspStatePatcher] PatchFunds: suspicious drawdown delta=-36800.0 from current=57795.0 (>10% of pool, target=20995.0) — earning channel may be missing. HasSeed=True
9995: [INFO][KspStatePatcher] PatchFunds: 57795.0 -> 20995.0 (delta=-36800.0, target=20995.0)
```

Two more occurrences at `:12839` (-9300) and `:13581` (-41546.7) within the same 10-minute session, all with identical shape: the live KSP funds are higher than the ledger's computed target because stock KSP has credited milestones the Parsek ledger does not yet know about.

**Concern:** `KspStatePatcher.PatchFunds` logs the `suspicious drawdown` WARN (`KspStatePatcher.cs:160-167`) but deliberately still applies the drawdown — the comment at `:156-159` says "log-only (never aborts the patch) — but a >10% drop alongside a small pool (>1000F) is the shape of missing-earnings bugs". In this session that design is **destructive**: the recalc was triggered mid-launch by an OnLoad at 00:35:27 (just after revert subscribe on `:9957`), at which point the `r0` recording's tree had not yet committed (`Committed tree 'r0'` is at `:10083`, ~4s later). `actionsTotal=4` at that recalc — rollout + initial seed only, no milestones. So the ledger's target of `25000 - 4005 = 20995` ignores the `+800` `+4800` `+4800` `+4800` `+4800` milestone credits stock had already awarded, and `Funding.Instance.AddFunds(delta=-36800, TransactionReasons.None)` silently deletes 36,800F of the player's money.

A subsequent recalc at `:10134` with `actionsTotal=12` (post-commit) computes the full target, but by then the funds have been re-patched several times and the reconcile is in the broken state described in #469. The three drawdowns are not three separate events — they are three recalc passes, each landing before a different tree's commit.

**User-visible:** player earns milestones in flight (visible in game UI), then on scene transition / quickload the funds snap back to a lower value. This is the "ledger/resource recalculation did not really work correctly" the reporter is describing.

**Fix:** two layered fixes, both probably needed:

1. **Gate `RecalculateAndPatch` on "no mid-flight uncommitted tree"**. If a tree is actively accumulating but not yet committed, defer `PatchFunds` / `PatchScience` / `PatchReputation` until commit. Policy: recalc still walks the known-committed ledger (updates in-memory balances), but the KSP patch step is skipped with a VERBOSE log `Deferred patch: uncommitted tree active`. Alternative: include provisional/pending-tree actions in the walk so the target reflects the in-flight work.
2. **Harden `IsSuspiciousDrawdown` into an abort, not just a log**. When the drawdown exceeds the threshold AND the ledger has a known uncommitted tree (or provisional FundsChanged events within the last recalc window), refuse to patch — no `AddFunds(-36800)` call, just a WARN. This keeps the design flexibility of patching down for legitimate revert/rewind paths (where a stale ledger is *supposed* to reset), but blocks the destructive case.

Fix 1 is preferred — prevention over detection. Fix 2 is a safety net for paths that can't be gated cleanly (OnLoad from save, cross-session resume).

Add a cross-reference: `#439`, `#440`, `#448` and the already-archived post-#307 reconciliation work all touched adjacent logic. Re-read `done/todo-and-known-bugs-v3.md` entries for those before writing the fix — the reason several drawdown WARNs were *kept* log-only was a prior bug where aborting the patch masked a different class of problem. Don't regress that.

**Files:** `Source/Parsek/GameActions/KspStatePatcher.cs` (patch gate), `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`RecalculateAndPatch` entry check), `Source/Parsek/FlightRecorder.cs` or `ParsekFlight.cs` (uncommitted-tree predicate). Tests: xUnit for the gate (seed a mid-flight state, trigger recalc, assert no `Funding.AddFunds` call); integration-style log-assertion test covering the revert-mid-flight path.

**Scope:** Medium. Touches patch gating + recalc entry + a new predicate. Several test cases to cover revert/rewind/OnLoad/quickload interactions.

**Dependencies:** read the #307/#439/#440 history first. Fix should land before / alongside #469 since the reconcile warnings mostly disappear once the patch gate prevents the stale-target state from ever being written.

**Status:** TODO. Priority: **critical** — silently destroys earned player funds. Reproducible in ~3 minutes in career mode with any launch that completes milestones, as the collected log shows three occurrences in one 10-minute session.

---

## 465. Ghost engine/RCS audio keeps playing while the KSP pause menu is open outside the flight scene

**Source:** user playtest report. "When paused (game menu open) in KSC view and probably other views, the sound from the rocket ghost is still audible."

**Concern:** ghost `AudioSource` components (engine loops, RCS, ambient clips) don't respond to KSP's global pause like stock vessels' audio does. In the flight scene this is already handled: `ParsekFlight.cs:657` subscribes to `GameEvents.onGamePause`/`onGameUnpause` and the handlers at `ParsekFlight.cs:4302-4315` delegate to `engine.PauseAllGhostAudio()` / `engine.UnpauseAllGhostAudio()`, which loop over active ghost states and call `AudioSource.Pause()`/`UnPause()` (see `GhostPlaybackLogic.cs:2358` for the helpers). No equivalent subscription exists in `ParsekKSC.cs` — nothing in that file touches `onGamePause` or `PauseAllGhostAudio`. Same likely true for tracking station / other non-flight scenes that run ghost playback via the KSC code path. Result: ESC menu in KSC silences stock audio but ghost engine loops keep roaring until the menu closes.

**Fix:** mirror the flight handler in `ParsekKSC.cs` — subscribe to `GameEvents.onGamePause` / `onGameUnpause` in `Awake`/`Start` (matching the `ParsekFlight.cs:657/1124` add/remove pair), unsubscribe in `OnDestroy`, and forward to the same `engine.PauseAllGhostAudio()` / `engine.UnpauseAllGhostAudio()` helpers the flight scene uses. The helpers already iterate `engine.ghostStates` so they work regardless of which scene is active. Verify the same fires from the tracking station scene if Parsek runs ghost audio there (grep for any `AudioSource` activation outside the flight/KSC paths). Add a log-assertion unit test that drives the pause handler and asserts `AudioSource.Pause` was called on the engine's state set (covered via the existing `GhostPlaybackEngine` test seams used in the pause/unpause flight tests).

**Files:** `Source/Parsek/ParsekKSC.cs` (new subscription + handlers); possibly `Source/Parsek/Patches/GhostTrackingStation.cs` (tracking-station ghosts, if they carry `AudioSource`s at all); test in `Source/Parsek.Tests/`.

**Scope:** Small. ~10 lines in ParsekKSC mirroring the flight code, one test.

**Dependencies:** none — `PauseAllGhostAudio` / `UnpauseAllGhostAudio` already exist and are scene-agnostic.

**Status:** TODO. Priority: medium — audible UX bug, trivial to reproduce (ESC at KSC while any loop ghost is playing).

---

## 464. Timeline Details tab duplicates milestone / strategy entries — gray `GameStateEvent` line shadows the green `GameAction` reward line

**Source:** user playtest report. "From the Timeline Details tab list, remove the 'Milestone … achieved' messages and leave only the green ones, they're kind of duplicates; same for Strategy: activate / deactivate, duplicates."

**Concern:** for each milestone or strategy lifecycle event, the Timeline Details list renders two rows:

- the green `GameAction` row — rendered by `TimelineEntryDisplay.cs:296-308` for `GameActionType.MilestoneAchievement` and carries the user-meaningful data (milestone name + `+960 funds` / `+0.5 rep`). The strategy-activation variant is rendered in the same file for `GameActionType.StrategyActivate` / `StrategyDeactivate` (setup cost legs).
- the gray `GameStateEvent` row — rendered by `GameStateEvent.GetDisplayDescription` at `GameStateEvent.cs:398-399` (`"{key}" achieved`) and `:405-413` (`"{title}" activated` / `"{title}" deactivated`). These are emitted by the `GameStateRecorder` path for audit completeness but add no information beyond what the green GameAction row already shows.

Net effect: every milestone / strategy event shows up twice in the Timeline Details tab — first as the green reward summary, then as the plain gray confirmation. Players read this as redundant.

**Fix:** filter the duplicate `GameStateEventType.MilestoneAchieved`, `StrategyActivated`, and `StrategyDeactivated` rows out of the Timeline Details rendering when a matching green `GameActionType.MilestoneAchievement` / `StrategyActivate` / `StrategyDeactivate` already exists for the same UT + key. Two equally valid places to apply the filter:

1. In the timeline-details collator (wherever `GameStateEvent`s are merged into the per-recording display list — likely `ParsekUI` / `RecordingsTableUI` or a shared `TimelineBuilder` helper). Preferred — drops them at assembly time so the display path stays simple.
2. In `TimelineEntryDisplay` via a post-hoc "if a preceding entry for this UT already carries the milestone id / strategy title, skip this one" dedup. Works but leaks the dedup logic into the display layer.

Keep the gray rows emitted at the data layer — they're still useful for the raw event log / debugging. Only filter at the Timeline Details renderer level. Add a setting/toggle only if users actually want the duplicates back (unlikely given the report).

**Files:** `Source/Parsek/Timeline/TimelineEntryDisplay.cs` (or upstream of it — grep for whatever builds the Details list); `Source/Parsek/GameStateEvent.cs` only if the "achieved"/"activated" format strings themselves need to change (they don't for this bug — the fix is filtering, not rewording). Test: xUnit building a timeline with both a MilestoneAchievement GameAction and a matching MilestoneAchieved GameStateEvent at the same UT, asserts the rendered list contains exactly one row for that milestone (the green one).

**Scope:** Small. Single collator/filter site + one test. No schema or recording-format change.

**Dependencies:** none.

**Status:** TODO. Priority: low-medium — UI polish, no functional impact. Batch with any next UI pass.

---

## 463. Deferred-spawn flush skips FlagEvents — flags planted mid-recording never materialise when warp carries the active vessel past a non-watched recording's end

**Source:** user playtest `logs/2026-04-19_0014_investigate/KSP.log`. Reproducer:

1. Record an "Untitled Space Craft" flight; EVA Bob Kerman and plant a flag (`[Flight] Flag planted: 'a' by 'Bob Kerman'` at UT 17126).
2. Watch an unrelated recording (Learstar A1) and time-warp through the flag's UT.
3. At warp-end the capsule (#290) and kerbal (#291) materialise as real vessels via the deferred-spawn queue — but the flag 'a' does NOT spawn.
4. Stop watching Learstar; watch the actual Bob Kerman recording (#291) instead. Its ghost runs through UT 17126 normally, `[GhostVisual] Spawned flag vessel: 'a' by 'Bob Kerman'` fires, and the flag appears.

Specific log lines in the snapshot (all from the same session):

- `00:10:40.581 [Policy] Deferred spawn during warp: #291 "Bob Kerman"` — warp active, spawn queued.
- `00:10:57.316 [Policy] Deferred spawn executing: #291 "Bob Kerman" id=d631f348fde24b6f8fbeb00228d8e057` — warp ended, queue flushed; `host.SpawnVesselOrChainTipFromPolicy(rec, i)` runs and spawns the EVA vessel. Nothing touches `rec.FlagEvents`.
- `00:12:56.614 [GhostVisual] Spawned flag vessel: 'a' by 'Bob Kerman'` — only emitted when the actual Bob Kerman recording is watched (session 2), via the normal `GhostPlaybackLogic.ApplyFlagEvents` cursor path.

**Root cause:** flag vessel spawns are driven by `GhostPlaybackLogic.ApplyFlagEvents` (`Source/Parsek/GhostPlaybackLogic.cs:1892`), which walks `state.flagEventIndex` forward over `rec.FlagEvents` every frame a ghost is in range. Callers are `GhostPlaybackEngine.UpdateNonLoopingPlayback:744`, `ParsekKSC.cs:341/476/528`, and the preview path in `ParsekFlight.cs:8177`. The deferred-spawn-at-warp-end path in `ParsekPlaybackPolicy.ExecuteDeferredSpawns` (≈ `ParsekPlaybackPolicy.cs:143-179`) goes straight from `host.SpawnVesselOrChainTipFromPolicy(rec, i)` to `continue` without ever stepping the flag-event cursor, because the ghost for that recording never entered range (you were watching Learstar). Flags in the recording interval — which are "in the past" by the time deferred spawn runs — are silently dropped.

User-visible symptom: a flag planted during an EVA disappears from the world whenever the player time-warps past its recording while watching anything else. "Capsule and kerbal spawned but the flag didn't" is the exact report shape.

**Fix:** in `ParsekPlaybackPolicy.ExecuteDeferredSpawns`, after a successful `SpawnVesselOrChainTipFromPolicy` call, walk `rec.FlagEvents` and invoke `GhostVisualBuilder.SpawnFlagVessel(evt)` for every event with `evt.ut <= currentUT`, guarded by the existing `GhostPlaybackLogic.FlagExistsAtPosition` dedup. This mirrors the state-less fallback branch inside `ApplyFlagEvents` (`GhostPlaybackLogic.cs:1918-1924`) so no new invariant is added — the dedup helper already handles idempotent replays. Consider extracting a small `GhostPlaybackLogic.SpawnFlagVesselsForRecording(rec, currentUT)` helper so both paths share one implementation. Log a `[Verbose][Policy] Deferred flag flush: #N "rec" spawned K/N flags` summary so the fix is observable in playtest logs.

**Also verify during fix:** earlier in the same session, `00:09:08.031 [Scenario] Stripping future vessel 'a' (pid=1009931614, sit=LANDED) — not in quicksave whitelist` fires from `ParsekScenario.StripFuturePrelaunchVessels`. This is the rewind/quickload strip path (`Source/Parsek/ParsekScenario.cs:1490`). Confirm that flags planted during a committed recording are NOT treated as future-prelaunch vessels on quicksave round-trip — the whitelist-based strip predates flag support, so a fresh look at whether the planted-flag PID should be added to the whitelist (or filtered by type) would close a related observation. If a quickload can strip the flag before the deferred-spawn replay even runs, the main fix above does not cover that path.

**Files:** `Source/Parsek/ParsekPlaybackPolicy.cs` (spawn + flag-flush); likely `Source/Parsek/GhostPlaybackLogic.cs` (new shared helper); possibly `Source/Parsek/ParsekScenario.cs` (strip-whitelist check). Test: xUnit that drives `ExecuteDeferredSpawns` with a `Recording` carrying one `FlagEvent` at `ut=currentUT-1`, asserts `SpawnFlagVessel` is invoked exactly once and the log line fires.

**Scope:** Small-to-medium. Core fix is a 5-10 line loop in one method + one helper + one unit test. Strip-path verification is separate and may be a no-op if flags are already on the whitelist.

**Dependencies:** none (flag event capture + `SpawnFlagVessel` both already work).

**Status:** TODO. Priority: medium-high — functional correctness bug, reproducible every session, no workaround except manually watching every recording end-to-end.

---

## 462. LedgerOrchestrator earnings reconciliation: MilestoneAchievement double-count vs FundsChanged

**Source:** `logs/2026-04-19_0014_investigate/KSP.log` (48 WARN lines across one session). Representative pair:

```
[WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, funds): MilestoneAchievement id=Kerbin/SurfaceEVA expected=960.0, observed=1440.0 across 2 event(s) keyed 'Progression' at ut=17110.6 -- post-walk delta mismatch
[WARN][LedgerOrchestrator] Earnings reconciliation (funds): store delta=13920.0 vs ledger emitted delta=13440.0 — missing earning channel? window=[17076.5,17110.6]
```

**Concern:** post-walk funds reconciliation detects a systematic mismatch between the expected milestone award and the observed FundsChanged events for several stock milestones — `MilestoneAchievement id=` values hitting 1.5× the expected payout across 2 events (so every recalc is double-writing one of them), plus store-vs-ledger window mismatches where the full-window delta diverges by a stable offset. Seen for: `RecordsSpeed` (12×), `RecordsDistance` (12×), `Kerbin/SurfaceEVA` (6+3), `Kerbin/Landing` (6×), `Kerbin/FlagPlant` (6×), `FirstLaunch` (6×). All on the same test-career session at UT≈17110. Because the observed delta is higher than expected, funds accounting for these milestones is likely over-paying — the kind of bug that silently inflates funds over long play sessions and is very hard to spot without the reconciliation WARNs.

**Fix:** investigate `LedgerOrchestrator.RecalculateAndPatch` + `GameActions/KerbalsModule`-style earnings paths for milestone events. Two plausible causes: (1) milestone event being emitted twice into the ledger (once from the live progress event, once during recalc replay); (2) `Progression` channel key matching two distinct events in the reconciliation window (ambient FundsChanged from another source collapsed in). Add a test generator that reproduces the double-count for `RecordsSpeed` in `Source/Parsek.Tests/` (the milestone most obviously reproducible — it fires on every takeoff/landing in the test save). Cross-reference with the existing PR #307 follow-ups in `done/todo-and-known-bugs-v3.md` — that bundle already touched the `Progression` dedup key.

**Files:** likely `Source/Parsek/GameActions/LedgerOrchestrator.cs`, the earnings emit path for MilestoneAchievement, and whichever module owns MilestoneStore→FundsChanged conversion. Log snapshot saved under `logs/2026-04-19_0014_investigate/` for reproduction context.

**Scope:** Medium. Funds reconciliation is safety-critical (double-counted earnings invalidate career economies), but the WARN mechanism is already catching it — so the fix is localised to one emit path, not a schema redesign.

**Dependencies:** none.

**Status:** TODO. Priority: medium-to-high — real data correctness bug with no user-facing symptom today except the WARNs, but compounds over long saves.

**Update (superseded by #477):** re-investigation in `logs/2026-04-19_0117_thorough-check/` showed the 2× / 3× / spurious-sci pattern is general across every milestone, not specific to `Kerbin/SurfaceEVA`. The root cause is duplicate `MilestoneAchievement` action emissions (not a double-count at the event-store side). See #477 for the general case — fixing #477 is expected to resolve #462 simultaneously; close this entry only after verifying the reconcile WARN disappears for `Kerbin/SurfaceEVA` specifically.

---

## 461. Pin the #406 reuse post-frame visibility invariant with an in-game test

**Source:** clean-context Opus review of PR #394 (#406 ghost GameObject reuse across loop-cycle boundaries), finding #4.

**Concern:** the reuse orchestrator (`GhostPlaybackEngine.ReusePrimaryGhostAcrossCycle`) exits with `state.deferVisibilityUntilPlaybackSync == true` and `state.ghost.activeSelf == false` (set by `PrimeLoadedGhostForPlaybackUT.SetActive(false)`). Control then falls through to `UpdateLoopingPlayback:1161-1166`, where `ActivateGhostVisualsIfNeeded` clears both on the same frame before any render pass. A post-investigation trace confirmed this is invariant-equivalent to the pre-#406 destroy+spawn path, so no visual regression exists today — but NO test pins this control-flow ordering. A future refactor that adds an early `return` between `:1068` (the reuse call) and `:1166` (the activation) would silently hide the ghost for a frame on every cycle boundary.

**Fix:** new in-game test `Bug406_ReuseClearsDeferVisOnSameFrame` (alongside the existing `Bug406_ReusePrimaryGhostAcrossCycle_PreservesGhostIdentity` in `Source/Parsek/InGameTests/RuntimeTests.cs`) that drives a full `UpdateLoopingPlayback` cycle-boundary pass (using a real committed recording from the test fixture, or a minimal IPlaybackTrajectory + positioner stub with engine-level seams) and asserts on the post-frame state: `state.deferVisibilityUntilPlaybackSync == false` AND `state.ghost.activeSelf == true` (in the happy-path, not-zone-hidden case). A second variant asserts the `hiddenByZone` branch keeps the ghost inactive as designed. xUnit cannot observe `GameObject.activeSelf`; must be in-game.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs` (new test). Possibly a test seam on `GhostPlaybackEngine` if the full `UpdateLoopingPlayback` path is too coupled to the real FrameContext plumbing — prefer driving the public entry point to avoid white-box coupling.

**Scope:** Small (one in-game test + possibly a thin seam). Pure regression-test work, no production code change.

**Dependencies:** #394 (#406 follow-up) merged.

**Status:** TODO. Priority: low. User-visible impact today is none (invariant-equivalent to pre-#406 behaviour); the test is a guard against future refactors.

---

## 450. Per-spawn time budgeting / coroutine split — #414 follow-up for bimodal single-spawn cost

**Source:** smoke-test bundle `logs/2026-04-18_0221_v0.8.2-smoke/KSP.log:11489`. One-shot #414 breakdown line (first exceeded frame in the session):

```
Playback budget breakdown (one-shot, first exceeded frame):
total=40.1ms mainLoop=11.34ms
spawn=28.11ms (built=1 throttled=0 max=28.11ms)
destroy=0.00ms explosionCleanup=0.00ms deferredCreated=0.24ms (1 evts)
deferredCompleted=0.00ms observabilityCapture=0.43ms
trajectories=1 ghosts=0 warp=1x
```

**Concern:** #414's fix caps ghost spawns per frame at 2 via `GhostPlaybackEngine.MaxSpawnsPerFrame`, but this frame built exactly 1 ghost and that single spawn cost 28.11 ms — throttled=0, max=28.11 ms. This is the **bimodal cost distribution** #414 explicitly flagged as requiring a follow-up: "if max > ~10 ms we have a bimodal cost distribution that a count cap alone cannot cover, in which case the follow-up is per-spawn time budgeting or a coroutine split" (see #414 **Fix** section). The smoke test confirms the bimodal case is real on this save.

Breakdown of the exceeded frame: 70% of the budget (28.11 / 40.1 ms) lived inside a single `SpawnGhost` invocation. Candidates for the dominant per-spawn cost:
- `GhostVisualBuilder.BuildGhostVisuals` — part instantiation + engine FX size-boost pass (PR #316) + reentry material pre-warm.
- `PartLoader.getPartInfoByName` resolution for every unique part name in the ghost snapshot (cold PartLoader cache on first spawn of a given vessel type).
- Ghost rigidbody freeze + collider disable walk (`GhostVisualBuilder.ConfigureGhostPart`).

`mainLoop=11.34 ms` with `trajectories=1 ghosts=0` is also on the high side (expected ≤1 ms per trajectory on an established session), worth subtracting from the spawn cost attribution when a follow-up breakdown lands.

**Phase A (shipped):** diagnostic first. `PlaybackBudgetPhases` now carries an aggregate-and-heaviest-spawn breakdown of every `BuildGhostVisualsWithMetrics` call across four sub-phases (snapshot resolve, timeline-from-snapshot, dictionaries, reentry FX) plus a residual "other" bucket so `sum + other = spawnMax` reconciles. See `docs/dev/plan-450-build-breakdown.md`.

**Phase B branch decision — data from the 2026-04-18 playtest:**

```
heaviestSpawn[type=recording-start-snapshot
              snapshot=0.00ms timeline=15.90ms dicts=1.28ms reentry=6.94ms
              other=0.08ms total=24.20ms]
```

Timeline dominates (65.7 %) and reentry is a significant secondary contributor (28.7 %). Both B2 and B3 apply; B3 ships first (smaller blast radius), then B2 takes on the remaining `timeline` cost.

**Phase B3 (shipped):** lazy reentry FX pre-warm. Defers `GhostVisualBuilder.TryBuildReentryFx` from spawn time to the first frame the ghost is actually inside a body's atmosphere. `MaxLazyReentryBuildsPerFrame = 2` per-frame cap mirrors `MaxSpawnsPerFrame`. See `docs/dev/plan-450-b3-lazy-reentry.md`.

**Phase B2 (next):** coroutine split of `BuildTimelineGhostFromSnapshot`. Targets the dominant 15.90 ms timeline bucket. Post-B3 playtest (`logs/2026-04-18_1947_450-b3-playtest/KSP.log:14474`) confirmed the gating: heaviestSpawn reentry dropped to `0.00 ms`, timeline now 93 % of the single-spawn cost (`17.59 / 18.91 ms`), and the diagnostics health line reports `deferred 4 buildsAvoided 3` — three trajectories saved the full build entirely by never entering atmosphere. Ready to be planned and implemented.

**Phase B1 (not planned):** the 15 ms latch threshold means #450's diagnostic only fires on bimodal cases, so the "spread across many spawns" case B1 targets is structurally out of scope of the evidence. #414's count cap already covers that pattern.

**Scope:** Phase B2 = Medium (coroutine split, new invariants).

**Dependencies:** #414 shipped, Phase A shipped, Phase B3 shipped.

**Status:** Phase A + Phase B3 shipped. Phase B2 TODO — gating confirmed by the 2026-04-18_1947 playtest; ready to plan. Priority: medium. Not release-blocking for v0.8.2 but should not survive the v0.8.3 cycle.

---

## 435. Multi-recording Gloops trees (main + debris + crew children, no vessel spawn)

**Source:** world-model conversation on #432 (2026-04-17). The aspirational design for Gloops: when the player records a Gloops flight that stages or EVAs, the capture produces a **tree of ghost-only recordings** — main + debris children + crew children — all flagged `IsGhostOnly`, all grouped under a per-flight Gloops parent in the Recordings Manager, and none of them spawning a real vessel at ghost-end. Structurally the same as the normal Parsek recording tree (decouple → debris background recording, EVA → linked crew child), with the ghost-only flag applied uniformly and the vessel-spawn-at-end path skipped.

**Guiding architectural principle:** per `docs/dev/gloops-recorder-design.md`, Gloops is on track to be extracted as a standalone mod on which Parsek will depend. Parsek's recorder and tree infrastructure will become the base that both Gloops and Parsek share — Gloops exposes the trajectory recorder + playback engine, Parsek layers the career-state / tree / DAG / world-presence envelope on top via the `IPlaybackTrajectory` boundary. Multi-recording Gloops must therefore **reuse Parsek's existing recorder, tree, and BackgroundRecorder infrastructure** rather than growing a parallel Gloops-flavored implementation. The ghost-only distinction is a per-recording flag on top of shared machinery, not a separate code path.

**Current state (audited 2026-04-17):**

- `gloopsRecorder` is a **parallel** `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`) — a temporary workaround that the extraction direction wants to retire.
- `BackgroundRecorder` is never initialized in the Gloops path — only alongside `activeTree` for normal recordings. Staging during a Gloops flight does not produce a debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`), so EVA does not produce a linked crew child either.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, adds it to the flat `"Gloops - Ghosts Only"` group (`RecordingStore.cs:394-418`). No `CommitGloopsTree`, no nested group structure.
- No conditional `IsGloopsMode` branch inside `RecordingTree`, no half-finished Gloops tree scaffolding.

**Net: Gloops is strictly single-recording by design today**, implemented as a parallel workaround. Multi-recording Gloops is a separate, sizable feature that should also consolidate Gloops onto the shared Parsek recorder (retire the parallel `gloopsRecorder` path).

**Desired behavior:**

- Gloops uses Parsek's main `FlightRecorder` + `RecordingTree` + `BackgroundRecorder` path, with a tree-level `IsGhostOnly` flag propagated to every leaf at commit. No parallel `gloopsRecorder`.
- Starting a Gloops recording creates a `RecordingTree` with the ghost-only flag; normal recording continues alongside on the same machinery if already active, or the tree operates solo if not. How the two modes interleave in the UI (explicit toggle, implicit based on UI state, etc.) is for the implementing PR to decide — possibly in coordination with a UI gate preventing concurrent career + Gloops capture.
- Staging during a Gloops flight → debris gets its own ghost-only recording via the normal `BackgroundRecorder` split path, with `IsGhostOnly = true` inherited from the tree.
- EVA during a Gloops flight → linked child ghost-only recording via the normal EVA split path.
- Commit: the whole Gloops tree flushes as a nested group under `"Gloops - Ghosts Only"` — e.g. `"Gloops - Ghosts Only / Mk3 Airshow Flight"` with child debris / crew recordings under it. Every leaf is `IsGhostOnly`.
- No vessel-spawn-at-end for any recording in a Gloops tree. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` already gates on `!rec.IsGhostOnly` (see `GhostPlaybackLogic.cs:3001`); the tree case reuses this.
- Per-recording delete / regroup / rename in the Recordings Manager works the same as normal trees.
- Apply-side: #432's filter reads `rec.IsGhostOnly` per-recording, so every leaf in a Gloops tree is already excluded from the ledger with no extra work.

**Files likely to touch (sketch, not exhaustive):**

- `Source/Parsek/ParsekFlight.cs` — retire `gloopsRecorder` in favor of the main `recorder`/`activeTree` path; the "Start Gloops" action creates a tree flagged ghost-only. `CheckGloopsAutoStoppedByVesselSwitch` goes away or is folded into normal tree commit.
- `Source/Parsek/FlightRecorder.cs` — remove `IsGloopsMode` branches once the parallel recorder is retired; the recorder becomes agnostic to career semantics (aligning with the extraction boundary in `gloops-recorder-design.md`).
- `Source/Parsek/BackgroundRecorder.cs` — carry a tree-level ghost-only flag so debris children inherit it.
- `Source/Parsek/RecordingStore.cs` — collapse `CommitGloopsRecording` into the normal tree commit path; the ghost-only distinction is per-tree (or per-leaf, if partial-Gloops trees ever become a thing, which they shouldn't).
- `Source/Parsek/UI/GloopsRecorderUI.cs` — controls now drive the main recorder with a ghost-only flag rather than spinning up a parallel instance.
- `Source/Parsek.Tests/` — tree-structural tests for multi-recording Gloops capture and commit.

**Dependencies / sequencing:**

- Ships after #432 (which closes the existing single-recording leak and establishes the per-recording `IsGhostOnly` apply-side filter that multi-recording Gloops will rely on).
- Coordinates loosely with the Gloops extraction work (`docs/dev/gloops-recorder-design.md` Section 11 — the extraction sequence); ideally this consolidation happens before extraction so the extraction moves a single unified recorder, not two.
- Not tied to the deterministic-timeline correctness cluster — this is a feature extension, not a correctness bug.

**Out of scope:**

- Making Gloops spawn real vessels at ghost-end (explicitly not wanted — Gloops is visual-only).
- Turning the existing single-recording Gloops path into a tree retroactively for existing saves (beta, restart the save if you want the new behavior).
- Actually extracting Gloops into its own mod. That's covered by `docs/dev/gloops-recorder-design.md`'s extraction plan. #435 is a preparatory consolidation step on the Parsek side.

**Priority:** Medium. Feature extension + architectural cleanup. Worth scoping after #432 lands.

**Status:** TODO. Size: L. New feature — not a follow-up to anything shipped today.

---

## 430. "Why is this blocked?" explainer for the committed-action dialog

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Desired behavior:**

- Replace the one-line reason with a structured block:
  - The action the player tried (e.g. "Research node: Heavier Rocketry").
  - The committed action that blocks it, including the source recording and its UT (e.g. "Already scheduled at UT 183420 in recording 'Mun Lander 3'").
  - A `Go to Timeline` button that opens the Timeline window and scrolls to the offending entry (reuses `TimelineWindowUI.ScrollToRecording`).
  - A `Revert to launch` shortcut if the player actually wants to undo it (routes to the existing rewind dialog pre-filled with the blocking recording).
- Keep the OK/close path unchanged so existing muscle memory still works.

**Why it matters:**

The mental model of "you can't do this because the timeline already did" is counter-intuitive for a first-time player. Showing the *which* and *when* turns a mysterious block into a debuggable constraint, reinforcing the ledger-as-truth principle every time a block fires.

**Files to touch:**

- `Source/Parsek/CommittedActionDialog.cs` — extend the dialog body; accept an optional `blockingRecordingId` + `blockingUT` + `blockingAction` tuple.
- `Source/Parsek/Patches/*Patch.cs` (where blocks are triggered for tech research / facility upgrade / part purchase) — pass the conflict context into the dialog instead of just the short reason string.
- `Source/Parsek/UI/TimelineWindowUI.cs` — already has `ScrollToRecording`; no changes beyond what's there.

**Out of scope for v1:**

- Auto-resolving the block by rewinding silently; this stays an informational dialog, not a one-click rewind.
- Collapsing multiple overlapping blocks into a summary (each block fires its own dialog as today).

**Status:** TODO. Size: S-M. Best quality-per-effort of the paradox-comms work.

---

## 428. Preview-rewind pane

**Source:** follow-up on the "cost-of-rewind is hard to intuit" thread. Rewind is the most consequential single action in Parsek — it moves the player back to a chosen launch point and replays forward with existing ghosts. But right now the rewind confirmation dialog shows a single summary line ("Rewind to 'Mun Lander 3' at Y1 D23?") and a raw count of "how many future recordings exist". A player can't tell before confirming: which exact recordings will be preserved, which will be replayed, which resources / contracts / milestones will be re-rolled, whether crew reservations will shift.

**Desired behavior:**

- Replace the existing one-line confirmation with a two-pane preview dialog anchored on the rewind button.
- Left pane: **"Before rewind point"** — committed recordings whose `EndUT <= rewindTargetUT` (stay intact on the ledger and their ledger effects remain applied); game-action milestones that already fired before the target; crew reservations that complete before the target.
- Right pane: **"Re-rolled forward"** — committed recordings whose `StartUT > rewindTargetUT` (they stay committed; their resource deltas + events re-apply from the target UT forward as the player plays); milestones pending at UT > target (they'll re-fire); crew reservations spanning the target (stand-in chain resets).
- Each pane shows a count + a preview list of the first ~5 items with `...and N more` if longer.
- Confirm / Cancel buttons unchanged.

**Why it matters:**

Rewind currently feels like a commitment to the unknown — the player isn't sure what they'll lose. Making the consequences legible before the dialog closes reduces regret and teaches the two buckets (before / re-rolled), which is the honest mental model: rewind is deterministic replay, nothing is thrown away.

**Files to touch:**

- `Source/Parsek/UI/RewindConfirmationUI.cs` (new or extension of the existing confirmation helper — current code is inlined in `RecordingsTableUI.ShowRewindConfirmation`).
- A `RewindPreview.Build(recordings, ledgerActions, milestones, rewindTargetUT, liveUT)` pure helper that classifies each item as "before rewind point" or "re-rolled forward". Lives next to `TimelineBuilder` since both walk similar data.
- Tests: classification helper fully covered (happy path + each bucket's edge cases + an item spanning the target UT).

**Out of scope for v1:**

- Previewing the new resource balance after rewind. Just show counts + first few items.
- Undo for rewind. One-way operation stays one-way.

**Status:** TODO. Size: M-L. Biggest UX win per dollar on the rewind mechanic.

---

## 427. Proactive paradox warnings surface

**Source:** follow-up on the conversation after shipping the Career State window. Today the mod prevents paradoxes mostly via blocks (action-blocked dialog) and a single red over-committed warning in the Timeline's resource footer. There's no centralized surface that says "your committed timeline has these N potential issues" — so a player can build up a career with, e.g., a contract that expires before its committed completion, or a facility upgrade requiring a level that won't be reached in time, and only discover the contradiction when it fires (or silently zeroes out).

**Desired behavior:**

- A **Warnings** badge on the main ParsekUI button row — hidden when count is 0, shown as `Warnings (N)` when any warning rules fire.
- Clicking opens a small scrollable window listing each warning as a row:
  - Category tag (`Contract`, `Facility`, `Strategy`, `Resource`, `Crew`).
  - One-line description (`Contract "Rescue Kerbal" deadline UT 240000 is before committed completion at UT 250000`).
  - `Go to ...` button linking to the relevant other window (Timeline scroll, Career State tab, etc.).
- Warnings are computed once per `OnTimelineDataChanged` fan-out (same cache-invalidation channel everything else uses).
- Starter rule set, each as a pure static helper in `WarningRules.cs`:
  - **ContractDeadlineMissed** — active contract's `DeadlineUT < terminal-UT of its committed completion recording`.
  - **FacilityLevelRequirement** — an action requires facility level N but the facility doesn't reach N until after that action's UT.
  - **StrategySlotOverflow** — projected active strategies > projected max slots (currently only warned in log, not UI).
  - **ContractSlotOverflow** — same for contracts.
  - **CrewDoubleBooking** — a stand-in appears in two chains at overlapping UT ranges.
  - **ResourceOverCommit** — already shown in Timeline budget footer, but also listed here for one-stop-shop.

**Why it matters:**

Action blocking catches paradoxes at the moment the player tries to violate them. Warnings catch *latent* contradictions that the ledger can detect but won't error on — the subtle ones where the ledger silently picks a resolution the player didn't intend (e.g. contract gets zeroed out because its deadline passed unexpectedly). Surfacing these early turns the mod's "structural paradox prevention" into a communicated design contract rather than a hidden invariant.

**Files to touch:**

- `Source/Parsek/UI/WarningsWindowUI.cs` — new scrollable list window.
- `Source/Parsek/WarningRules.cs` — new pure-static rule evaluators, one method per rule, each returning `List<Warning>` given `(ledger, recordings, modules)`. Heavy unit-test coverage.
- `Source/Parsek/ParsekUI.cs` — add the badge button + open toggle; integrate with `OnTimelineDataChanged` cache invalidation.
- `Source/Parsek.Tests/WarningRulesTests.cs` — one test per rule (happy + each flag condition).

**Out of scope for v1:**

- Auto-fix for any warning. Pure read-only surface.
- Severity levels / color-coding. All warnings are equal in v1; add severity in a follow-up if there are too many of one kind.
- Per-rule disable toggles. Playtesting can decide which rules feel noisy before we add knobs.

**Status:** TODO. Size: M. Complements the help popup (#426) — where help explains the system, warnings explain *your career's* specific issues. Together they turn the mod from "learn by experimenting" to "learn by seeing the model."

---

## 426. In-window help popups explaining each Parsek system

**Source:** follow-up conversation during the #416 UI polish pass. A player unfamiliar with the mod has to read `docs/user-guide.md` (out of the game) to understand what each window's sections and columns mean. The mechanics are specific enough (slots vs. stand-ins vs. reservations, per-recording fates, timeline tiers, resource budget semantics, etc.) that even tooltips-on-hover don't carry the full picture. An in-game help surface keeps the explanation next to the thing it explains.

**Desired behavior:**

- A small `?` icon button rendered in the title bar (or as the last button in the main toolbar row) of each Parsek window: Recordings, Timeline, Kerbals, Career State, Real Spawn Control, Gloops Flight Recorder, Settings.
- Clicking the `?` opens a small modal-ish popup window titled `Parsek - {Window} Help` anchored next to the parent window.
- The popup body is static help text tailored to that window. For tabbed windows (Kerbals, Career State), the help content should also cover each tab, either as one scrolling document or as a small tab-match sub-structure inside the popup. Keep each section brief (5-15 sentences) — the goal is orientation, not exhaustive docs.
- A "Close" button and `GUI.DragWindow()` so the popup can be moved.
- Help text can be hard-coded string constants in `Source/Parsek/UI/HelpContent/` (one file per window). No runtime load, no localization for v1.
- Suggested starter content:
  - **Recordings** — column-by-column walkthrough, L/R/FF/W/Hide button meanings, group vs chain vs ghost-only distinction.
  - **Timeline** — Overview vs Details tiers, Recordings/Actions/Events source toggles, time-range filter, resource-budget footer, loop toggle semantics on entry rows, GoTo cross-link.
  - **Kerbals** — slots vs stand-ins vs reservations (Roster State tab), chronological outcomes per kerbal (Mission Outcomes tab), outcome-click-scrolls-Timeline.
  - **Career State** — contracts / strategies / facilities / milestones tabs, current-vs-projected columns when the timeline holds pending recordings, Mission Control / Administration slot math.
  - **Real Spawn Control** — what it does (warp-to-vessel-spawn), State column, 500m proximity trigger.
  - **Gloops** — ghost-only manual recording, loop-by-default commit, X delete button in Recordings.
  - **Settings** — group-by-group overview (Recording, Looping, Ghosts, Diagnostics, Recorder Sample Density, Data Management); call out Auto-merge, Auto-launch, Camera cutoff, Show-ghosts-in-Tracking-Station.

**Out of scope for v1:**

- Inline tooltips on every sub-control (hover-tooltips already exist for a few buttons; expanding them is a separate follow-up).
- Localization / translation.
- Interactive tutorials.
- Search within help content.
- External hyperlinks (no browser launch from KSP IMGUI reliably).

**Files to touch:**

- New: `Source/Parsek/UI/HelpWindowUI.cs` (shared small popup window; takes a `windowKey` + body-text source).
- New: `Source/Parsek/UI/HelpContent/*.cs` (one static class per window, each exposes `public const string Body` or a `BuildBody()` method if dynamic content is needed later).
- Each existing window UI file (RecordingsTableUI, TimelineWindowUI, KerbalsWindowUI, CareerStateWindowUI, SpawnControlUI, GloopsRecorderUI, SettingsWindowUI): add a small `?` button and an `IsHelpOpen` toggle that feeds HelpWindowUI.
- `ParsekUI.cs`: add a single shared `HelpWindowUI` field + accessor so every window delegates to the same instance (only one popup open at a time).
- `CHANGELOG.md` entry under Unreleased.
- `docs/user-guide.md` can mention the new `?` buttons briefly but stays as the authoritative long-form reference.

**Status:** TODO. Size: M. Style it the same way as the rest of the mod (shared section headers, dark list box for paragraph groups, pressed toggle idiom if any sub-tabs appear).

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1` section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary `v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen without unpacking the authoritative files first.

Remaining high-value work should stay measurement-gated and follow `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- any further snapshot-side work now has to clear a higher bar: `.prec` and `_ghost.craft` are already roughly equal buckets after compression, and `_vessel.craft` is small, so "focus on snapshots next" only applies if a future corpus shifts the split back toward snapshots
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if a future rebaseline against a larger / more vessel-heavy corpus shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay covered by sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work — measurement-gated guidance for future shrink work rather than active tasks

---

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

Latest investigation: a second procedural-truss attempt was tested against fresh collected logs in `logs/2026-04-13_1529_fairing-truss-artifact`. The run correctly detected `FairingJettisoned` and rebuilt the ghost with `showMesh=True`, but the generated truss still looked bad in game: visible dark bars with transparent gaps following the fairing outline from base to tip. This confirms the simplified procedural replacement is still not shippable.

Important constraint: the current ghost snapshot is just a normal `ProtoVessel`/`ConfigNode` capture (`BackupVessel` output copied into `GhostVisualSnapshot`). That preserves fairing state such as `fsm`, `ModuleStructuralNodeToggle.showMesh`, and `XSECTION`, but it does not preserve the live runtime-generated stock Cap/Truss mesh deformation/material state from `ModuleProceduralFairing`. So the ghost cannot reproduce the exact stock truss visual from snapshot data alone.

To implement properly: prefer a stock-authoritative approach instead of another simplified procedural mesh. Most likely options are either capturing the live stock fairing truss render/mesh state at record time, or spawning/regenerating a hidden stock fairing from the snapshot and cloning the resulting stock truss renderers for the ghost. Only fall back to custom geometry if it can genuinely match stock quality.

**Status:** Open — do not revive the current simplified procedural-strip truss

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort
