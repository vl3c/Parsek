# Unified Testing Design — Systems, Composition, and the Build-Forward Program

**Purpose.** The single explainer for Parsek's entire testing and validation stack: what the three systems are, how each works, how they compose into one pipeline, and what to build next. Written for a solo developer who cannot recruit long-career playtesters — the design goal of everything here is **maximum validated behavior per human-minute**.

**Relationship to other authorities.** This document explains and directs; it does not track status. `docs/dev/autotest-status.md` remains the single status authority, `docs/dev/autotest-roadmap.md` (R1–R14) remains the committed build order for the harness track, and `docs/dev/test-coverage-audit-2026-07-29.md` (the companion audit) holds the measured current state and the ranked gap register this document's program is derived from. Where this document proposes work beyond R1–R14, it is additive and sequenced *around* that roadmap, not a replacement for it.

---

## 1. The model: a validation pyramid with three extra axes

Parsek's quality problem has a specific shape: the mod is a time-recording system whose bugs live in *composition* — a recording made in one session, superseded in another, replayed in a third, with career money attached. No single test tier can see that; the stack exists so that each tier proves one narrow thing and the tiers stack.

```
                    ┌─────────────────────────┐
   human eye        │  contact sheets, spot   │   minutes/release (goal metric)
                    │  playtests              │
                    ├─────────────────────────┤
   composed arcs    │  FORGE chains, career   │   hours, scheduled
                    │  grand-oracle runs      │
                    ├─────────────────────────┤
   flown scenarios  │  harness missions       │   40–235 s per registry cell
                    │  (kRPC + seam)          │
                    ├─────────────────────────┤
   live decisions   │  in-game test batches   │   6–17 s per registry cell
                    │  (seam-driven)          │
                    ├─────────────────────────┤
   pure logic       │  xUnit (~19k cases)     │   milliseconds
                    └─────────────────────────┘
```

**The atomic-decomposition rule** (binding for all new test work): every test at level N must be attributable to the levels below it. A flown scenario asserts only what its in-game and unit layers cannot; when a composed test fails, the failure must decompose into exactly one atomic layer's defect. Concretely: never write a flight assertion for a decision a unit test could pin; never write an in-game test for math that extracts to `internal static`; never compose a multi-mission arc whose individual missions aren't independently green. The corollary is the harness's proven cost law: **the batch lane buys coverage 4–20× cheaper per cell than the flight lane, and the flight lane must not grow until what already flies is properly gated** (roadmap sequencing rule).

Three axes cut across the pyramid, and coverage must be tracked on all three:

1. **Subsystem** (recorder, playback, rewind, ledger, logistics, render, UI…) — what the audit measures per-slice.
2. **Runtime dimension** — the committed D1–D18 registry (`harness/coverage/registry.toml`, 242 cells). This is the machine denominator; a behavior without a registry cell is invisible to coverage accounting.
3. **Game mode** — sandbox / science / career (registry D14 carries all three as first-class values):
   - **Sandbox** tests flight, recording, playback, rendering, crew *reservation* mechanics — everything with no currency attached. 48 of 55 specs live here; it is the correct default for mechanics.
   - **Science mode is the deliberately cheap middle rung.** It exercises the science-points ledger path (research-node debits, science capture/transmission) with *none* of career's confounds — no funds, no contracts, no facility gating, no part-purchase state. Any ledger behavior that touches only science should be proven here first (one spec exists today: `L1-research-node-science`; §5.4 expands this).
   - **Career** adds funds, reputation, contracts, facilities, tech gating, hiring costs. It is the hardest mode to reach by *playing* — and §5.4's central claim is that it mostly doesn't have to be played: career states can be **seeded** (templated into `persistent.sfs`) or **forged** (driven through real stock code via seam `KscAction` steps, then harvested), so the ledger can be validated at arbitrary progression points without automating the progression itself.

Finally, the goal metric: **human-eyeball minutes per release**. Every proposal in §8 is justified by how much human validation time it deletes or how much it sharpens the few minutes that remain.

---

## 2. System 1 — xUnit unit tests (`Source/Parsek.Tests/`)

**What it is.** ~19,000 cases across 848 files, running headless via `dotnet test` from `Source/Parsek.Tests`. No KSP, no Unity runtime (UnityEngine.CoreModule is referenced for `Vector3`/`Quaternion` math only).

**How it works — the load-bearing conventions:**

- **Pure-core extraction.** Any logic (guards, state transitions, math, serialization) is extracted to `internal static` methods so xUnit can reach it; MonoBehaviours keep only glue. This is followed well enough that 77% of the 3,179 pure methods are test-named, and the largest files have the most extraction. When you find KSP-coupled logic, the first question is always "what is the pure core here" (`SwitchSegmentBuilder` is the proof this works even for tree mutation).
- **Shared-static discipline.** Classes touching `ParsekLog` / `RecordingStore` / `ParsekScenario` statics take `[Collection("Sequential")]` + `ResetForTesting()` calls; log assertions go through `ParsekLog.TestSinkForTesting` (set in ctor, `ResetTestOverrides()` in `Dispose` — the sink *diverts*, it does not tee).
- **Generators** (`Tests/Generators/`): `RecordingBuilder`, `VesselSnapshotBuilder`, `ScenarioWriter`, `RouteFixtureBuilder`, etc. produce recordings, snapshots, saves, and RP quicksaves. They are the fixture supply chain for the *other two systems* too (the 272-recording corpus, the rewind-b9 fixtures) — which is why their ceilings (audit §2.1: ~50 un-emittable metadata keys, no MODULE nodes, 11 un-emittable scenario node types) are stack-wide constraints, not unit-test trivia.
- **Structural gates.** ~53 files assert on production source text (wiring gates, grep audits like ERS/ELS routing). Legitimate and cheap, but they are *presence* proofs — never let one be the sole coverage for behavior.

**What it can never prove:** Harmony patches actually applying, KSP API behavior, scene lifecycle, IMGUI, anything reflection-bound to live `PartModule` fields, `ParsekScenario.OnSave/OnLoad` against a real game (accepted limitation; source-text gates are the house pattern there).

## 3. System 2 — in-game runtime tests (`Source/Parsek/InGameTests/`)

**What it is.** 539 `[InGameTest]` declarations in 97 categories, discovered by whole-assembly reflection, executed inside a live KSP process. Run interactively (Ctrl+Shift+T → `TestRunnerUI`) or unattended (env hooks / command seam). Results export to `parsek-test-results.txt`; every batch emits one machine-readable `BATCH_COMPLETE v1 total=N passed=P failed=F skipped=S category=… scene=…` line — the contract the harness pins.

**How it works:**

- **Attribute surface**: `Category`, `Scene` (FLIGHT/SPACECENTER/TRACKSTATION/AnyScene; EDITOR is banned by an enforced contract test), `RunLast`, `AllowBatchExecution` (71 false), `RestoreBatchFlightBaselineAfterExecution` (72 true), `BatchSkipReason`. Tests are `void` (sync, 411) or `IEnumerator` (coroutine, 128).
- **Batch isolation** (campaign safety): FLIGHT-with-vessel/TS batches get in-memory quicksave/quickload *plus* on-disk `.bak` revert; the `.bak` is written *before* the `PARSEK_TEST_BATCH_MARKER`, so a mid-batch crash is reconciled on the next `ParsekScenario.OnLoad` in a fresh process. The R5 isolated path (`RunTests isolated="true"`, shipped 2026-07-27) admits restore-flagged batch-disabled tests — this is what makes the 68 previously-manual tests unattended-reachable.
- **Self-setup or skip loudly**: a FLIGHT test that cannot construct its context must `InGameAssert.Skip` naming what it needed (816 sites do). `InGameFixtureMath` self-sizes tolerances to the floating-origin float grid — the model for any world-position assertion.
- **Automation hooks** (all read once at Awake, unset = inert): `PARSEK_AUTORUN_TESTS`, `PARSEK_AUTORUN_EXIT`, `PARSEK_AUTORUN_ISOLATED`, `PARSEK_TEST_COMMANDS`.

**The two traps every new in-game test must avoid** (both bit us; audit §2.2):
1. **Vacuous pass** — a store-walk over an empty store, or a silent `if (x == null) return;`, reports PASSED while asserting nothing. Skip loudly instead; the anti-vacuity gate cannot see this class by proven construction — only the fixture defends.
2. **Tally sync** — adding a test to a harness-driven category reds `CommittedBatchTallySourceSyncTests` unless the spec's pinned `BATCH_COMPLETE` tally is updated in the same change.

**What it can never prove:** that a full mission flies, cross-scene arcs the seam can't reach (until R12), anything requiring mods not on the provisioned instance, and multi-hour behavior.

## 4. System 3 — automated flight harness (`harness/`)

**What it is.** A Python (stdlib-only) pipeline that provisions a dedicated KSP instance, stages a fixture save, boots the game, drives it — by seam commands and/or a kRPC-piloted mission — and classifies the outcome through a nine-row verifier chain (the M-C2 `saveParse` row landed 2026-07-31). 55 committed scenario TOMLs; daily tier ≈ 12 min, nightly ≈ 4 h.

**The moving parts, in run order:**

1. **Provisioner** (`provision/`, M-A6): builds `automation/<profile>/` from `pins.toml` + `profiles/*.toml` (stock-minimal today; modded-compat authored, never built). Its DEPLOY phase is the *only* path that puts this worktree's DLL into the automation instance — a dev-instance build proves nothing about a harness flight.
2. **Fixtures** (`harness/fixtures/saves/`): 12 committed save templates (9 sandbox, 1 science, 2 career), built by construction (headless text splicing, byte-identity drift gates) — never by play. Key learned invariants: a persisted `VESSEL` node loads regardless of tech unlocks (how `career-pad-craft` carries a flyable craft at the `start` node); a flyable template must splice an inert `ParsekScenario` node or the FLIGHT route records nothing.
3. **Command seam** (M-A2, `Source/Parsek/TestCommands/`): file-drop command/response channel, 19 implemented verbs (LoadGame, SetSetting, RunTests, recording verbs, TimeJump, InvokeRewind, AnswerMergeDialog, KscAction, SaveGame, 4 EVA verbs, FlushAndQuit…), at-most-once journal, fail-closed env gate. The seam is the best-tested code in the repo (404 xUnit cells + fake-KSP full-run smokes).
4. **Mission library** (M-B1, `harness/missions/`): pure decision cores (`mlib.py`) + an injectable kRPC/MechJeb runner. Five phase machines serve 18 missions; `b5_decide` alone serves nine (flyby/orbit/landing at any body, by parameters). The kRPC ceiling — what "AI can't play like a human" means concretely — is: it CAN launch, ascend, plan/execute transfers and captures, warp, land untargeted, rendezvous, dock, transfer resources, and drive EVA via seam verbs; it CANNOT fly planes, drive rovers, do precision landings, use action groups/fairings/claw/ISRU, run science experiments, or choreograph N>2 vessels without a bespoke machine.
5. **Verifier chain**: driver-validity (+mission, +missionOutcome) → batch-complete → offline analyzer (INV1–INV10, baseline-Forbid on fresh saves) → log validation → test results → anomaly sweep → expectations (recording count + log-contract regexes) → ledger oracle. Classification is orthogonal by design: a mission that didn't fly is driver-INVALID (retry once), never PARSEK-FAIL; a good flight Parsek mis-recorded is PARSEK-FAIL.
6. **Ledger oracle** (M-B2, `harness/lib/oracle.py`): expected career state (seed baseline + author-declared manifest) vs the produced save parsed independently — never reading Parsek-derived numbers (circularity ban).
7. **Coverage registry** (`harness/coverage/registry.toml`): the D1–D18 / 242-cell denominator. `[dimensionsCovered]` blocks are declarative — *claim is not gate*; a claim is honest only when a gating token/assertion backs it.

**What it can never prove (today):** anything visual (no screenshots, no pixel or geometry gate outside S1.6/S1.7's synthetic parity), UI health, perf/memory, occurrence-counts or ordering in logs, structural save content beyond a recording count (ADDRESSED by R9 2026-07-31: the `saveParse` row reads the produced save's ParsekScenario surfaces on every driver-valid run - report-only except on `S4.1-rewind-merge`, the one armed spec), scenes beyond FLIGHT/SPACECENTER (until R12), mod-compat behavior (until R14's instance exists).

## 5. How the systems compose

The stack is one pipeline, not three silos. The seams that make it so:

- **Shared analyzer core** (`Source/Parsek/Analyzer/`): the same pure invariant rules run offline (harness verifier) and in-game (H5 `RecordingInvariants`) — one implementation, two execution sites. The rule set is the template for any future shared oracle.
- **Shared fixtures**: `dotnet test --filter InjectAllRecordings` writes the 272-recording synthetic corpus that corpus-walking in-game categories and S1.4 playback both consume; `ScenarioWriter.BuildRewindPointQuicksave` authors the rewind fixtures the seam's `InvokeRewind` targets. Generator ceilings therefore bound all three systems at once.
- **Pinned contracts**: the harness pins whole `BATCH_COMPLETE` tallies and the C#-source-derived anomaly-token/declaration counts (`CommittedBatchTallySourceSyncTests`, `AnomalyGroundTruthEnumerationTests`) — cross-language drift reds locally, not on the next nightly.
- **The oracle's two layers**: pure Layer A (`CareerSaveParser` + `LedgerGroundTruthDiff`, xUnit-tested) is reused by the in-game Layer B harness and the Python oracle — three consumers of one parse/diff contract.
- **Composition upward**: FORGE scenarios fly a state into existence → operator harvests → committed fixture → later scenarios consume (deliberately git-mediated, never runtime-chained; forges must not chain off forges). `MISSION_HANDOFF_CONTRACTS` declare what a mission did *not* verify so a green can't over-claim. `b5_decide`'s parameter flags are the strongest atomic-composition primitive: nine scenarios share one byte-identical machine prefix.

**The mode axis in composition terms**: sandbox proves mechanics; science proves the science-ledger path with one confound; career proves the full economy. A career behavior should climb that ladder — unit (module arithmetic) → science-mode spec if science-only → career seam spec (vessel-less KscAction) → career flown spec (CL-1 shape) → composed career arc (§8 Phase 4).

---

## 6. Automated visual validation — closing the last human-only gap

The single largest human cost today: even a fully automated mission must be *watched* to know the orbit lines, polylines, ghosts, markers, and FX rendered correctly. The audit's key discovery is that the repo is much closer to closing this than assumed: a production, unit-tested, live-proven **recorded-vs-rendered geometry parity oracle** already exists (`MapRender/RenderParityOracle` + `MapRenderProbe` + `GhostTrajectoryPolylineRenderer` leg parity, gated by S1.6/S1.7 with negative controls). What's missing is aim, time, pixels, and delivery. The program, in build order:

**V1 — Aim the existing oracle at real missions (no C#).** Add a *map-dwell phase* to already-flown missions: after the flight completes, the mission runner sets `mapRenderTracing=true`, drives the kRPC map camera (`camera.mode = Map`, focus + pitch/heading/distance — fully scriptable in Python), and dwells across a warp ramp and an SOI crossing while the probe samples every frame. This converts 47 one-frame synthetic parity assertions into continuous parity over real recorded geometry — the exact thing a human currently watches for.

**V2 — Gate what is already measured (near-zero effort).** `GhostFxEmissionProbe` already *measures* runtime flame direction (`angleFromDown`; ~0 = correct, ~180 = inverted) and logs it to a line nothing reads. Raise a Tier-C anomaly + forbidden-pattern on inverted emission. Same move for the nine raised-but-ungated anomaly reasons (`icon-teleport`, `icon-off-orbit`, …): measure their green-run frequency via `unlistedReasons`, then gate with count budgets rather than binary allow-lists.

**V3 — Contact sheets: spend the human minutes well.** Make artifact collection unconditional (today `collect-logs` runs only on non-PASS — inverted for visual review), and add `harness/tools/contact_sheet.py` (stdlib, no image decoding): per run, an HTML grid of captured frames, each captioned with the parity/anomaly/`BATCH_COMPLETE` log lines from ±N frames around it. Your validation becomes a 30-second scan of numbers-next-to-pictures instead of a play session.

**V4 — `Screenshot` + `MapCamera` seam verbs (~150 lines C#).** Deferred-completion verbs mirroring the existing pattern; gives seam-driven specs the camera staging kRPC already gives missions, and produces the frames V3 displays. An env-armed auto-capture trigger (`PARSEK_AUTOCAPTURE`, mirroring `AutorunHooks`) should capture on every Tier-C anomaly and at known interesting moments (descent window, SOI `bodyChanged`, `LoopRestarted`, `OverlapExpired`, undock) — a picture of every anomaly the system raises.

**V5 — The self-consistency pixel oracle (the strongest pixel idea).** Capture the same paused, camera-pinned frame twice, differing only in one Parsek-controlled toggle (`showCommittedFutureOverlays`, already seam-whitelisted). The XOR of the two frames is *precisely the Parsek-drawn pixels* — immune by construction to skybox/terrain-LOD/particle variance that makes golden images flaky. Diff in C# (`CaptureScreenshotAsTexture` + `GetPixels32`), emit one scalar summary line, gate through the existing `logContracts` chain: non-zero delta pixels (**rendering anti-vacuity** — "something actually drew"), pixel count in band, centroid within N px of the `WorldToScreenPoint`-projected expected position, connected-component count = expected marker count. Catches invisible ghosts, clipped/off-screen/double markers, z-order failures — the whole "did anything appear" class no structural assertion reaches.

**V6 — Geometric invariants (pixel-free), riding the existing probe:** icon-distance-to-*drawn*-line; seam-gap between UT-contiguous legs; ownership conservation in the *reverse* direction (Director claims it → something must have drawn); monotone playback progress per segment (the first time-dimension invariant — catches frozen/backwards ghosts); screen-space marker↔line attachment + at-most-one-marker via a post-OnGUI reconcile (closes the IMGUI blind spot); one-shot line-style and mesh-scale checks; later, a flight-scene mesh parity oracle reusing `ComputeDrift` (RELATIVE sections must resolve through `TryResolveRelativeWorldPosition`; tolerances via `SceneFloatGridToleranceMeters`).

**V7 — UI smoke rendering.** Generalize the proven probe-MonoBehaviour pattern (`LogisticsTooltipEchoImguiTest`) into a `UiSmokeRender` category that draws all 15 IMGUI windows ≥2 Layout+Repaint cycles against production `DrawWindow` and asserts no exception + sane layout metrics — paired with adding raw Unity exception patterns (`NullReferenceException|ArgumentException|MissingReferenceException`, calibrated with a benign allowlist) to every spec's forbidden list. Today an IMGUI exception storm passes every scenario silently; this pair closes the single largest fail-open in the visual slice.

**Golden-image diffs are deliberately last and never a flight-scene gate** (PQS LOD + particle RNG). If pursued: map/TS shots only, UI hidden, Unity Graphics-Test-Framework three-threshold model, report-only for ≥10 runs first. Our pinned render settings and single-machine setup remove the flake source that kills golden images elsewhere, so it can eventually work — but V1–V7 deliver more, sooner, without golden files at all.

---

## 7. Binding constraints (do not re-litigate)

Condensed from the design docs and paid-for lessons; full register with citations in the audit and agent notes. Any new test work must respect these:

- **Oracle independence**: never read Parsek-derived numbers (ERS/ELS/recalc output) into the ledger oracle; never sum captured stock awards into EXPECTED; never assert module-getter vs live-singleton after a patch (the `TopBarReflectsLedgerAfterRecalc` tautology).
- **H5/invariant walks read RAW `CommittedRecordings`**, never ERS — ERS hides exactly the rows INV7 checks.
- **Commanded latch ≠ compliance** — assert observed effects (part events, save state), not "we issued the command". **Token presence ≠ reachability** — grep an archived log before pinning. **Claim ≠ gate** — a registry claim without a backing assertion proves nothing.
- **Fixtures are production-shaped** (RP quicksaves, guid-stamped sidecars) — fixture divergence has already cost a wrong diagnosis. The corpus injector strips `spawnedPid` by construction; some cells need C# fixture changes, not specs.
- **Never reference `Parsek.Tests` from `Parsek.csproj`** (pulls xUnit into the shipped DLL) — in-game tests cannot use `RecordingBuilder`; share pure cores by moving them into `Parsek` (the Analyzer precedent).
- **Test contracts, not constants** (monotonic/bounded/deterministic — not tuning values); never mock sidecar I/O in rendering tests; no schema migration paths; no re-fly leaf-check shortcut; no TrackSection fields on the BG on-rails state; don't relax the `ReFlySessionMarker` tree-id gate; don't make the harness avoid S4.1's idle-discard bug; the near-vacuous batch pin (`total=42 passed=1 skipped=41`) is proven unclosable statically — fixture quality is the only defense.
- **Count in-game declarations only via `hlib.parse_ingame_test_declarations`**; one category per spec (until R13); `_driven_category` gates only the FIRST `RunTests` step; tracers reset OFF at stage and teardown — arm them explicitly per spec.

---

## 8. The build-forward program

Sequenced by the audit's ranking; each phase's exit condition makes the next phase's green mean more. Registry cells and roadmap items (R#) named where they apply. Phases 0–2 are almost entirely spec/config work; C# grows only where a capability is genuinely missing.

### Phase 0 — Close the fail-open gates (spec-only; days)

Green must mean something before coverage grows.

1. Raw-Unity-exception forbidden patterns on every spec (calibrate → allowlist) — audit B1.
2. Rewrite `STOCK_AWARD_PATTERNS` from the archived CL-1 log + literal-line oracle unit test — revives the ledger oracle's independence leg (B2, operator item 3).
3. Anomaly bookkeeping: retire the dead `icon-jump` token, budget-gate `icon-teleport`/`icon-off-orbit`, arm `ghostRenderTracing` on one spec with a raise shape the sweep can match (B3/B4).
4. Diagnose B4's chute from part events in an archived recording; replace the commanded latch (B5).
5. Convert the ~19 silent early-return PASS sites to loud Skips (B6).
6. ERS/ELS gate: add `CommittedTrees` to the patterns, fail (not skip) on missing pwsh in CI (B7).

### Phase 1 — Execute the coverage that is already written (weeks; ~30 specs ≈ +30 min wall)

The cheapest absolute wins in the stack — R5's unlock is sitting unused.

1. Wire the ~68 reachable-undriven in-game categories as batch specs (R6–R8): `Rewind` (37 — the largest never-executed asset), `GhostLifecycle`, `CrewReservation`, `GhostAudio`, `Optimizer`, `BackgroundSeeder`, D13 spawn categories, `LedgerGroundTruth` (now reachable via `career-pad-craft` — the cheapest large increase in ledger trust available). Read each category's skip preconditions first and pick fixtures that satisfy them (the vacuity rule).
2. Run S1.5 and S4.1 unattended (2 boots → 7 D9 cells); fix S4.1-IDLE-DISCARD (a ~30-minute behavioral unit test + the product call) so S4.1 stops flaking.
3. Provision `modded-compat` (R14): one instance + one spec = 17 stranded compat tests, 3–4 D17 cells, the FX-fingerprint A/B diff (§6 V-adjacent), and the first second run lane.
4. R12: `SimulateStockSwitchClick` + a `scene=` route on `LoadGame` — unlocks the 9 stranded TrackingStation tests, the real switch-segment cells, and the TS visual surface in one verb.
5. Marginal tokens on flights that already fly: staging-debris TTL/promotion on B2, a BG-vessel SOI section on B7, F5/F9 seam steps mid-ascent, SOI-crossing playback in S1.4's corpus — ~8 registry cells for zero new flight time.
6. Fixture unlocks: `WithSpawnedPid` in the corpus writer; a two-loaded-vessel proximity fixture (the precondition for every live dock/undock/delivery/grapple test); R13 multi-category batches to collapse boot count.

### Phase 2 — Make green deep: structural assertions + data-integrity units (weeks)

1. **R9 — structural save-content expectations** (was the single highest-leverage unbuilt item; harness half SHIPPED 2026-07-31 report-only and ARMED on S4.1 the same day, analyzer half still open): `[expectations.recordings.structure]` over the analyzer's parsed model (branch-point counts by type, section frames/anchors, terminal state + body), plus landing the three inert `route`/`rewind`/`loop` expectation families and `[expectations.world]`. Retires presence-only grep as the primary proof.
2. R10 — runtime-handle plumbing (`${step.field}`), unblocking verbs that address live trees/vessels/routes.
3. The Tier-A data-integrity units from the audit: `SafeWriteConfigNode` (check `Save()`'s return, use `File.Replace`, test all three `SafeWrite*` — landed via PR #1375 while this document was in review), the schema-reject prune chain end-to-end and `SaveActiveTreeIfAny` both-or-neither (both landed via PR #1376), rewind-across-SOI, RP-slot ambiguity, the crew-death→tombstone→rep unit chain (with the wrong `CrewKilled`-vs-`VesselLoss` mapping as the first assertion — the mapping fix plus the reconciler's per-case unit suite landed via PR #1381; the flown end-to-end chain still waits on R12).
4. Crash-matrix systematization: journal fault-injection as a `[Theory]` over the phase enum (a new phase reds automatically; adds the missing `Split` case free); extend `RewindInvoker`'s checkpoint hook to ~8 points with a throw-at-each theory.
5. Property/fuzz foundations (copying `RecalculationFuzzer`'s seeded pattern): extend the ledger fuzzer to all 9 modules with state invariants (finite pools, permutation-invariance, idempotence, seed survival, ELS ⊆ Ledger); a random-tree fuzzer for the supersede/chain/closure walkers (termination, closure ⊆ committed, idempotence). DONE 2026-07-30: both landed via PRs #1387 (`LedgerStateFuzzerTests`) and #1388 (`EffectiveStateGraphFuzzerTests`); the audit's Tier E unit batch landed the same day via PRs #1378/#1380-#1385.

### Phase 3 — The visual program (§6 V1–V7, in that order; weeks, interleaved with Phase 2)

Exit condition: a nightly produces a contact sheet whose every frame carries a mechanical verdict, and "look at the game" means reviewing flagged frames only.

### Phase 4 — Mode-axis expansion: science and career without playtesters (weeks)

1. **Science-mode lane**: promote science-only ledger behaviors to `fresh-science` specs — science capture, transmission-vs-recovery scalars, research-node debits, tech stickiness across rewind. Cheap, low-confound, and currently one spec deep.
2. **Seam-forged career states** (the FORGE pattern applied to progression): N `KscAction` steps drive *real* stock unlock/upgrade/hire code from `fresh-career`, then `SaveGame` → harvest → commit as `career-t2-*` fixtures. No progression automation, no hand-authored contract nodes — the game itself produces the state, and the oracle's seed baseline reads it automatically.
3. **Templated mid-career matrix** where forging is too slow: the fixture builders' ConfigNode text-editing helpers already support splicing `Funding`/`ResearchAndDevelopment`/`Reputation`/facility nodes. Known obstacles are documented (facility `lvl` is a normalized fraction; tech nodes need explicit part lists; the provisioned tree is CTT+PBC, not stock; always splice the inert `ParsekScenario` node into flyable templates). Contracts/milestones stay out of templates — they are measured from flights (CL-1's already-recorded milestone numbers become an `L2-milestone-career` manifest for free).
4. **Ground-truth hardening**: wire `LedgerGroundTruthHarness` into a career spec; extend `CareerSaveParser` with roster/StrategySystem/tech-node parses (unblocks the deferred L1-dismiss assert and a strategy spec); make `StrictPerIdentityForTesting` settable per-scenario — harness fixtures ARE clean Parsek-native careers, so per-identity facets can gate there.
5. **The L-track grand oracle** (the end goal): a long career arc — launch, fly, rewind, re-fly, recover, repeat across sessions — oracle-diffed at every session boundary. Requires Phase 0 item 2 (oracle leg alive) and benefits from every phase above; this is the automated replacement for the long-career playtest that can't be recruited.

### Phase 5 — Mission-fleet expansion (scheduled, not opportunistic; the flight lane grows only after Phases 0–2)

1. **Parameter sweeps first**: `b5_decide` reaches Ike/Gilly/Dres/Eeloo/Moho and the Jool system by spec edits — D14 multipliers at param-only cost (watch warp budgets on nested SOIs).
2. **Craft fleet growth — including downloaded craft.** The pipeline supports it with known constraints: stock+MH parts only on `stock-minimal`; `.craft` version 1.12.5 (older triggers an un-clickable compat dialog); staging/docking/resource conventions the mission shells assume; and inertia-tuned burn budgets that must be re-measured per craft. Two intake routes: drop into a fixture's `Ships/VAB/` for `launch_vessel` missions (sandbox; career VAB launches are tech-gated), or **serialize as a persisted `VESSEL` node** (loads regardless of tech state — the career-safe route, and the `[fixture].craft` spec key should be fixed or deleted, since it currently copies to a dead path). A small curated fleet — a Duna-capable lander (heat shield + chutes), a station+tug pair, a high-part-count stress vessel — unlocks more scenario diversity than any new phase machine.
3. **New capability in cost order**: Duna landing (incremental — `b5_decide` params + new craft + re-tuned budgets), rendezvous-without-docking (isolates spawn-safety cells), aerobraking/aerocapture (new terminal class), then the Tier-4 declarative multi-piece mission composer (the prerequisite for Jool tours and N-vessel assembly — don't hand-write another bespoke 18-phase machine first).
4. **Perf/soak lane**: a high-part-count fixture and a 20–50-ghost playback scenario with frame-cost and memory-growth budgets (the design principle "every per-frame computation multiplies" currently has no measurement anywhere); a 60k-point recording round-trip budget; `ComputeERS` cost at the 272-corpus.

### Ongoing — hygiene that keeps the system honest

- Update stale design registers (analyzer namespace/version, seam verb table, rewind §13.1 names) or mark sections historical — a reviewer reads them as authoritative.
- Write the missing design-promised tests when touching their features (the rewind stable-leaf acceptance suite is the standing debt; its scenarios are already written).
- Every new feature follows the workflow's scenario→test step — the audit shows exactly which features skipped it and what that cost.
- Consider a minimal mutation-adequacy spot-check (hand-broken build → confirm the expected cells red) once per release; no tooling exists and full mutation testing is out of scope, but one deliberate mutation per release keeps "this cell bites" claims honest.

---

## 9. Recipe: adding a test for a new behavior (the atomic checklist)

1. **Extract the decision** to `internal static`; unit-test it (including the "what makes it fail" justification — a test that can't fail for a realistic bug shouldn't exist).
2. **If the behavior is runtime-bound** (patch, scene, PartModule, IMGUI): add an in-game test — self-setup or Skip loudly; pick the right Scene; mind `AllowBatchExecution`/restore flags; never assert into an assumed context.
3. **Give it a registry cell** if it's a new runtime dimension value — otherwise coverage accounting can't see it.
4. **Gate it unattended**: extend an existing category's spec (update the pinned tally in the same change) or add a batch spec; prefer the batch lane; add a flight only if the behavior genuinely requires a flown situation.
5. **If it renders**: add its Tier-C anomaly or parity surface (§6) so the visual layer sees it without eyes.
6. **If it touches career state**: declare its oracle manifest entry; climb the mode ladder (science before career where possible).
7. **Docs in the same commit**: CHANGELOG, todo, autotest-status if a gate/scenario/module changed.

---

*Companion: `test-coverage-audit-2026-07-29.md` (measured state, ranked gaps). Status: `autotest-status.md`. Harness build order: `autotest-roadmap.md`.*
