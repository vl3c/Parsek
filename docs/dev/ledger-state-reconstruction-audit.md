# Audit: Ledger / Game-Actions State-Reconstruction — Logging & Test Coverage

**Status:** investigation report (2026-06-08). No code changed by this audit.
**Scope:** the ledger / game-actions system that reconstructs **career scalar state**
(funds, science global + per-subject, reputation, facilities, contracts, milestones,
strategies, kerbals, tech tree) after rewind / fast-forward / time-warp / save-load.
**Audience:** future agents implementing the follow-up work (prompts in
`docs/dev/ledger-audit-followup-prompts.md`).

> **Why this matters.** This subsystem rewrites real KSP career values
> (`Funding.Instance`, `ResearchAndDevelopment.Instance`, `Reputation.Instance`,
> facility levels, tech tree, contracts) every time the player rewinds, warps, or
> reloads. A silent bug here corrupts an entire career save. The two parts of the
> system that are *most* dangerous — the live **apply boundary** and the **closed
> rewind→recalc→reapply loop** — are the *least* covered by logging and tests.

---

## 1. Bottom line

- **Logging:** the three headline pools (funds, science pool, reputation) are fully
  debuggable from a default-level `KSP.log` (`old → new (delta=…, target=…)` at Info).
  Everything else — per-subject science, tech nodes, facility levels, milestones,
  contracts, vessel deletion — logs **identities only at Verbose** and surfaces
  **only aggregate counts at Info**. A corruption in any of those is effectively
  invisible in a normal log.
- **Tests:** pure recalc math, serialization, and supersede/tombstone/cutoff topology
  are genuinely strong (hundreds of focused unit tests). But the **live apply boundary
  is exercised by only 2 in-game tests with synthetic inputs**, and there is **no
  end-to-end test that reconstructs state and compares it to a ground-truth save** —
  and none that touches vessels.
- **Verdict:** coverage is **sufficient for the recalc math layer** and **insufficient
  at the apply boundary and the full round-trip level**. A targeted logging upgrade, a
  lightweight event-driven ledger tracer, and an in-game ground-truth verification
  harness are all warranted. A full per-frame probe clone (like the render tracer) is
  **not** warranted — ledger state changes on discrete recalc events, not per-frame.

---

## 2. Pipeline overview (apply boundary)

**Apply funnel:** `KspStatePatcher.PatchAll` (`GameActions/KspStatePatcher.cs:39`) is the
single entry for career-resource writes. It runs all mutations inside
`SuppressionGuard.ResourcesAndReplay()` (`:49`) and dispatches:
`PatchScience` → `PatchTechTree` → `PatchFunds` → `PatchReputation` →
`PatchFacilities` → `PatchMilestones` → `PatchContracts`.

**Orchestration entry points** (`GameActions/LedgerOrchestrator.cs`):
- `RecalculateAndPatch` (`:1325`) → `RecalculateAndPatchCore` (`:1705`) is the universal
  driver — called on **commit, rewind, warp-exit, time-jump, KSP load**.
- Wrappers funnel into the core: `RecalculateAndPatchForCurrentTimelineUT` (`:1344`,
  post-rewind/time-jump), `RecalculateAndPatchForTimeJump` (`:1377`),
  `RecalculateAndPatchForLiveTimelineEvent` (`:1411`),
  `RecalculateAndPatchAfterTombstones` (`:1439`, supersede tombstone refresh).
- Core flow: `SeedInitialResourceBalances` → `PurgeGhostOnlyActionsFromLedger` →
  `BuildRecalculationActions` (ELS via `EffectiveState.ComputeELS`) →
  `RecalculationEngine.Recalculate` → **patch-deferral gate**
  (`GetKspPatchDeferralReason`, `:1764` — skips the live KSP write while a live/pending
  tree exists) → `ApplyRecalculatedStateToKsp` (`:1889`) → `KspStatePatcher.PatchAll`.
- `kerbalsModule.ApplyToRoster` (`:1903`) runs separately inside
  `ApplyRecalculatedStateToKsp`, **before** `PatchAll`.

**Recalc engine** (`GameActions/RecalculationEngine.cs`): pure computation, no KSP
access. Sorts actions by `(UT, earning-before-spending, sequence)`, resets modules,
walks forward dispatching to tiers (first-tier: Science/Milestones/Contracts/Kerbals;
strategy transform; second-tier: Funds/Reputation; facilities). Emits a single
recalc-complete summary via `VerboseOnChange("RecalcEngine", "recalculate-summary", …)`
(`:214-232`) — counts only, no value-level truth comparison. UT-cutoff filtering
(`:144-233`) + a shadow projection walk (`RunProjectionWalk`) reserve future spendings.

**Strategies note:** strategies are a ledger-transform tier only — there is **no live
`StrategySystem` mutation anywhere in production code** (only referenced in UI/tests).

---

## 3. What's already solid (do not rebuild)

- **Recalc math:** `FundsModuleTests` (65), `ScienceModuleTests` (53),
  `ReputationModuleTests` (48), `ContractsModuleTests` (43), `RecalculationEngineTests`
  (39), `FullCareerTimelineTests`, `CrossTierIntegrationTests` (7) — thorough,
  hand-verified constants.
- **Serialization / migration / supersede topology:** `GameActionSerializationTests`
  (51), `GameStateEventTests` (102), `RewindSupersedeRollbackTests` (43),
  `SupersedeCommitTests` (101), `SupersedeCommitTombstoneTests` (23),
  `RewindUtCutoffTests` (~80), `EffectiveStateTests` (39),
  `LedgerTombstoneRoundTripTests`, `LedgerRecoveryMigrationTests` (14).
- **Headline-pool apply logging:** `PatchFunds` (`KspStatePatcher.cs:733`),
  `PatchScience` (`:160`), `PatchReputation` (`:803`) each log `old → new
  (delta=…, target=…)` at **Info**, with a loud **Warn** drawdown-guard clamp + session
  toast (`:2298`) and a >10% suspicious-drawdown Warn (`:723`).
- **Idempotence:** `DoubleRecalculate_IsIdempotent` proves a repeated walk on the same
  list is stable.
- **Kerbal roster apply:** create / recreate / delete / retire log per-kerbal at
  **Info** (`KerbalsModule.cs:1398/:1384/:1490/:1465/:1477`).

---

## 4. Logging audit

### 4.1 Per-resource apply + logging table

| Resource | Mutation site | old→new at Info? | Identity at Info? | Notes |
|---|---|---|---|---|
| **Funds** | `Funding.Instance.AddFunds` `KspStatePatcher.cs:730` | **YES** `:733` | n/a (single pool) | delta + target logged |
| **Science (pool)** | `AddScience` `:157` | **YES** `:160` | n/a | delta logged |
| **Reputation** | `SetReputation` `:800` | **YES** `:803` | n/a | uses Set (not Add) to avoid double curve |
| **Per-subject science** | `kspSubject.science = target` `:871` | **NO (none at any level)** | **NO** | aggregate `patched=/cleared=` only `:887`; **unclamped** by drawdown guard `:2295` |
| **Tech tree** | `SetTechState` `:377`, `protoNodes.Remove` `:388` | per-node Verbose only | per-node Verbose only | Info aggregate `madeAvailable=/madeUnavailable=` `:412` |
| **Facility level** | `facility.SetLevel` `FacilityStatePatcher.cs:109` | per-facility Verbose `:112` | per-facility Verbose | Info aggregate `patched=` `:132` |
| **Facility destruction** | `db.Demolish()`/`db.Repair()` `:231/:239` | per-building Verbose `:233/:240` | Verbose | Info aggregate `:250` |
| **Milestones (one-shot)** | reflection set `reached/complete` `KspStatePatcher.cs:1088/:1098` | per-node Verbose `:1092/:1104` | Verbose | Info aggregate `credited=/unreached=` `:996` |
| **Milestones (repeatable)** | reflection field writes `:1363-1391` | single Verbose `synced` `:1418` | Verbose | — |
| **Contracts** | `currentContracts.Add` `:1727`, `RemoveAt` `:2177` | per-contract Verbose `:1675` | Verbose/Warn | Info aggregate `removedStale=/restored=` `:1774` |
| **Kerbal roster** | `TryRemove/TryCreate/TryRecreate` | per-kerbal **Info** | **Info** (name + slot) | well-covered |
| **Strategies** | none (ledger-transform only) | n/a | n/a | no live mutation |
| **Vessels** (rewind only) | `Vessel.Die()` `PostLoadStripper.cs:280` | n/a | **bare-PID list only** `:213` | see §4.3 |

### 4.2 Logging gaps ranked by career-corruption risk

| # | Risk | Site | Problem |
|---|------|------|---------|
| 1 | **HIGH** | `KspStatePatcher.cs:871` per-subject science | No per-subject old→new at *any* level; only aggregate counts at Info. Drawdown guard leaves this **unclamped** (`:2295`). A corrupted subject total is undiagnosable from the log. |
| 2 | **HIGH** | `:377/:388` tech-node availability flips | Per-node identity only at Verbose ("first 10 missing", `:428`); Info is counts only. A wrongly re-locked researched node shows only `madeUnavailable=N`. |
| 3 | **HIGH** | `:2177/:1727` contract remove/restore | contractId only at Verbose/Warn; Info is counts. A wrongly-removed active contract (lost advance/reward) is not identifiable. |
| 4 | MED | `FacilityStatePatcher.cs:109` `SetLevel` | Per-facility old→new only at Verbose. A silently-downgraded launchpad (blocks launches) shows `patched=1`. |
| 5 | MED | `PostLoadStripper.cs:280` `Vessel.Die()` | No per-vessel Info line; destroyed vessels appear only as a joined **bare-PID list** on `Strip stripped=[…]` `:213`. PID is craft-baked / non-unique → forensically weak. |
| 6 | MED | `:1088/:1098/:1363` milestone flips | Per-node set/clear only at Verbose. A wrongly-cleared milestone can revoke its reward on the next recalc. |
| 7 | LOW | `LedgerOrchestrator.cs:1786/:1769` | The single most important branch — `authoritativeReduction` (authorized rewind drawdown vs buggy live clobber) and patch-deferral reason — logged only at Verbose. |

**Helper note:** `VerboseStablePatchState` (`KspStatePatcher.cs:24`) routes skip/no-op
states through `ParsekLog.VerboseOnChange`; actual-mutation logs use `ParsekLog.Info`.

### 4.3 Vessel reconstruction logging

Vessels are **not** patched by the ledger system (`grep ProtoVessel|FlightGlobals.Vessels`
in `KspStatePatcher.cs` = zero hits). They are reconstructed only on the
**Rewind-to-Separation** path: `RewindInvoker.ConsumePostLoad` →
`ReconciliationBundle.Restore` (repopulates `FlightGlobals.Vessels` from the RP
quicksave; Info `vesselCount=` `:642`) → `PostLoadStripper.Strip` (vessel destruction via
`Vessel.Die()` under `SuppressionGuard.Crew()` so deaths don't fan out to the ledger).
Per-vessel `Die()` is logged at **Verbose** for strict-unmatched strips (`:204`) and
only as a joined PID list in one Info summary (`:213`); a throwing `Die()` is Warn
(`:286`). Survivor reconciliation Info `:993`; spawn/respawn routes through
`ParsekScenario.ReconcileSpawnStateAfterStrip` (`:995`).

---

## 5. Test coverage audit

### 5.1 Inventory by layer

| Layer | Representative tests | Verdict |
|---|---|---|
| (a) Pure recalc math (modules) | Funds(65)/Science(53)/Reputation(48)/Contracts(43)/Facilities(22)/Milestones(15)/Strategies(34)/Route(16) module tests, `RecalculationEngineTests`(39), `CrossTierIntegrationTests`(7), `FullCareerTimelineTests`(3) | **Strong** |
| (b) Serialization / round-trip | `GameActionSerializationTests`(51), `GameStateEventTests`(102), `LedgerTombstoneRoundTripTests`(3), `RecordingSupersedeRelationRoundTripTests` | **Strong** |
| (c) Load migration | `LedgerRecoveryMigrationTests`(14), `ActionIdMigrationTests`, `MergeJournalForkMigrationTests` | **OK** |
| (d) Rewind / supersede / tombstone / cutoff | `RewindSupersedeRollbackTests`(43), `SupersedeCommitTests`(101), `SupersedeCommitTombstoneTests`(23), `TombstoneEligibilityTests`(26), `EffectiveStateTests`(39), `RewindUtCutoffTests`(~80) | **Strong on logic, module-state-only** |
| (e) **KSP-apply (`KspStatePatcher`, 2315 LOC)** | `KspStatePatcherTests`(43), `PatchFundsSanityTests`(7), `ContractAcceptPatchTests`(6), `MilestonePatchingTests`(18), `ScienceSubjectPatchHardeningTests`(5) | **THIN at the real mutation boundary** |
| (f) Crash recovery | `MergeCrashRecoveryMatrixTests`(5), `MergeJournalOrchestratorTests`, `ParsekScenarioRecoveryRoutingTests` | **OK** |
| Reconciliation diagnostics | `EarningsReconciliationTests`(112), `PostWalkReconciliationIntegrationTests`(6) | diagnostic-only (WARN on divergence), not a state-diff |

### 5.2 The critical question — is the round trip verified against an actual save (incl. vessels)?

**NO.** There is no test that snapshots a real KSP save at UT X, rewinds, reconstructs,
and asserts reconstructed funds/science/rep/facilities/kerbals/**vessels** match that
save. Evidence:

- **`KspStatePatcherTests` run with `SuppressUnityCallsForTesting = true`.** In xUnit the
  `Funding`/`R&D`/`Reputation` singletons are null, so every real mutation site hits the
  `if (… == null) return;` early-return. xUnit covers only the **pure target
  computation** (`BuildTargetTechIdsForPatch`, `AdjustSciencePatchTargetForPending*`,
  `BuildSubjectIdsForPatch`, `BuildFacilityPatchTargets`) + null-guard logging — never
  compute-delta → write → read-back.
- **`FullCareerTimelineTests.FullMunLandingTimeline`** (closest E2E) asserts a full
  ~20-action timeline reconstructs to hand-computed constants (funds=39700, science=50,
  facility level 2, …) — but compares against **constants, not a save snapshot**, reads
  **in-memory module getters, not live `Funding.Instance`**, has **no rewind** (forward
  walk), and **no vessels**.
- **`RewindUtCutoffTests`** verify cutoff projection drops future actions correctly —
  module-state-only, no live singleton, no save, no vessels.
- **`TopBarReflectsLedgerAfterRecalc`** (`RuntimeTests.cs:15573`, in-game) asserts
  `Funding.Instance.Funds == FundsModule.GetAvailableFunds()` **after** patching — this
  is **circular** (the patcher just wrote the live value from the module; it always
  passes). It proves the write happened, not that the reconstruction is correct.
- **`GameActionsHealth`** in-game tests (`ExtendedRuntimeTests.cs:710+`) are singleton
  sanity only (`Funds >= 0`, rep in `[-1000,1000]`).
- The only live-singleton apply tests are 2 in-game tests (`RuntimeTests.cs:22661+`,
  drawdown-guard / spending-gate) using **hand-seeded synthetic modules**, not a
  recalculated ledger.

### 5.3 Riskiest untested paths

1. **`KspStatePatcher` live mutation with a recalculated ledger** — the exact code that
   writes real funds/science/rep/tech/facilities, only run live by 2 synthetic-input
   in-game tests. Highest-risk gap.
2. **Full closed-loop rewind→recalc→reapply numeric equivalence** — no test proves
   reconstructing at the live tip reproduces pre-rewind balances exactly.
3. **Per-subject science + tech-tree node-set apply against live R&D** — target-set
   computation tested; observed effect on `ResearchAndDevelopment.Instance` not.
4. **Cross-subsystem consistency (ledger scalars ↔ restored vessels)** — a rewind
   restores both; nothing asserts they agree.
5. **Facility-level/destruction apply on live `ProtoUpgradeables`** — target computation
   tested, live level change not.

---

## 6. The ground-truth oracle that already exists (key insight)

On rewind, `RewindInvoker` copies the RewindPoint quicksave
(`saves/<save>/Parsek/RewindPoints/<rpId>.sfs`) to the save root and calls KSP's
`GamePersistence.LoadGame` — loading a **real KSP save at the rewind UT**, with the
actual funds/science/rep/facilities/vessels baked in. **Only then** does
`RecalculateAndPatch(double.MaxValue)` run (`RewindInvoker.cs:908`), overwriting those
values with the recalc result.

⇒ The live KSP state **immediately after load, before the patch**, is ground truth.
Nothing currently reads it back to verify the recalc agrees. This is the natural oracle
for both a production self-check and an in-game verification harness.

---

## 7. Existing observability infrastructure (the reusable template)

The map/TS render tracer is the proven pattern to copy (selectively).

**Two-file shape:** a pure static trace module (`MapRenderTrace.cs`) + a MonoBehaviour
truth probe (`MapRenderProbe.cs`); sibling `GhostRenderTrace.cs` mirrors it for flight.

- **Gating (near-zero cost when off):** `MapRenderTrace.IsEnabled` (`:378`) =
  `ForceEnabledForTesting || ParsekSettings.Current.mapRenderTracing`. Every emit
  early-returns on `!IsEnabled`; the probe's whole `LateUpdate` is one bool check
  (`MapRenderProbe.cs:168`).
- **Three tiers:** Tier-A structural (`EmitStructural` `:719` → `Info`, opens a detailed
  window); Tier-B change-based truth (`EmitOnChange` `:670`, one Verbose line per
  *changed* field, caller-owned change detection); Tier-C anomalies (`EmitAnomaly`
  `:792`, pure Unity-free predicates `IsIconJump`/`IsLineBlink`/`IsIconOffOrbit`,
  rate-limited).
- **Intent → truth → reconcile (the core mechanism):** the decision site records its
  *intent* frame-stamped (`RecordLineIntent` `:861`); the probe runs at
  `[DefaultExecutionOrder(10000)]` so it reads ACTUAL state after renderers; pure
  `ReconcileLineState` (`:951`) returns a mismatch token; freshness gating
  (`IntentFreshnessFrames=0`) drops stale intent instead of false-flagging.
- **Detailed-window registry:** `detailedUntilByKey` (`:365`) keeps full per-frame detail
  only around interesting events; `Reset()` clears pid-keyed stores on scene switch.

**`ParsekLog` primitives a tracer builds on** (`ParsekLog.cs`): `Info/Warn/Error/Verbose`;
`VerboseRateLimited` (`:161`, per-key throttle + suppressed-count); `VerboseOnChange`
(`:271`, emit only when a caller `stateKey` flips per identity — already used by the
recalc summary); `WarnRateLimited` (`:404`, not gated on verbose); `RecState` (`:511`,
structured field-ordered grep-stable snapshot line — closest existing analogue to a
state tracer); test seams `TestSinkForTesting`/`TestObserverForTesting`/
`ClockOverrideForTesting`/`SuppressScope`; all `[ThreadStatic]`.

**Settings-flag plumbing template** (to add a `ledgerTracing` flag, mirror
`mapRenderTracing` across 4 files): `ParsekSettings.cs:56` (field +
`[CustomParameterUI]`), `UI/SettingsWindowUI.cs:458` (Diagnostics toggle),
`ParsekSettingsPersistence.cs:46` (key + record/load/restore), defaults-reset path
(`SettingsWindowUI.cs:195`). Convention suffix for heavy logs: `"(Warning: huge logs)"`.

**In-game test runner capability** (`InGameTests/`): reflection discovery
(`InGameTestRunner.DiscoverTests:227`), `[InGameTest]` attrs carry
`Category/Scene/Description/RunLast/AllowBatchExecution/RestoreBatchFlightBaselineAfterExecution`;
tests can be multi-frame `IEnumerator` coroutines; can quicksave/quickload
(`QuickloadResumeHelpers.TriggerQuicksave`, `ValidateQuicksaveStructure`), read live
`Funding/RnD/Reputation.Instance` + `FlightGlobals`, and assert via `InGameAssert`
(`AreEqual/ApproxEqual/IsGreaterThan/Contains/Skip`). Existing `SnapshotFinancials` /
`RestoreFinancials` helpers (`RuntimeTests.cs:14541`) snapshot+restore the three pools
under a `SuppressionGuard`. Precedent for ledger-driven in-game tests:
`ContractTombstonesAcrossSupersedeTest`, `KerbalRecoveryOnSupersedeTest`.

**Existing ledger-side observability** is ordinary structured logging, not a tracer —
no intent/truth reconcile loop. Closest existing pattern:
`KerbalLoadRepairDiagnostics` (accumulate counters → `EmitAndReset` once, bounded
samples, `HasInterestingChanges` gate), driven from `LedgerOrchestrator.cs:2386`.

---

## 8. Recommendations (priority order)

1. **Rewind read-back guard (production safety).** At the rewind apply boundary, compare
   the just-loaded quicksave economy (ground truth, §6) against the recalc target
   *before* patching; fire a loud Warn (and optionally abort the patch) on divergence.
   Turns silent career corruption into a caught, logged, abortable event. *Highest
   leverage; most self-contained.*
2. **Logging gaps 1–3** (per-subject science, tech nodes, contracts): promote per-identity
   changes to a trace-gated line. HIGH risk, currently invisible.
3. **In-game ground-truth harness:** from a career save at UT X, capture a quicksave
   snapshot S, run `RecalculateAndPatch`, and diff the recalc output against S
   independently (funds, science pool, per-subject science, rep, facility levels,
   contract/milestone sets, and the **vessel set** parsed from S's `.sfs`). The closed
   loop that doesn't exist today.
4. **`LedgerTrace`** behind a `ledgerTracing` flag (event-driven, **not** a per-frame
   probe): intent capture at each `Patch*`, read-back reconcile → Tier-C `ledger-vs-truth`
   anomaly, per-identity Tier-B change lines (folds in gaps 4–7), Tier-A `RecState`-style
   structural snapshot per recalc. Built almost entirely on existing primitives.
5. **xUnit apply-boundary tests:** add a thin live-singleton fake (or move assertions
   fully in-game) so the compute-delta → write → read-back loop is covered headlessly,
   not null-skipped under `SuppressUnityCallsForTesting`.

---

## 9. Key file index

| Concern | File:line |
|---|---|
| Apply funnel | `GameActions/KspStatePatcher.cs:39` (`PatchAll`) |
| Funds/Science/Rep apply (good logs) | `KspStatePatcher.cs:730/:157/:800` |
| Per-subject science (gap 1) | `KspStatePatcher.cs:871` |
| Tech tree (gap 2) | `KspStatePatcher.cs:377/:388` |
| Contracts (gap 3) | `KspStatePatcher.cs:2177/:1727` |
| Facility level (gap 4) | `FacilityStatePatcher.cs:109` |
| Vessel strip (gap 5) | `PostLoadStripper.cs:280` |
| Orchestrator entry | `GameActions/LedgerOrchestrator.cs:1325/:1705/:1889` |
| Recalc engine | `GameActions/RecalculationEngine.cs:144` |
| Rewind apply order (oracle) | `RewindInvoker.cs:908` |
| Tracer template | `MapRenderTrace.cs`, `MapRenderProbe.cs` |
| Log primitives | `ParsekLog.cs` (`VerboseOnChange:271`, `RecState:511`) |
| Settings flag template | `ParsekSettings.cs:56`, `UI/SettingsWindowUI.cs:458`, `ParsekSettingsPersistence.cs:46` |
| In-game runner | `InGameTests/InGameTestRunner.cs`, `RuntimeTests.cs:14541/:15573/:22661` |
| Closest E2E test | `Source/Parsek.Tests/FullCareerTimelineTests.cs` |
| ERS/ELS routing rule | `EffectiveState.ComputeERS/ComputeELS`; allowlist `scripts/ers-els-audit-allowlist.txt` |
