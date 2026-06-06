# Recalculation Pipeline Performance Plan

**Status:** Proposed (2026-04-29, revised eight times — internal Opus review + seven external reviews on 2026-04-29)
**Scope:** `LedgerOrchestrator.RecalculateAndPatch` and the `RecalculationEngine` walk it drives.
**Audience:** Implementer of the perf cycle that follows v0.8.x.

> **Revision 1 note.** First draft was reviewed by an independent Opus agent and returned an "iterate" verdict with seven concrete defects. The principal correctness bug (Item 1's "skip projection" claim — `FundsModule.GetAvailableFunds` short-circuits to `projectedAvailableFunds` when set, so dropping the projection silently returns stale legacy values) was fixed. Item 4's cache key was widened from `Ledger.StateVersion` only to a five-tuple. Items 5, 7, 8 each had an unaddressed edge case folded in. A new Item 3 (cache the ELS list allocation) was added. Implementation order was reordered so Item 4 ships before Item 6.

> **Revision 2 note.** External review found five P1/P2 defects, four of them addressed in revision 2:
> 1. **P1 — Projection buffer reused before consumed.** Fixed: two scratch buffers (`scratchLiveSort`, `scratchProjectionSort`).
> 2. **P1 — In-place sort loses stability.** Fixed in revision 2 via an `OriginalIndex` tiebreaker on a `List<ScratchEntry>` struct array; **superseded in revision 7** by a bottom-up merge-sort helper on `List<GameAction>` (preserves PrePass's mutation contract — see Item 2 active spec for the live design).
> 3. **P1 — Snapshot resume drops PrePass action mutations.** Patched in revision 2 (capture the post-PrePass timeline + cursor index). Subsequently dropped in revision 3 — see below.
> 4. **P1 — End-of-walk capture cannot represent earlier checkpoints.** Patched in revision 2 (mid-walk capture at exact cursor positions). Subsequently dropped in revision 3 — see below.
> 5. **P2 — Warp bypass scope inconsistent.** Fixed in revision 2: dropped the scope abstraction; single bool `SuppressRecalcDuringWarp` + dedicated `RecalculateAndPatchBypassingCoalescing()` entry point.

> **Revision 3 note.** Second external review found three more defects. Two of them (P1 snapshot key invalidates the commit path; P1 prefix derived fields can be stale after another cutoff resume) are not surface bugs — they are structural. Salvage options exist (split `Ledger.StateVersion` into append vs mutation generations, store per-action derived-field snapshots, audit PrePass for append-only safety, surgically split `Reset()` into walk-output-vs-PrePass-cache halves) but every salvage requires deep correctness analysis and meaningful code surgery for benefits that are not core to this plan's user-stated goals. Decision: **drop Item 4 (snapshot/checkpoint memoization) from this plan entirely.** Move to "Out of Plan / Future Work" with the design issues documented so it can be picked up properly later. The third defect (P2 — bypass entry point clears `RecalcRequested` after the walk, overwriting any nested subscriber request) is a real bug; fixed by clearing the flag *before* the walk starts.
>
> Impact of dropping Item 4: rewind and merge paths do not get the structural speedup the original plan promised. The 1 Hz warp tick still runs in bounded wall-clock time on realistic ledger sizes (a full walk on 10 000 actions is well within 1 s — measured ~10–20 ms). The user's stated goal ("during warp we should be able to update buildings state and budget values ~once per second, warping should not affect performance") is fully met by Items 1, 2, 3, 4 (the renumbered 1 Hz tick), and 5 (renumbered affordability cache). Items 6 and 7 (renumbered) handle remaining hot paths.

> **Revision 4 note.** Third external review found three more defects, all in Item 4 and the test strategy:
> 1. **P1 — Suppressed cutoff requests lose their semantics.** The original suppression branch coalesced *every* `RecalculateAndPatch` call into a flag-only `RecalcRequested = true`, but `RecalculateAndPatch` is also reachable with `utCutoff = RewindAdjustedUT / double.MaxValue`, with `bypassPatchDeferral = true`, and with `authoritativeRepeatableRecordState = true`. Coalescing such a call would silently flush as a default-args walk with the wrong cutoff and missing side-effect flags. **Fix: only default-args calls coalesce.** Cutoff/flag calls bypass the suppression check and run inline. They are rare during warp in practice (the rewind family halts warp via stock KSP behaviour first) and a one-off hitch is strictly better than silent semantic drift.
> 2. **P2 — Exit flush is said to clear a flag it never clears.** Revision 3's fix (clear `RecalcRequested` *before* the bypass walk) was correct for the bypass path but left the regular path unchanged, so the warp-exit flush walk could leave `RecalcRequested = true` and trigger a spurious tick at the start of the next warp session. **Fix: split clear policy.** Bypass entry clears before the walk (preserves nested subscriber requests for the next tick); regular entry clears after a successful walk (warp-exit flush goes here, so the next warp session starts with a clean flag).
> 3. **P3 — Fuzzer undercounts action types.** Original draft cited "18 `GameActionType` values"; the enum currently has 23 (values 0–22, including `StrategyDeactivate` and the three `*Initial` seeds). **Fix: parameterise the fuzzer to "all currently-defined values" plus a CI-gate test that pins the enum count, forcing fuzzer updates whenever a new action type is added.**

> **Revision 5 note.** Fourth external review found two more defects in revision 4's Item 4 pseudocode:
> 1. **P1 — `RecalculateAndPatch(utCutoff)` no longer sets rewind flags.** Revision 4's pseudocode hard-coded `bypassPatchDeferral=false, authoritativeRepeatableRecordState=false` in the public `RecalculateAndPatch` wrapper, silently breaking the existing `utCutoff.HasValue`-derived contract at `LedgerOrchestrator.cs:1239-1240`. `RecalculateAndPatch(RewindAdjustedUT)` and `RecalculateAndPatch(double.MaxValue)` would have run with the wrong patch-deferral and repeatable-record semantics. **Fix: keep the existing public-wrapper bodies verbatim** (preserve `utCutoff.HasValue` flag derivation) and route all wrappers through a new private `RecalculateAndPatchInternal` that adds coalescing logic without touching the flag contract.
> 2. **P2 — Cutoff walks should not clear pending default requests.** Revision 4's `RecalculateAndPatchInternal` cleared `RecalcRequested = false` after every successful non-coalesced walk, including cutoff/flag walks running inline during warp suppression. A cutoff walk doesn't satisfy a pending default-args request — they have different semantics — so clearing the flag would drop a queued 1 Hz or warp-exit flush. **Fix: gate the post-walk clear on `isDefaultArgs`.** Only a default-args (full) walk clears the pending default-args request; cutoff/flag walks leave the flag alone.

> **Revision 6 note.** Fifth external review found three more defects:
> 1. **P1 — Scratch sort buffer drops PrePass mutation contract.** Revision 1's `ScratchEntry { int OriginalIndex; GameAction Action; }` struct array would not have compiled against `PrePassAllModules`'s `List<GameAction>` parameter, and `ContractsModule.PrePass` requires a mutable sorted `List<GameAction>` it can append `ContractFail` actions to before the second sort. **Fix: keep the buffers typed as `List<GameAction>` and provide a stable in-place sort helper** (bottom-up merge sort with a reused `scratchSortAux` buffer). Stability is provided by the algorithm, not an extra tiebreaker key; comparer stays 3-key. The same `scratchLiveSort` survives across both sort calls per walk and PrePass appends to it in place.
> 2. **P1 — Projection-disabled cutoff leaves stale future fields.** Item 6's claim that "future fields stay at reset baseline" was wrong — `RecalculationEngine.ResetDerivedFields` resets only the cutoff-filtered list, not the excluded suffix. With projection ON, the projection walk overwrites suffix fields; with projection OFF, the suffix retains stale values from a prior walk. (Patched in revision 6 via "reset full list"; superseded in revision 7 — see below.)
> 3. **P2 — Bypass smoke test contradicts clear-before policy.** The earlier `BypassEntryPoint_RunsWalkEvenWhenSuppressed` test text was ambiguous about which flag it asserts on — read literally, it implied `RecalcRequested` should still be `true` after the bypass walk, which would force the implementation back toward the stale-flag behaviour the v3/v4 fixes removed. **Fix: clarify the test** — assert `SuppressRecalcDuringWarp` stays `true` (bypass doesn't toggle suppression) AND `RecalcRequested` is `false` after (cleared before walk; no nested subscriber re-set it).

> **Revision 7 note.** Sixth external review found two more defects, both real:
> 1. **P1 — Stale `ScratchEntry` instructions remained in the active spec.** Revision 6 added the corrected `List<GameAction>` design but left the prior `List<ScratchEntry>` declaration block, the `OriginalIndex` references in Files-touched / Risk wording, and the global risk-table row about `OriginalIndex` overflow. An implementer reading the active spec would see two contradicting declarations and likely build the broken struct-array version. **Fix: scrub all `ScratchEntry` / `OriginalIndex` references from the active spec.** Item 2's declaration block now only shows the four `List<GameAction>` buffers + the `ActionComparer` singleton; Files-touched and Risk wording reference `StableSort` and the merge-sort helper; the global risk-table row about overflow is replaced with one about non-deterministic ordering on equal keys (eliminated by the stable merge sort). Revision-2 note also updated to mark the `OriginalIndex` approach as superseded.
> 2. **P1 — `ResetDerivedFields` does not make excluded actions inactive.** Revision 6's "reset full list when `runProjection: false`" fix was structurally correct but used the wrong helper. The existing `ResetDerivedFields` baseline (per `RecalculationEngine.cs:464-471`) sets `Effective = true` and seeds `Transformed*Reward = *Reward` — defaults appropriate for actions about to be dispatched, but NOT for actions that will neither be dispatched nor projected over. Career State / Timeline both gate display on `Effective`, so excluded future actions would still render as **active with their original rewards**, not as "didn't happen". **Fix: dedicated `ResetExcludedDerivedFields(actions, cutoff)` helper** that sets `Effective = false`, `Transformed*Reward = 0f`, and zeros the other derived fields. Distinct semantics from the dispatch-baseline reset, called only on the rare `runProjection: false` cutoff path. Tests renamed to `*_AreInactivated` (was `*_AreResetToBaseline`) and explicitly assert `Effective = false`.

> **Revision 8 note.** Final clean Opus review verdict was ITERATE (not blocking, but non-trivial). Folded fixes:
> - **File-path corrections.** `EffectiveState.cs` and `KerbalsModule.cs` live at `Source/Parsek/`, not under `Source/Parsek/GameActions/`. §10 references and Item 3's Files-touched bullet updated.
> - **Item 3 collapse.** `EffectiveState.ComputeELS` already returns `IReadOnlyList<GameAction>` (`Source/Parsek/EffectiveState.cs:523`), so step 3a is a no-op verification. Item 3 reduces to the orchestrator-side wrapper-allocation drop.
> - **Item 4 4d insertion-point disambiguation.** Set `SuppressRecalcDuringWarp = true` BEFORE the `if (LedgerOrchestrator.IsInitialized)` gate at `ParsekFlight.cs:6835`, not inside it. Suppression must hold even when the orchestrator initialises mid-warp.
> - **Item 6 scope tightening.** `RewindInvoker.cs:643` (`RecalculateAndPatch(double.MaxValue)`) is excluded from Item 6 — the cutoff is not selective and the path is followed by a player-visible UI-consumed walk. `MergeJournalOrchestrator` site requires explicit citation in the implementation PR.
> - **Test-coverage gaps pinned.** Five new tests added in §6: `CutoffWalk_AllocationsBounded` (projection-walk module-clone allocation), `RegularEntry_OnTimelineDataChangedReentry_DoesNotCorrupt` (orchestrator-level reentrancy outside `walkInProgress`'s scope), `WarpRecalcCadence_NoOverheadOutsideWarp` (Update-loop overhead), `KerbalsModuleTests.RepeatedWalks_SlotCountStable` (`slots` field walk-stability under high cadence). Risk table grew correspondingly.
> - **Implementation-order dependency clarification.** §5 now explicitly states Item 4 has no structural dependency on Items 1–3 (correctness rests on flag + private layer + handler edits, not on sort/buffer code) and Item 5 has no dependency on Item 3 (`ComputeELS` already returns `IReadOnlyList`).

---

## 1. Background

The audit in the conversation that produced this doc established the following ground truth — citations are file:line in the current tree.

**The ledger walk is always full UT=0 → end.** The `utCutoff` parameter at `RecalculationEngine.cs:144` filters which actions reach the walker but does not let it skip the early ledger; modules are reset to baseline at `RecalculationEngine.cs:385` and the walk starts over from the first action every call. There is no memoization of walk results.

**The walk is invoked from 14 call sites** with these cutoffs:

| Cutoff | Site | Trigger |
|---|---|---|
| `null` (full) | LedgerOrchestrator.cs:281 | `OnRecordingCommitted` |
| `null` (full) | LedgerOrchestrator.cs:1820 | `OnKscSpending` |
| `null` (full) | LedgerOrchestrator.cs:2290 | `OnFacilityUpgraded` |
| `null` (full) | LedgerOrchestrator.cs:2319, 2346, 2550 | Deferred reconcilers |
| `null` (full) | ParsekFlight.cs:6849 | Warp-exit handler |
| `null` (full) | ParsekScenario.cs:251, 4336, 4375 | OnLoad seed paths |
| `RewindAdjustedUT` | ParsekScenario.cs:1647, 2112, 4429 | Rewind paths |
| `double.MaxValue` | RewindInvoker.cs:643 | Rewind session finish |
| `Planetarium.GetUniversalTime()` | LedgerOrchestrator.cs:3046 | `CanAffordScienceSpending` |
| `Planetarium.GetUniversalTime()` | LedgerOrchestrator.cs:3080 | `CanAffordFundsSpending` |

> **Verification note for the implementing PR:** spot-check each line citation against `git log` at the moment the first PR is opened. The observability-audit branch in flight (memory: `project_logging_audit_in_flight.md`) is moving log-call lines around and may have shifted these by ±10 lines.

**Time warp behaviour:**
- During warp itself: `BackgroundRecorder.OnBackgroundPhysicsFrame` early-returns on packed BG vessels; no recalc is gated on UT progression. **However**, ledger-mutating events that fire during warp (KSC spending, facility upgrades completing, deferred reconcilers landing one frame later, the warp-exit handler itself) each trigger their own full RecalculateAndPatch via the call sites above. At a high warp multiplier with several such events this is the dominant per-second cost.
- On warp-start (`ParsekFlight.cs:6832-6841` — branch starts at the `!wasWarpActive && isWarpNow` guard at `:6832`; `KspStatePatcher.PatchFacilities` call is at `:6837`): only the facility-visual refresh runs. Funds, science, reputation, kerbal returns from contract completions, and contract-timer expiries are **not** updated mid-warp.
- On warp-exit (`ParsekFlight.cs:6849`): full `RecalculateAndPatch()` with no cutoff.

**What is already cached.** Three independent caches in `EffectiveState.cs` (lines 31–42) keyed on monotonic version counters:
- `ersCache` — committed/visible/non-superseded recordings (`StoreVersion + SupersedeVersion + MarkerIdentity`)
- `elsCache` — ledger actions minus tombstones (`LedgerVersion + TombstoneVersion`)
- `suppressionCache` — session-suppressed recording IDs (`StoreVersion + MarkerIdentity`)

These cache *the inputs* to the walk; they do not cache the walk itself, and the orchestrator allocates a fresh `List<GameAction>` from the cached ELS on every call (`LedgerOrchestrator.cs:1594`).

**Five version counters together define walk-result validity.** Any snapshot or memoization scheme MUST key on the full set, because they bump independently:

| Counter | Bumps when | Read by |
|---|---|---|
| `Ledger.StateVersion` (`Ledger.cs:29`) | Action add/remove, supersede-relation mutation, tombstone-list mutation | ELS cache |
| `ParsekScenario.TombstoneStateVersion` (`ParsekScenario.cs:54`) | Tombstone applied/lifted | ELS cache |
| `ParsekScenario.SupersedeStateVersion` (`ParsekScenario.cs:53`) | Supersede subtree flip (Immutable / CommittedProvisional) | ERS cache |
| `RecordingStore.StateVersion` | Recording committed/discarded/visibility flip | ERS cache |
| `ReFlySessionMarker` identity | Re-Fly session start/end | ERS + suppression caches |

`Ledger.StateVersion` alone is **not** a sufficient cache key — a tombstone-only bump (only `TombstoneStateVersion` advances) leaves `Ledger.StateVersion` alone but changes ELS output. Likewise, `KerbalsModule.PrePass` reads `RecordingStore.CommittedRecordings` (`KerbalsModule.cs:157`), so a recording-store mutation invalidates module state without touching either ledger counter.

**What is not cached.**
- The walk result. Modules reset to baseline every call (`RecalculationEngine.cs:385`).
- The ELS list itself. `BuildRecalculationActions` (`LedgerOrchestrator.cs:1594`) allocates a fresh `List<GameAction>` from `EffectiveState.ComputeELS()` every walk, even when the underlying ELS is cache-hit and unchanged.
- `SortActions` — runs twice per walk (once at line 382, once at line 398 after `PrePassAllModules` may inject `ContractFail`s) using LINQ `OrderBy/ThenBy/.ToList()`.
- The cutoff-filtered list at `RecalculationEngine.cs:160` allocates a fresh `List<GameAction>` each call.
- The projection walk (`RecalculationEngine.cs:186`) clones every cloneable module and re-walks the full timeline once more, including for both affordability probes.

**Cost shape of one walk** for `N` actions, `M` modules:

| Step | Cost |
|---|---|
| Build ELS (`LedgerOrchestrator.cs:1594`) | O(N) + 1 list alloc |
| Cutoff filter (if cutoff) | O(N) + 1 list alloc |
| Sort #1 | O(N log N) + 1 list alloc |
| Reset modules + derived fields | O(N + M) |
| Pre-pass + Sort #2 | O(N log N) + 1 list alloc |
| Walk loop | N × (8 dispatches average) |
| Projection walk (cutoff path) | doubles every step above + module deep-clone |

For ~10 000 ledger actions: ~160 000 `ProcessAction` calls + 4 list allocations per cutoff walk, doubled to ~320 000 + module clone for affordability probes.

**Module reentrancy.** The walk fires `OnTimelineDataChanged?.Invoke()` at `LedgerOrchestrator.cs:1577` synchronously inside the recalc-and-patch sequence. Subscribers of this event include UI rebuilds that may, in turn, call `RecalculateAndPatch` again (e.g., a timeline window asking for fresh affordability values during its repaint). Today this is technically reentrant but works because the inner walk is just slow, not destructive. Any optimisation that introduces shared scratch state or coalescing flags must explicitly handle reentrancy or the inner walk corrupts the outer.

---

## 2. Goals

1. **Walk-cost reduction.** Eliminate redundant work on the hot paths (commit, affordability, warp-exit, post-warp loops).
2. **Bounded warp cost.** Make per-wall-clock-second cost during time warp independent of the warp multiplier and of the count of ledger-mutating events that fire while warp is active. Target: at most one walk-and-patch per wall-clock second, regardless of warp rate, regardless of how many `OnKscSpending` / `OnFacilityUpgraded` / deferred-reconciler triggers landed in that second.
3. **Live mid-warp UI.** The KSC top-bar resources (funds, science, reputation) and facility visual state must reflect ledger-driven changes within ~1 wall-clock second of the underlying event firing during warp, not jump-cut on warp-exit. (Today funds/science/rep stay frozen mid-warp and snap on exit.)
4. **Correctness preserved.** No regressions to: tombstone semantics, rewind-adjusted cutoff walks, post-walk reconciliation, the cutoff-walk projection (it powers `Effective*Reward` / `EffectiveScience` UI fields *and* feeds `GetAvailable*` short-circuits), or the slot-limit "one walk behind" convergence.

---

## 3. Non-Goals

- Architectural rework of `RecalculationEngine`'s tier system, module interface, or pre-pass/post-walk contracts. Modules already do the right thing — only the harness around them changes.
- Replacing `OrderBy/ThenBy` LINQ chains in non-hot paths (sort optimisation is scoped to the engine's own dual sort; orchestrator-level sorts elsewhere are out of scope).
- Caching `EffectiveState.ComputeELS` / `ComputeERS` themselves — they already cache. (We do, however, cache the *list-wrapper allocation* the orchestrator builds from them — see Item 3.)
- Changing the 14 call sites' triggers; they fire at the same moments, only their cost changes.
- Multi-threading any part of the walk. Single-threaded throughout, but with per-action allocation pressure removed.
- Snapshot or checkpoint memoization of walk results. The original draft proposed this as the structural rewind/merge win; two external reviews surfaced correctness defects that forced dropping it. See §9 Future Work for the full write-up of the issue and the salvage paths.

---

## 4. Work Items

Seven items, ordered by ROI **and** correctness-stack-up. Each has a self-contained acceptance test so they can ship one PR at a time. The order has been chosen so that risky items land on top of stabilised foundations.

### Item 1 — Conditional second sort

**Problem.** `RecalculationEngine.cs:398` re-sorts after `PrePassAllModules` because `ContractsModule` may inject synthetic `ContractFail` actions for expired deadlines. Most walks don't inject anything, so the second `OrderBy/ThenBy/.ToList()` is wasted work.

**Approach.** `IResourceModule.PrePass` returns a `bool didMutateActionList` (today returns `void`). The engine ORs the eight return values; only re-sorts if any module returned true.

**Files touched.**
- `Source/Parsek/GameActions/IResourceModule.cs`: change `void PrePass(List<GameAction>, double?)` → `bool PrePass(List<GameAction>, double?)`. Default modules return `false`.
- `Source/Parsek/GameActions/RecalculationEngine.cs`: `PrePassAllModules` returns `bool`; `RunWalk` skips the sort #2 when false.
- All eight module files: update `PrePass` signature; only `ContractsModule` returns true (and only when it actually injected — read by checking the action count delta or maintaining its own flag).

**Tests.**
- `RecalculationEngineTests.PrePassNoInjection_SkipsSecondSort` — register a sentinel sortable that records sort calls; assert one call when no module injects.
- `RecalculationEngineTests.PrePassWithInjection_DoesSecondSort` — `ContractsModule` injects a `ContractFail` for an expired deadline; assert two sort calls.

**Risk.** Trivial. The signature change is mechanical; the only correctness concern is forgetting to flip the bit in `ContractsModule`, which a unit test catches.

**Acceptance signal.** Walks with no contract-deadline expiry shave one O(N log N) sort + one list allocation. Measurable on the synthetic 10 000-action benchmark.

---

### Item 2 — Reusable scratch buffers (two buffers + stable-sort tiebreaker)

**Problem.** Every walk allocates several `List<GameAction>` instances:
- `RecalculationEngine.cs:160` (cutoff filter)
- `SortActions` (`RecalculationEngine.cs:269`) returns a fresh list from LINQ `.ToList()` — twice per walk
- `RunProjectionWalk` allocates a copy of the action list at `:187` (`CopyNonNullActions`) plus its own internal sort
- `ApplyProjectedAvailability`'s timeline parameter is the projection's sorted output

In a sustained warp at 1 Hz with 10 000-action ledgers, the GC churn from these is observable.

**Approach.**

**2a. Two scratch sort buffers — not one.** External review caught this. The current cutoff flow at `RecalculationEngine.cs:186-194` runs:

```
projectedTimeline = RunProjectionWalk(...).Sorted   // step 1 — populates one buffer
walk             = RunWalk(effective, utCutoff)     // step 2 — sorts again
ApplyProjectedAvailability(projectedTimeline, ...)  // step 3 — reads step 1's output
```

If steps 1 and 2 share a single scratch sort buffer, step 2 clobbers `projectedTimeline` before step 3 consumes it — silently turning every cutoff walk's `projectedAvailableFunds` / `projectedAvailableScience` into garbage and breaking affordability. Fix: two distinct static buffers, both typed as `List<GameAction>` so PrePass can mutate them in place (append synthetic `ContractFail` actions for expired deadlines):

```csharp
private static readonly List<GameAction>  scratchLiveSort       = new();   // RunWalk works in here; PrePass appends in here
private static readonly List<GameAction>  scratchProjectionSort = new();   // RunProjectionWalk works in here
private static readonly List<GameAction>  scratchEffective      = new();   // cutoff filter target
private static readonly List<GameAction>  scratchSortAux        = new();   // shared aux buffer for StableSort
private static readonly ActionComparer    actionComparer        = new();   // 3-key comparer (UT, category, Sequence)
```

The projection buffer is ALSO accessed by `ApplyProjectedAvailability` after `RunWalk` returns — that's fine because `RunWalk` only touches `scratchLiveSort`.

**2b. Stable in-place sort on `List<GameAction>`.** LINQ `OrderBy/ThenBy` is stable; `List<T>.Sort` (and `Array.Sort`) use introsort and are NOT stable. Many `GameAction`s share full sort keys: same UT, same earnings/spending category, same `Sequence` (defaults to 0 for several generated action types and resets per conversion batch). A non-stable sort would produce a different walk order on every call, leading to non-deterministic `Effective*Reward` / `EffectiveScience` values for ties.

The buffer MUST stay typed as `List<GameAction>` because `PrePassAllModules` (and specifically `ContractsModule.PrePass`) takes a `List<GameAction>`, scans it in sorted order, reads the last action's UT for "now", and **appends synthetic `ContractFail` actions to the same list** before the second sort. The first draft of this item proposed a `List<ScratchEntry>` struct array with an `OriginalIndex` tiebreaker, which would have either failed to compile against the PrePass signature or tempted the implementer to lose the deadline-injection contract. **Use a stable algorithm directly on `List<GameAction>` instead.**

The stable sort is a **bottom-up merge sort** helper:

```csharp
internal static void StableSort(List<GameAction> list, IComparer<GameAction> cmp);
```

Bottom-up merge sort is O(N log N) — same asymptotic cost as introsort but naturally stable, requires only an O(N) auxiliary buffer (reused via `scratchSortAux`), and out-performs introsort on already-mostly-sorted inputs (the common case after PrePass). Comparer stays 3-key (UT, category, `Sequence`) — stability is provided by the algorithm, not an extra tiebreaker key, so `Sequence`'s natural meaning is preserved.

The same `scratchLiveSort` buffer survives across the engine's two sort calls per walk: first sort fills it from ELS; PrePass mutates it (appends `ContractFail` entries); second sort re-sorts it in place.

**2c. Reentrancy & threading.** Static scratch lists are not thread-safe **and** not reentrant-safe. Add two assertions to `RunWalk`:
1. `ThreadId` assertion (`#if DEBUG`): throws if the engine is invoked from two threads. Walk is single-threaded today.
2. **Reentrancy guard bool** (always-on, not debug-only — corruption is silent and severe): a `private static bool walkInProgress` flag set on entry to `RunWalk`, cleared on exit (try/finally). If a walk reenters (e.g., via `OnTimelineDataChanged` subscriber), the inner call throws `InvalidOperationException` immediately. Better to surface as a crash than silently corrupt scratch buffers. Document this contract loudly and pair it with the §5 pre-Item-5 audit of `OnTimelineDataChanged` subscribers.

**Files touched.**
- `Source/Parsek/GameActions/RecalculationEngine.cs`: introduce the four scratch `List<GameAction>` buffers (`scratchLiveSort`, `scratchProjectionSort`, `scratchEffective`, `scratchSortAux`) and the `ActionComparer` singleton, all per the declarations in 2a. Add the `StableSort(List<GameAction>, IComparer<GameAction>)` bottom-up merge-sort helper using `scratchSortAux`. `Clear()` the buffers before each fill. Add the two assertions from 2c. `SortActions` becomes a private helper that fills the appropriate buffer from the input and calls `StableSort` in place; callers iterate the buffer directly as a `List<GameAction>`.

**Tests.**
- `RecalculationEngineTests.StableSort_PreservedOnIdenticalFullKeys` — feed actions with **identical UT, identical category (all earnings or all spendings), identical `Sequence`** in a known input order; assert post-sort order matches input order. Without a stable algorithm, introsort would reorder ties non-deterministically and this test would fail intermittently.
- `RecalculationEngineTests.StableSort_LinqEquivalence` — generate 1000 random actions where ~30% share full sort keys; assert the merge-sort output matches `actions.OrderBy(...).ThenBy(...).ThenBy(...).ToList()` element-for-element.
- `RecalculationEngineTests.PrePassMutation_RoundtripsThroughBuffer` (new per review v6) — feed an action set into `scratchLiveSort` via the engine's normal flow; let `ContractsModule.PrePass` append a synthetic `ContractFail` to the list; verify (a) the second sort places the injected action correctly, (b) the buffer is the same `List<GameAction>` instance across both sorts, and (c) the appended action survives the second sort and gets dispatched.
- `RecalculationEngineTests.CutoffWalk_ProjectionBufferNotClobbered` — run a cutoff walk on a timeline with future `FundsSpending` actions; assert `fundsModule.GetAvailableFunds()` after the walk reflects the future-spending reservation (i.e., `< currentBalance`). Without two buffers, this returns `currentBalance` because `projectedAvailableFunds` is overwritten by `RunWalk`'s sort and `SetProjectedAvailable` ends up reading garbage.
- `RecalculationEngineTests.RepeatedWalk_NoListAllocations` — invoke walk 100x; assert with a custom `GC.GetAllocatedBytesForCurrentThread` delta that the per-walk allocation is below a threshold (e.g., < 1 KB after first walk warms up). Sort aux buffer is reused, no per-walk allocation expected after warmup.
- `RecalculationEngineTests.ReentrantWalk_Throws` — install a fake module whose `ProcessAction` invokes `Recalculate` again; assert `InvalidOperationException`.

**Risk.** Low-medium. The two-buffer separation is mechanical. The stable-sort fix has a single subtle correctness gate (the merge-sort algorithm must be implemented stably — equal-key elements must preserve insertion order across the merge step) that the test design above catches via `StableSort_PreservedOnIdenticalFullKeys` and `StableSort_LinqEquivalence`.

**Acceptance signal.** GC.Alloc per walk (10 000 actions, no cutoff) drops to near-zero after warmup; cutoff-walk affordability values agree with from-scratch values element-for-element across the fuzzer.

---

### Item 3 — Cache the ELS list allocation (NEW per review)

**Problem.** `BuildRecalculationActions` (`LedgerOrchestrator.cs:1594`) does `new List<GameAction>(EffectiveState.ComputeELS())` on every walk. Even when `ComputeELS()` is a cache hit (no `Ledger.StateVersion` or `TombstoneStateVersion` change), the orchestrator allocates a fresh wrapper list. With the 1 Hz warp tick (Item 4) on a 10 000-action save, this is a per-second 80 KB allocation and copy that has zero functional purpose.

**Approach.** Two parts.

3a. Have `EffectiveState.ComputeELS()` return `IReadOnlyList<GameAction>` (it already does internally; just confirm the signature exposes it).

3b. Change `RecalculationEngine.Recalculate(List<GameAction>, ...)` to accept `IReadOnlyList<GameAction>`. The engine no longer needs `List<T>` mutability since the cutoff-filter and sort steps already copy into the scratch buffer (Item 2). The orchestrator passes the cached ELS directly with no wrapper allocation.

**Files touched.**
- `Source/Parsek/EffectiveState.cs`: already returns `IReadOnlyList<GameAction>` at line 523 — verified, no change needed in this file. (The wrapper allocation is purely on the orchestrator side.)
- `Source/Parsek/GameActions/RecalculationEngine.cs`: change signature `Recalculate(IReadOnlyList<GameAction>, double?, bool runProjection = true)`. Update internal helpers accordingly.
- `Source/Parsek/GameActions/LedgerOrchestrator.cs:1594`: drop the wrapper allocation; pass `EffectiveState.ComputeELS()` directly.
- `Source/Parsek/GameActions/LedgerOrchestrator.cs:3044, 3078` (affordability probes): same change.

**Tests.**
- `LedgerOrchestratorTests.RepeatedWalk_NoElsWrapperAllocation` — invoke `RecalculateAndPatch()` twice with no ledger mutation between; assert the second call's allocation delta excludes the wrapper list.

**Risk.** Trivial. ELS is already immutable from the consumer's perspective; surfacing that in the signature is a tightening, not a loosening.

**Acceptance signal.** Per-walk allocation when ELS unchanged drops by `~16 bytes/action` (8B reference + amortised list-header overhead).

---

### Item 4 — 1 Hz wall-clock recalc tick during warp

**Problem stated by the user.** During time warp, the game's top-bar funds/science/reputation values and facility visual state should refresh at most once per wall-clock second, independent of the warp multiplier. If a recalc takes longer than 1 s, the next tick is delayed (not skipped). The cadence must not be coupled to game-time: at 100 000× warp, an in-game month elapses in ~26 s of wall-clock time but the player must still see only ~26 ticks, not "many thousands of recalcs as events fire".

**Approach.** Four parts.

**4a. Single bool + dedicated bypass entry point — no scope abstraction.** External review v2 caught that the original "SuspendScope" design was self-contradictory (Push-on-construct increments suppression, but the tick wants suppression *off* for its own call; Pop-on-construct opens the gate for nested subscriber calls too, which we don't want). The clean primitive:

```csharp
// LedgerOrchestrator state
internal static bool SuppressRecalcDuringWarp;       // single bool, no nesting
internal static bool RecalcRequested;                // any default-args call during suppression sets this

// Public entry points — PRESERVE the existing flag contract from
// LedgerOrchestrator.cs:1235-1255 verbatim. Do NOT change which flags get derived from
// which inputs; this revision only adds the warp-coalescing wrapper around them.
internal static void RecalculateAndPatch(double? utCutoff = null)
{
    // Existing contract (LedgerOrchestrator.cs:1239-1240): a cutoff call IS a rewind-style
    // walk, so both flags follow utCutoff.HasValue. Reviewing this if/when adding new
    // entry points: pass the flags through explicitly rather than deriving from utCutoff
    // here, because not every cutoff caller wants rewind side-effects (see
    // RecalculateAndPatchForPostRewindFlightLoad below — same utCutoff, different flags).
    RecalculateAndPatchInternal(
        utCutoff,
        bypassPatchDeferral: utCutoff.HasValue,
        authoritativeRepeatableRecordState: utCutoff.HasValue);
}

internal static void RecalculateAndPatchForPostRewindFlightLoad(double utCutoff)
{
    // Existing contract (LedgerOrchestrator.cs:1252-1255): post-rewind flight-load uses a
    // cutoff but preserves live-tree patch deferral and same-branch repeatable records.
    RecalculateAndPatchInternal(
        utCutoff,
        bypassPatchDeferral: false,
        authoritativeRepeatableRecordState: false);
}
// ... (any other public wrappers that take explicit flags route through the same Internal)

private static void RecalculateAndPatchInternal(
    double? utCutoff,
    bool bypassPatchDeferral,
    bool authoritativeRepeatableRecordState)
{
    // Coalesce ONLY default-args calls. Calls with a cutoff or any non-default flag
    // carry semantics the simple `RecalcRequested = true` flag cannot represent
    // (e.g., `RewindAdjustedUT`, `bypassPatchDeferral=true`, `authoritativeRepeatableRecordState=true`),
    // and the eventual flush walk would silently substitute default semantics.
    bool isDefaultArgs = !utCutoff.HasValue
        && !bypassPatchDeferral
        && !authoritativeRepeatableRecordState;

    if (SuppressRecalcDuringWarp && isDefaultArgs)
    {
        RecalcRequested = true;
        ParsekLog.VerboseRateLimited("warp-coalesce-drop", ...);
        return;
    }

    // Either suppression is off, or this is a non-coalesceable call (cutoff/flags).
    // Non-coalesceable calls during warp run immediately and are correct by construction
    // (their own cutoff + flags are honoured), at the cost of a one-off warp hitch.
    // In practice, those callers (RewindInvoker, post-rewind flight load) already halt
    // warp via stock KSP behaviour before calling, so warp+cutoff overlap is rare.
    RecalculateAndPatchCore(utCutoff, bypassPatchDeferral, authoritativeRepeatableRecordState);

    // Only a default-args (full) walk satisfies a pending default-args request. Cutoff or
    // flagged walks have different semantics (filtered actions, rewind side-effects, or
    // patch-deferral preservation) and DO NOT represent the "current full state" the
    // coalesced caller wanted. If we cleared after a cutoff walk, the next 1 Hz tick or
    // warp-exit flush would skip the pending default-args request and the player would
    // see stale top-bar resources until the next event landed.
    //
    // P2 fix from external review v5: gate this clear on isDefaultArgs.
    if (isDefaultArgs)
        RecalcRequested = false;
}

internal static void RecalculateAndPatchBypassingCoalescing()
{
    // Read-and-clear BEFORE the walk so any nested subscriber call that sets
    // RecalcRequested=true during the walk remains queued for the next tick.
    // Order matters: the post-walk-clear pattern would overwrite the nested request.
    RecalcRequested = false;

    // Walk runs in the same configuration as a default-args RecalculateAndPatch — the
    // tick's job is to flush coalesced default-args requests. Calls with cutoffs/flags
    // never coalesce (see RecalculateAndPatchInternal above), so they cannot be queued here.
    RecalculateAndPatchCore(
        utCutoff: null,
        bypassPatchDeferral: false,
        authoritativeRepeatableRecordState: false);

    // Do NOT clear RecalcRequested here. Nested subscriber requests set during the walk
    // remain queued for the next 1 Hz tick.
}
```

**Coalesce eligibility — only default-args calls (P1 fix from external review v4).** External review v4 caught that the original suppression branch only stored a boolean `RecalcRequested = true`, losing the semantics of any in-flight call with a cutoff or special flag. Concretely: `RewindInvoker.cs:643` passes `double.MaxValue` (which through `RecalculateAndPatch(utCutoff)` derives `bypassPatchDeferral=true, authoritativeRepeatableRecordState=true`); `RecalculateAndPatchForPostRewindFlightLoad` passes `utCutoff` with `bypassPatchDeferral=false, authoritativeRepeatableRecordState=false`. If any of those fired while `SuppressRecalcDuringWarp == true`, the eventual flush would run a default-args walk that silently dropped the cutoff and the side-effect flags. **Fix: only default-args calls coalesce.** Cutoff/flag calls bypass the suppression check and run inline. They are rare during warp in practice — `RewindInvoker` and `RecalculateAndPatchForPostRewindFlightLoad` already halt warp via stock KSP behaviour before invoking — and a one-off hitch on a rare path is strictly better than silent semantic drift.

**Public-wrapper flag contract preserved (P1 fix from external review v5).** External review v5 caught that an earlier draft of this revision hard-coded `bypassPatchDeferral=false, authoritativeRepeatableRecordState=false` in `RecalculateAndPatch`, which would have silently broken `RecalculateAndPatch(RewindAdjustedUT)` and `RecalculateAndPatch(double.MaxValue)` — both rely on the existing `utCutoff.HasValue`-derived flags at `LedgerOrchestrator.cs:1239-1240`. **Fix: keep the existing wrapper bodies verbatim** and route them through the new internal entry point. Only the internal layer adds coalescing logic; the public contracts at all 14 call sites are unchanged.

**Why two entry points instead of a scope.** The flag's intent is "any default-args trigger during warp coalesces". The tick is itself a trigger but a privileged one — the very purpose of the tick is to flush the coalesced request. So the tick goes through `RecalculateAndPatchBypassingCoalescing()` which runs unconditionally. Critically, the flag stays set throughout that call, so any nested subscriber calls (e.g., `OnTimelineDataChanged` at `LedgerOrchestrator.cs:1577` triggering a UI rebuild that calls `RecalculateAndPatch`) take the suppressed branch and merely set `RecalcRequested = true` — they do NOT trigger their own expensive walks.

This eliminates the reentrancy hazard entirely without needing a counter. It also matches Item 2's reentrancy guard: if a subscriber tries to re-enter `RunWalk` directly (not through the orchestrator), the engine's `walkInProgress` bool throws.

**`RecalcRequested` clear ordering — split policy (combined P2 fixes from external reviews v3 + v4).** Two entry points, two clear-policies, both correct:
- **Bypass entry point (warp tick only):** clears `RecalcRequested = false` BEFORE the walk; does NOT clear after. Reasoning: a subscriber to `OnTimelineDataChanged` may set `RecalcRequested = true` mid-walk to request a follow-up tick. If the bypass cleared after the walk, the nested subscriber's request would be silently overwritten. Clearing first means the nested request stays queued for the next 1 Hz tick.
- **Regular entry point (everything else, including warp-exit flush):** runs the walk; clears `RecalcRequested = false` AFTER a successful walk completes. Reasoning: any default-args request the regular entry serves is fully satisfied by the just-finished walk — no follow-up tick is needed because the walk dispatched all current actions. Without this post-walk clear, a stale `RecalcRequested = true` flag from a coalesced call during the just-ended warp would survive the warp-exit flush and trigger a spurious tick at the start of the next warp session (P2 from external review v4).

**4b. Warp-state gate (pause-aware).** The tick fires when **all** of:
1. `IsAnyWarpActive()` returns true (warp index > 0 OR rate > 1).
2. `Time.timeScale > 0.01f` — pattern already used at `ParsekFlight.cs:17854`. **This is the pause gate.** `Time.unscaledTime` advances during pause; `IsAnyWarpActive` stays true during a paused warp; without this gate the 1 Hz tick would fire on a paused warp and mutate top-bar values while the player believes the world is frozen.
3. `RecalcRequested == true` (something asked for a recalc since the last tick).
4. `!warpRecalcInFlight` (the previous tick's walk has returned).
5. `Time.unscaledTime - lastWarpRecalcWallClockTime >= 1.0f` (one wall-clock second has passed).

**4c. Warp tick driver.** A new `Update()`-driven tick lives in `ParsekFlight.cs` near the existing per-frame controllers:

```csharp
// In ParsekFlight.Update():
bool warpActive = IsAnyWarpActive();
bool worldPaused = Time.timeScale < 0.01f;
if (warpActive && !worldPaused
    && LedgerOrchestrator.RecalcRequested
    && !warpRecalcInFlight
    && Time.unscaledTime - lastWarpRecalcWallClockTime >= 1.0f)
{
    warpRecalcInFlight = true;
    try
    {
        // Bypass entry point: ignores SuppressRecalcDuringWarp for THIS call.
        // Nested subscriber calls during the walk still see suppression and coalesce.
        LedgerOrchestrator.RecalculateAndPatchBypassingCoalescing();
        lastWarpRecalcWallClockTime = Time.unscaledTime;
    }
    finally
    {
        warpRecalcInFlight = false;
    }
}
```

`Time.unscaledTime` is wall-clock, advances at 1.0 s/s regardless of warp rate or pause-`timeScale`. Already used in `ParsekFlight.cs:1956/1960/7679/7683`.

**4d. Warp-start / warp-exit lifecycle.** Existing handlers at `ParsekFlight.cs:6820-6860`:

- **Warp start** (`!wasWarpActive && isWarpNow`): existing `KspStatePatcher.PatchFacilities(...)` stays. Add: `LedgerOrchestrator.SuppressRecalcDuringWarp = true` and reset `lastWarpRecalcWallClockTime = Time.unscaledTime` (forces a fresh 1-second window — handles rapid warp toggle). **Insertion point: BEFORE the `if (LedgerOrchestrator.IsInitialized)` gate at `:6835`.** Suppression must hold even when the orchestrator initialises mid-warp; placing the assignment inside the gate would leave a window where ledger-mutating events that fire before initialisation could trigger a non-coalesced walk.
- **Warp exit** (`wasWarpActive && !isWarpNow`): existing `RecalculateAndPatch()` stays. **Order matters**: clear `LedgerOrchestrator.SuppressRecalcDuringWarp = false` BEFORE the recalc call, so the flush walk runs unsuppressed via the regular entry point. The regular entry's post-walk clear pattern (see 4a) clears `RecalcRequested = false` automatically when the walk completes — guaranteeing the next warp session starts with a clean flag and resource numbers are exact at warp-exit, not just exact-modulo-1-second.

**Edge case: nested warp sub-events.** Some KSP setups can fire `OnTimeWarpRateChanged` mid-warp without a true exit (e.g., physics warp transitions). The handler must only flip `SuppressRecalcDuringWarp` on the actual `wasWarpActive != isWarpNow` boundary, not on every rate change — which is exactly what the existing `:6832` and `:6844` guards already do. Test: `WarpRecalcCadence_RateChangeDuringWarp_DoesNotFlushUnnecessarily`.

**What gets patched at 1 Hz.** Everything `ApplyRecalculatedStateToKsp` (`LedgerOrchestrator.cs:1642`) already patches outside warp:
- `kerbalsModule.ApplyToRoster` (kerbal status changes from contract completions / KIA tombstones reaching their UT)
- `KspStatePatcher.PatchAll` (funds, science, reputation, milestones, facilities, contracts, tech-tree-targeted-only-on-cutoff)

All wrapped in `SuppressionGuard.ResourcesAndReplay()` and idempotent. The 1 Hz tick is structurally identical to the warp-exit flush, just running every wall-clock second instead of once at exit.

**Tech-tree gating works correctly mid-warp.** `LedgerOrchestrator.cs:1660-1678` gates tech-tree patching on `utCutoff.HasValue`. The warp tick passes no cutoff, so tech-tree mutations are skipped — exactly the desired behaviour.

**Files touched.**
- `Source/Parsek/GameActions/LedgerOrchestrator.cs`: add `SuppressRecalcDuringWarp` (bool), `RecalcRequested` (bool), the early-return branch in `RecalculateAndPatch`, and the new `RecalculateAndPatchBypassingCoalescing` entry point. Keep all 14 existing call sites unchanged.
- `Source/Parsek/ParsekFlight.cs`: add the `Update`-side tick driver. Modify warp-start branch (`:6832-6841`; insertion point per 4d is BEFORE the `IsInitialized` gate at `:6835`) and warp-exit branch (`:6844-6849`) per 4d.

**Tests.**
- `LedgerOrchestratorTests.RecalcDuringWarp_DefaultArgs_SetsRequestFlag` — set `SuppressRecalcDuringWarp = true`, call `RecalculateAndPatch()` (default args), assert `RecalcRequested == true` and live `fundsModule.GetAvailableFunds()` did not change.
- `LedgerOrchestratorTests.RecalcDuringWarp_WithCutoff_RunsImmediatelyWithRewindFlags` (P1 fix from reviews v4 + v5) — set `SuppressRecalcDuringWarp = true`, call `RecalculateAndPatch(utCutoff: 1234.0)`. Assert: a real walk ran (live module state changed); `bypassPatchDeferral` and `authoritativeRepeatableRecordState` were both observed `true` inside `RecalculateAndPatchCore` (test hook on `Core` records the flags it received); `RecalcRequested` is unchanged from its pre-call value. Specifically tests both the coalesce-eligibility rule AND the preserved `utCutoff.HasValue`-derived flag contract.
- `LedgerOrchestratorTests.RecalcDuringWarp_PostRewindFlightLoad_RunsImmediately` (P1 fix from review v4) — set `SuppressRecalcDuringWarp = true`, call `RecalculateAndPatchForPostRewindFlightLoad(utCutoff: 1234.0)`. Assert: a real walk ran; `bypassPatchDeferral` and `authoritativeRepeatableRecordState` were both observed `false` (different contract from `RecalculateAndPatch(utCutoff)`). Locks in the post-rewind-flight-load path's distinct flag policy.
- `LedgerOrchestratorTests.RecalcDuringWarp_CutoffWalkDoesNotClearPendingDefault` (P2 fix from review v5) — set `SuppressRecalcDuringWarp = true`, call `RecalculateAndPatch()` (default args; coalesces, sets `RecalcRequested = true`), then call `RecalculateAndPatch(utCutoff: 1234.0)` (cutoff; runs inline). Assert `RecalcRequested == true` after the cutoff walk returns. Without this fix, the next 1 Hz tick or warp-exit flush would skip the pending default request and the player would see stale top-bar resources.
- `LedgerOrchestratorTests.BypassEntryPoint_RunsWalkEvenWhenSuppressed` (clarified per review v6) — set `SuppressRecalcDuringWarp = true` and `RecalcRequested = true`, call `RecalculateAndPatchBypassingCoalescing`, assert: (a) a real walk ran (live module state changed); (b) `SuppressRecalcDuringWarp` is still `true` after the call (bypass does not toggle the suppression flag); (c) `RecalcRequested` is `false` after the call (cleared before the walk; no nested subscriber re-set it). Two prior reviews (v3, v4) established the clear-before-walk policy; this test's earlier wording was ambiguous about which flag the smoke-test asserts on, and previous drafts incorrectly suggested `RecalcRequested` should still be `true` after — that reading would force the implementation back toward the stale-flag behaviour the v3/v4 fixes removed.
- `LedgerOrchestratorTests.BypassEntryPoint_NestedSubscriberCallStillCoalesces` — install an `OnTimelineDataChanged` subscriber that calls `RecalculateAndPatch()` (default args). Set `SuppressRecalcDuringWarp = true`. Call the bypass entry point. Assert: the bypass walk ran exactly once, the subscriber set `RecalcRequested = true`, no inner walk dispatched.
- `LedgerOrchestratorTests.BypassEntryPoint_ClearsRecalcRequestedBeforeWalk` — pre-set `RecalcRequested = true`, call the bypass entry point, assert the flag is observed `false` at the *start* of the walk (via a sentinel module's `Reset` that reads it).
- `LedgerOrchestratorTests.BypassEntryPoint_NestedSubscriberRequest_QueuesForNextTick` (P2 fix from review v3) — install an `OnTimelineDataChanged` subscriber that, when invoked, sets `RecalcRequested = true`. Pre-set `RecalcRequested = true`. Call the bypass entry point. Assert `RecalcRequested == true` after the call returns.
- `LedgerOrchestratorTests.RegularEntry_ClearsRecalcRequestedAfterWalk` (P2 fix from review v4) — pre-set `RecalcRequested = true`, call `RecalculateAndPatch()` with `SuppressRecalcDuringWarp = false`, assert `RecalcRequested == false` after the call returns. Together with the bypass-clear-before test, locks in the split clear policy.
- `LedgerOrchestratorTests.WarpExit_ClearsStaleRecalcRequested` (P2 fix from review v4) — simulate the full warp lifecycle: warp-start sets `SuppressRecalcDuringWarp = true`; some default-args call during warp coalesces (sets `RecalcRequested = true`); warp-exit clears `SuppressRecalcDuringWarp = false` then calls the regular `RecalculateAndPatch()`. Assert `RecalcRequested == false` after warp-exit returns. (Without the regular-entry post-walk clear, the next warp session would inherit a stale `true`.)
- `LedgerOrchestratorTests.RecalcDuringWarp_LongWalkDoesNotStack` — set `warpRecalcInFlight = true` (test hook), advance unscaled time by 5 s, assert no second recalc starts.
- `InGameTests/RuntimeTests.cs::WarpRecalcCadence_RespectsOneHzWallClock` — runtime test in flight scene: enter 100× warp, mutate ledger via test hook (synthetic KSC spending) at simulated 10 events/wall-second for 5 wall-seconds; assert exactly 5 walks ran (±1 for exit-flush) and assert top-bar funds value matches expected within 1 wall-second of each event.
- `InGameTests/RuntimeTests.cs::WarpRecalcCadence_NotCoupledToWarpRate` — same scenario at 1×, 100×, 100 000× warp; assert wall-clock walk count is the same in all three runs (within ±1).
- `InGameTests/RuntimeTests.cs::WarpRecalcCadence_DoesNotTickWhilePaused` — enter warp, pause game, advance unscaled time by 10 s with a pending `RecalcRequested`; assert no walk fires; unpause, assert flush walk fires within 1 wall-second.
- `InGameTests/RuntimeTests.cs::WarpRecalcCadence_ExitFlushIsExact` — fire several events in the last 0.5 s of warp; exit warp; assert the exit-flush walk reflects all of them exactly.
- `InGameTests/RuntimeTests.cs::WarpRecalcCadence_RateChangeDuringWarp_DoesNotFlushUnnecessarily` — enter 100× warp, change to 1000× warp without exiting, assert no flush walk fires for the rate transition itself.

**Risk.** Low-medium. The principal residual risk is an event class that needs exact-frame visual feedback during warp — today none do (they wait for warp exit), so 1 Hz is strictly better. If one is identified later, a dedicated `RecalculateAndPatchImmediate()` that bypasses suppression can opt out. None known today.

**Acceptance signal.** A 5-wall-second 100 000× warp burst with 50 synthetic ledger events fires exactly 5 walks (±1) and the top-bar funds value matches the cumulative net delta within 1 wall-second of each event.

---

### Item 5 — Affordability cache (re-scoped after review)

**Problem.** `CanAffordScienceSpending` (`LedgerOrchestrator.cs:3033`) and `CanAffordFundsSpending` (`LedgerOrchestrator.cs:3068`) each run a full walk + projection just to read one number. Today this only fires from `TechResearchPatch.cs:30` (one-shot per tech unlock click), so it is not yet a hitch — but it is a footgun for any future UI hover / per-frame check.

**What changed from draft 1 (correctness fix).** The first draft proposed a "skip projection" entry point. **This was wrong.** `FundsModule.GetAvailableFunds` (`FundsModule.cs:522-528`) and `ScienceModule.GetAvailableScience` (`ScienceModule.cs:430-435`) short-circuit to `projectedAvailable*` whenever `hasProjectedAvailable*=true`, set by `RecalculationEngine.ApplyProjectedAvailability` at `:589`. Skipping the projection would silently fall through to the legacy `initialFunds + totalEarnings - totalCommittedSpendings` formula, returning a value that ignores already-committed future spendings — exactly the cashflow reservation behaviour that affordability is supposed to enforce.

**New approach: pure caching, no path-skipping.** Cache the per-resource available value keyed on the five-tuple from §1, plus a NowUT bucket:

```
key = (LedgerVersion, TombstoneVersion, SupersedeVersion, RecordingStoreVersion, MarkerIdentity, floor(NowUT / 1.0))
```

Bucket size 1.0 s UT — fine-grained enough that 1 s of in-game time can't push a player from "can afford" to "can't" without a ledger event (which invalidates the version key tuple).

The cache stores `{available, projection-min-balance}` per resource. On hit: return cached value, skip the walk. On miss: run the standard walk-with-projection (no behaviour change), store the result.

**Read-only contract preserved.** The probe must not perturb the live module state that drives the in-game top bar. Today `CanAfford*` resets the live modules implicitly via `RecalculationEngine.Recalculate(actions, nowUT)`. The cache hit path returns directly without invoking the engine, so live modules are untouched. The miss path goes through the existing engine call. The contract should be tested explicitly via `CanAfford_DoesNotMutateLiveModules`.

**Files touched.**
- `Source/Parsek/GameActions/LedgerOrchestrator.cs`: rewrite `CanAffordScienceSpending` and `CanAffordFundsSpending` to consult the cache first, fall back to the existing walk path on miss, store result. No new entry point on the engine; no skip-projection plumbing.

**Tests.**
- `LedgerOrchestratorTests.CanAfford_RepeatProbe_HitsCache` — call `CanAffordFundsSpending(100f)` twice with no ledger mutation between; assert verbose log confirms cache hit.
- `LedgerOrchestratorTests.CanAfford_AfterCommit_RecomputesOnVersionBump` — probe, commit a recording, probe again, assert recompute log.
- `LedgerOrchestratorTests.CanAfford_AfterTombstone_RecomputesOnVersionBump` — probe, tombstone a kerbal-death action (bumps `TombstoneStateVersion` only), probe again, assert recompute log. (Specifically tests the five-tuple key — would fail if only `Ledger.StateVersion` were used.)
- `LedgerOrchestratorTests.CanAfford_DoesNotMutateLiveModules` — capture `fundsModule.GetAvailableFunds()` before and after a probe; assert equal.
- `LedgerOrchestratorTests.CanAfford_ReflectsProjectionReservation` — register a future `FundsSpending` action; assert the probe returns `currentBalance - futureSpending`, not `currentBalance`. Locks in the projection-still-runs invariant.

**Risk.** Low. Pure read-side caching with a known-good invalidation key.

**Acceptance signal.** Late-career save: tech-research repeated click N times → exactly 1 walk + N-1 cache hits.

---

### Item 6 — Drop projection on rewind path when cutoff is the rewind UT

**Problem.** `RecalculateAndPatch(RewindAdjustedUT)` runs both the main walk and the projection walk. On the Re-Fly merge path, the future after `RewindAdjustedUT` is **about to be tombstoned** — the projection walks actions that will be filtered moments later. Wasted work.

**Approach.** Add a `bool runProjection = true` parameter to `RecalculateAndPatch`, `RecalculateAndPatchCore`, and `RecalculationEngine.Recalculate`. The Re-Fly merge call sites pass `runProjection: false`. All other call sites (including the plain rewind path at `ParsekScenario.cs:2112`) keep the default `true` — their projection still has correctness value because the post-rewind future lives on.

**Audit of `Effective*Reward` / `EffectiveScience` / `Affordable` / `EffectiveRep` / `Transformed*Reward` consumers** (review-required, performed):

| Consumer | Where | Risk |
|---|---|---|
| Module sibling reads (FundsModule reads MilestonesModule.Effective, etc.) | inside walk | None — walk-internal. |
| `KspStatePatcher.cs` | applies walk results | None — runs after walk completes. |
| `LedgerOrchestrator.RebuildCommittedScienceFromWalk` (`:1563`) | post-walk | None — runs after walk completes. |
| `PostWalkActionReconciler.cs` | post-walk | None — runs after walk completes. |
| `Source/Parsek/UI/CareerStateWindowUI.cs` | UI repaint | **REAL RISK** — reads displayed projected fields. |
| `Source/Parsek/Timeline/TimelineBuilder.cs:525, 602, 645` | Timeline window repaint | **REAL RISK** — reads `Effective*Reward` for future-action rendering. |
| `LedgerLoadMigration.cs` | OnLoad migration | None — runs once before any walk. |
| `KscActionExpectationClassifier.cs` | classification | None — reads action-immutable fields, not derived ones. |

**The Real Risk paths.** Between `ParsekScenario.cs:2112` (rewind walk with `RewindAdjustedUT`) and the immediately-following merge-tail walk in `MergeJournalOrchestrator`, GUI could repaint and read stale `Effective` flags on future actions if it's open during the rewind. Concretely: if the player has the Career State window or Timeline window open during a rewind-to-merge, between the rewind-walk return and the merge-tail-walk start, the UI could grab post-rewind paint with no projection populated.

**Stale-field hazard (P1 fix from external review v6, refined in v7).** External review v6 caught that the original "future fields stay at reset baseline" claim was wrong. Today's `RecalculationEngine.ResetDerivedFields(sorted)` resets only the cutoff-filtered list (i.e., actions with `UT ≤ cutoff`), not the excluded suffix. With projection ON, the projection walk dispatches the FULL timeline through cloned modules, which write to the shared `GameAction.EffectiveScience` / `Effective*Reward` fields and overwrite stale values. With projection OFF (this item), the suffix retains whatever values a *prior* walk wrote.

External review v7 then caught a follow-on subtlety: the engine's existing reset baseline at `RecalculationEngine.cs:464-471` sets `Effective = true` and seeds `Transformed*Reward = *Reward` — these defaults are correct for actions ABOUT TO BE DISPATCHED (where the walk may flip `Effective = false` for duplicates etc.), but they are *wrong* for actions that will NOT be dispatched and will NOT be projected over. Career State and Timeline both gate display on `Effective`; if we used the existing reset on excluded suffix actions, future actions would render as **active with their original rewards**, not as inactive — a different stale-state hazard, not the desired "didn't happen" semantics.

**Fix: dedicated `ResetExcludedDerivedFields` helper for the cutoff suffix.** When `runProjection: false` and a cutoff applies, the engine zeros out *and inactivates* derived fields on the excluded suffix:

```csharp
// In RecalculationEngine, called only when (utCutoff.HasValue && !runProjection):
internal static void ResetExcludedDerivedFields(IReadOnlyList<GameAction> actions, double cutoff)
{
    for (int i = 0; i < actions.Count; i++)
    {
        var a = actions[i];
        if (a == null || a.UT <= cutoff || IsSeedType(a.Type)) continue;

        // "Didn't happen" semantics — distinct from the dispatch-baseline reset at
        // ResetDerivedFields (which sets Effective=true for actions about to be walked).
        a.Effective = false;
        a.EffectiveScience = 0f;
        a.Affordable = false;
        a.EffectiveRep = 0f;
        a.TransformedFundsReward = 0f;
        a.TransformedScienceReward = 0f;
        a.TransformedRepReward = 0f;
    }
}
```

This is called BEFORE the live walk runs, so the live walk's normal reset on the filtered prefix proceeds unchanged. The excluded suffix lives with `Effective=false / Transformed*=0` until a subsequent walk processes them.

**Mitigation — three-part.**

1. **Explicit suffix reset when `runProjection: false`** (the belt). `ResetExcludedDerivedFields` ensures excluded actions render as inactive in any UI that filters on `Effective`. Cheap O(N) writes; only fires on the rare merge-tail rewind path.
2. **Synchronous-block invariant** (the suspenders). The Re-Fly merge sequence in `MergeJournalOrchestrator` runs both walks back-to-back synchronously. Verify this by inspection: the orchestrator's `RunFinisher` step that calls the rewind walk and the supersede walk is one contiguous synchronous block — no `yield return`, no Unity Update() tick can interleave. **If a Unity frame can land between them, the optimisation is unsafe and Item 6 must be dropped.** This invariant is auditable in code; record the verification in the implementation PR.
3. **In-game regression test** (the canary). `InGameTests/RuntimeTests.cs::CareerStateWindow_DuringRewindMerge_DoesNotShowStaleProjection` opens Career State + Timeline windows, performs a synthetic rewind-to-merge sequence, asserts UI consumers see `Effective=false` for excluded future actions (not stale-from-prior-walk values, AND not "active-with-default-rewards" values).

**Files touched.**
- `Source/Parsek/GameActions/RecalculationEngine.cs`: optional `runProjection` parameter on `Recalculate(...)`. New private `ResetExcludedDerivedFields(actions, cutoff)` helper. When `utCutoff.HasValue && !runProjection`, call this helper before the live walk; do NOT call the existing `ResetDerivedFields` on the full list (different semantics — see the helper's doc-comment).
- `Source/Parsek/GameActions/LedgerOrchestrator.cs`: optional `runProjection` parameter on `RecalculateAndPatch(...)` and `RecalculateAndPatchCore(...)`.
- `Source/Parsek/MergeJournalOrchestrator.cs`: pass `runProjection: false` on the supersede-emission walks. Implementer task: trace which `MergeJournalOrchestrator` step calls `RecalculateAndPatch(...)`, and confirm by inspection that the next synchronous step issues another recalc that DOES populate projection (the merge-tail walk after tombstones land). Document the citation in the implementation PR; if the next step does not issue a follow-up recalc, drop this site from Item 6's scope and confine the optimisation to `MergeJournalOrchestrator`'s explicit follow-up sites only.
- `Source/Parsek/RewindInvoker.cs:643` (`RecalculateAndPatch(double.MaxValue)`): **excluded from Item 6 scope.** This call uses `double.MaxValue` (effectively no cutoff — every action is dispatched), so projection-skip would not save measurable work, and rewind-session-finish is followed by `ParsekScenario.cs:1647`'s `RecalculateAndPatchForPostRewindFlightLoad(loadedUT)` which the player-visible UI consumes. Leave as-is.

**Tests.**
- `LedgerOrchestratorTests.RecalcWithProjectionFalse_ExcludedFutureActions_AreInactivated` (P1 fix from reviews v6 + v7) — set up: register a future action with `Effective = true`, non-zero `EffectiveScience`, non-zero `TransformedFundsReward` from a prior full walk; run cutoff walk with `runProjection: false`; assert the future action ends up with `Effective = false`, `EffectiveScience = 0f`, `TransformedFundsReward = 0f`. The "inactivated" naming and the explicit `Effective = false` assertion lock in the v7 semantics — without the dedicated `ResetExcludedDerivedFields` helper, the test fails because the existing reset baseline would leave `Effective = true`.
- `LedgerOrchestratorTests.RecalcWithProjectionTrue_ExcludedFutureActions_AreRepopulated` — companion test: prior full walk leaves future field at value X; cutoff walk with `runProjection: true` runs projection over the future; assert future field equals what the projection would write (not necessarily X, but a deterministic value derived from the projection). Locks in that the v6/v7 fix doesn't accidentally break the `runProjection: true` path.
- `LedgerOrchestratorTests.RecalcWithProjectionFalse_PrefixDispatchedActions_StayEffective` — companion test: actions inside the cutoff that are dispatched normally end up with `Effective = true` from the live walk's normal reset+walk. Confirms `ResetExcludedDerivedFields` does not touch the prefix.
- `LedgerOrchestratorTests.MergeSupersede_SkipsProjection` — Re-Fly merge sequence, assert the projection-walk verbose log line is absent.
- `InGameTests/RuntimeTests.cs::CareerStateWindow_DuringRewindMerge_DoesNotShowStaleProjection` — see above; specifically asserts UI shows excluded future actions as inactive (e.g., greyed out / hidden depending on filter).

**Risk.** Medium. This is the riskiest item — it is the most likely to surface a hidden coupling in UI code. Ship it last.

**Acceptance signal.** A Re-Fly merge against a 5 000-action late-career save runs measurably faster (target: 30%+ reduction in the merge-tail recalc cost).

---

### Item 7 — Slot-limit reorder (clean-up)

**Problem.** `UpdateSlotLimitsFromFacilities` (`LedgerOrchestrator.cs:1511`) is "always one walk behind facility upgrades — converging within two calls at most" (per its own comment block at `:1505-1510`). With the 1 Hz warp tick (Item 5), facility-upgrade events can complete during warp and the very next tick uses pre-upgrade slot limits — then the tick after that converges. Today this convergence is hidden because warp-exit + post-warp UI events ensure two walks fire close together. With coalesced 1 Hz ticks, the UI may briefly display stale slot-limit overlays for one tick.

**Approach.** Move `UpdateSlotLimitsFromFacilities` to fire **after** the walk's `Recalculate` step (i.e., after `RecalculationEngine.Recalculate` at `:1529`), so the same walk's results feed slot limits. The next walk's `PrePass` then reads the up-to-date slot limits.

**Review-clarified mechanics.** `UpdateSlotLimitsFromFacilities` (`LedgerOrchestrator.cs:3133-3149`) reads `facilitiesModule.GetFacilityLevel(...)`. The pre-walk read uses last-walk's facility state because `RecalculationEngine.ResetAllModules` hasn't run yet for *this* walk. Moving it post-walk reads this-walk's PostWalk state — correct for upgrade events landed during this walk.

**The "first walk uses defaults" worry is moot.** `LedgerOrchestrator.Initialize()` runs facility load before the first `Recalculate`, so the post-walk read on the first walk sees correct facility state. The current "first call uses defaults" comment is a relic of an earlier ordering and becomes false after the reorder — **delete the comment block at `:1505-1510`, don't rewrite it**.

**Verify slot-limit mutations precede next walk's PrePass reads.** `ContractsModule.SetMaxSlots` and `StrategiesModule.SetMaxSlots` mutations made post-walk are read by next-walk PrePass. Today this works because: (a) `Initialize` is gated by `initialized`, called once; (b) `SetMaxSlots` writes to a field that `PrePass` reads on next call. No capture-at-Init pattern. Add a regression test: `LedgerOrchestratorTests.SlotLimits_ContractsModule_ReadsCurrentMaxOnPrePass` to lock this in.

**CHANGELOG note.** Prior saves' UI may have transiently shown wrong slot caps for one walk after a facility upgrade. After the fix this snaps to correct on the same walk that processed the upgrade. Worth one user-facing CHANGELOG line: "Fix: facility upgrades now reflect in contract/strategy slot caps on the same recalculation pass instead of one pass later."

**Files touched.**
- `Source/Parsek/GameActions/LedgerOrchestrator.cs`: move `UpdateSlotLimitsFromFacilities` from `:1511` to immediately after `RecalculationEngine.Recalculate` at `:1529`. Delete the now-stale comment at `:1505-1510`.

**Tests.**
- `LedgerOrchestratorTests.SlotLimits_ConvergeInSingleWalk_AfterReorder` — facility upgrade action, walk once, assert slot limits reflect the upgrade.
- `LedgerOrchestratorTests.SlotLimits_StillCorrectOnLoad` — load a save with facility-upgrade actions; assert slot limits correct after a single OnLoad recalc.
- `LedgerOrchestratorTests.SlotLimits_ContractsModule_ReadsCurrentMaxOnPrePass` — verify ContractsModule re-reads on every PrePass, doesn't capture.

**Risk.** Low — the reorder is local; any hidden coupling shows up immediately in the contract/strategy slot-limit tests.

**Acceptance signal.** The "convergence within two calls" comment block can be deleted from `LedgerOrchestrator.cs:1505-1510`.

---

## 5. Implementation Order

Each item is one PR.

1. **Item 1** — Conditional second sort (smallest, hygiene win, warm-up).
2. **Item 2** — Reusable scratch buffers (pure perf, no semantic change, includes reentrancy guard).
3. **Item 3** — Cache the ELS list allocation (cheap, depends on Item 2's signature shift).
4. **Item 4** — 1 Hz warp tick (user-visible win; benefits from Items 1–3 because per-tick cost is already trimmed).
5. **Item 5** — Affordability cache (re-scoped from "skip projection" to "pure cache"; locks in the explicit cache-hit guarantee for tech-research and future hover/probe callers).
6. **Item 6** — Drop projection on rewind (riskiest; depends on the audit; ships last among the structural items).
7. **Item 7** — Slot-limit reorder (opportunistic; can ride along with any of the LedgerOrchestrator-touching PRs above; landed before Item 4 if practical because Item 4 surfaces the drift).

Items 1–7 do not require database / save-format changes. No format-version bump.

**Dependency clarifications (per review v8):**
- **Item 4 has no structural dependency on Items 1–3.** The 1 Hz warp tick's correctness rests on the suppression flag, the private `RecalculateAndPatchInternal` layer, and the warp-handler edits. None of those touch the sort/buffer code in Items 1-3. The order above is by ROI (Items 1-3 are quick wins that trim per-tick cost first), not by correctness. If Item 1, 2, or 3 slips, Item 4 can ship standalone.
- **Item 5 has no structural dependency on Item 3.** `EffectiveState.ComputeELS` already returns `IReadOnlyList<GameAction>` at `Source/Parsek/EffectiveState.cs:523`, so the affordability cache-hit path can return early without invoking the engine regardless of whether Item 3's orchestrator-side wrapper-allocation drop has landed.
- Items 6 and 7 are independent of all earlier items.

**Reentrancy review pre-Item 4.** Before Item 4 lands, audit every subscriber of `OnTimelineDataChanged` (`LedgerOrchestrator.cs:1577`) and verify none calls `RecalculateAndPatch` synchronously in a way that would surprise the new coalescing flag. The bypass entry point's design ensures nested calls during the walk take the suppressed branch — but a subscriber that calls `RecalculateAndPatch` *outside* of warp expecting immediate execution is unaffected. Grep: `OnTimelineDataChanged +=`.

---

## 6. Test Strategy

Beyond per-item tests:

- **Recalculation fuzzer** (new file `Source/Parsek.Tests/RecalculationFuzzer.cs`): **10 000** deterministic-seeded random ledger generations × 4 cutoff scenarios (`null` / mid-timeline / past end / pre-start). Asserts:
  - In-place sort (Item 2) vs LINQ `OrderBy/ThenBy` produce identical ordering.
  - Pre-pass injection presence (Item 1) matches the second-sort decision.
  - Two-buffer separation (Item 2): cutoff-walk affordability values agree with from-scratch values element-for-element.
  - Action-type space coverage: **all currently-defined `GameActionType` values** (currently 23, values 0–22 — see `GameAction.cs:10-35`; includes `StrategyDeactivate` and the three `*Initial` seeds) × spend/earn × deadline/no-deadline × ContractAccept→Complete pairing × supersede-mid-walk × kerbal-death+tombstone. Pure random doesn't hit all combinations in 1 000 iterations; 10 000 with a coverage-guided seeder does. **CI gate:** the fuzzer asserts the enum has exactly the count it was authored against (a `RecalculationFuzzerTests.GameActionType_EnumValueCount_Locked` test), so adding a new enum value forces the fuzzer's coverage table to be updated in the same PR.

- **Hand-seeded "tricky" sequences** in `RecalculationFuzzer`: 9 explicit scenarios that pure random rarely produces:
  1. `KerbalAssignment` → death → tombstone → resurrection of replacement chain.
  2. `ContractAccept` → deadline expiry → `ContractFail` injection (locks in PrePass-driven sort #2).
  3. Supersede subtree mid-walk (rewind-merge sequence).
  4. Concurrent reservations + replacement-chain edits.
  5. `FundsSpending` exactly at the cutoff UT (boundary condition).
  6. `FundsEarning` and `FundsSpending` at the same UT (earnings-before-spendings ordering, identical-key stable-sort case).
  7. Strategy activation at session start.
  8. Multiple facility upgrades in one walk.
  9. `KerbalRescue` whose recording is later marked ghost-only (should be purged).

- **Late-career synthetic save**: extend the existing test save (`dotnet test --filter InjectAllRecordings`) to include a 10 000-action variant for benchmarking. Add a test that runs each of the 14 call sites against this save and emits a `Stopwatch`-measured row to `parsek-test-results.txt`.

- **In-game runtime tests** for the warp tick (Item 4): five tests in `RuntimeTests.cs` per the Item 4 acceptance section, exercising real `TimeWarp` rates plus the paused-warp gate plus the rate-change-during-warp case.

- **In-game runtime test** for Item 6 risk: `CareerStateWindow_DuringRewindMerge_DoesNotShowStaleProjection`.

- **Cutoff-walk allocation bounds** (gap from review v8): `RecalculationEngineTests.CutoffWalk_AllocationsBounded` — Item 5's affordability cache miss path runs cutoff walks, which today call `CreateProjectionModules` (`RecalculationEngine.cs:512-547`) doing `Activator.CreateInstance` per cloneable module per walk. Add a test that runs 100 cutoff walks and asserts `GC.GetAllocatedBytesForCurrentThread` per walk is below a quantified threshold (e.g., < 4 KB after warmup — generous for the projection module clone list but tight enough to catch a regression). Locks the projection-walk allocation in scope without requiring Item 5 to optimise it.

- **Orchestrator-level reentrancy via `OnTimelineDataChanged`** (gap from review v8): `LedgerOrchestratorTests.RegularEntry_OnTimelineDataChangedReentry_DoesNotCorrupt` — Item 2's `walkInProgress` guard sits in `RecalculationEngine.RunWalk`, but `OnTimelineDataChanged?.Invoke()` fires at `LedgerOrchestrator.cs:1577` AFTER the engine returns, so a subscriber that calls `RecalculateAndPatch` recursively is *not* engine-reentrant — it's orchestrator-reentrant. The static scratch buffers are safe by reuse discipline (the outer `RunWalk` has already released them), but this isn't load-bearing without a test. Install a subscriber that calls `RecalculateAndPatch()` from the change event; assert (a) inner walk completes, (b) outer walk's post-walk steps (`RebuildCommittedScienceFromWalk`, `OnTimelineDataChanged` propagation guard) don't crash on the re-walked state, (c) module final state is identical to a single-walk reference.

- **Update-loop overhead outside warp** (gap from review v8): `LedgerOrchestratorTests.WarpRecalcCadence_NoOverheadOutsideWarp` — micro-bench asserting the `Update`-side warp-tick gate (5 conditions: `IsAnyWarpActive`, `Time.timeScale > 0.01f`, `RecalcRequested`, `!warpRecalcInFlight`, time-delta) costs less than a quantified ceiling (e.g., < 100 ns per frame on a modern CPU) when warp is inactive and the gate fails fast on the first condition. Run 10 000 iterations, divide. Catches anyone who later replaces the `IsAnyWarpActive` short-circuit with a more expensive predicate.

- **`KerbalsModule.slots` walk-stability** (gap from review v8): `KerbalsModuleTests.RepeatedWalks_SlotCountStable` — `KerbalsModule.slots` (`Source/Parsek/KerbalsModule.cs:39`) is mutated by `ProcessAction` and intentionally NOT cleared in `Reset()` at `:138-146` (only `reservations`, `recordingMeta` are cleared). Items 1-7 don't change this, but Item 4's 1 Hz warp tick increases the walk cadence dramatically — if a regression made `slots` accumulate per walk, late-career saves under sustained warp would silently bloat. Test: run `Recalculate` 10 times on the same input, assert `kerbalsModule.Slots.Count` is identical after each walk.

Each PR adds its own per-item tests **and** must not regress the fuzzer.

---

## 7. Logging & Observability

Per-walk logging stays the same shape (the `VerboseOnChange "recalculate-summary"` line at `RecalculationEngine.cs:228`). Additional verbose lines:

- Affordability cache hit: `[Affordability] cache hit (key=L:N|T:M|S:K|R:P|MI:Q|bucket:B)`
- Affordability cache miss + recompute: `[Affordability] cache miss, recomputed (resource=funds|science, available=X)`
- Warp tick: `[Warp] coalesced recalc tick (queued=K, last=Δs, paused=false)` — `VerboseRateLimited` keyed on `"warp-tick"`.
- Warp coalesce drop: `[Warp] suppressing recalc during active warp (call site=X)` — `VerboseRateLimited` keyed on `"warp-coalesce-drop"`.
- Warp pause-skip: `[Warp] skipping recalc tick — game paused (timeScale=0)` — `VerboseRateLimited` keyed on `"warp-pause-skip"`.
- Reentrancy guard (Item 2): `[RecalcEngine] re-entrant walk attempt blocked` — `Warn`-level (rare, real bug indicator).

Add a one-shot debug-flag `Stopwatch` instrumentation gated on a settings-window toggle (`Diagnostics > Recalc timing`). Off by default. When on, every walk logs `[RecalcEngine] walk N actions, M ms`. Useful for the rollout PR but should be untoggled before tagging a release.

---

## 8. Risks Summary

| Risk | Likelihood | Mitigation |
|---|---|---|
| Sort produces non-deterministic order on equal-key elements | Eliminated by design | Bottom-up merge-sort on `List<GameAction>` is stable by construction; comparer stays 3-key; `StableSort_PreservedOnIdenticalFullKeys` + `StableSort_LinqEquivalence` tests |
| Single shared sort buffer corrupts projection results | Eliminated by design | Two distinct buffers (`scratchLiveSort` + `scratchProjectionSort`); regression test `CutoffWalk_ProjectionBufferNotClobbered` |
| Affordability cache returns stale value across a missed bump | Low | Five-tuple version key from §1; explicit test for tombstone-only bump invalidation |
| 1 Hz tick fires during paused warp | Low (mitigated) | `Time.timeScale > 0.01f` gate + `WarpRecalcCadence_DoesNotTickWhilePaused` in-game test |
| Warp coalescing flag desynchronises (e.g. set on warp-start, never cleared) | Low | Single bool, paired set/clear in the same `OnTimeWarpRateChanged` handler; assertion in warp-exit branch that flag was set |
| Subscriber-triggered nested walk during bypass entry point causes correctness issue | Low | Bypass entry point does not toggle the flag; nested calls take the suppressed branch; `BypassEntryPoint_NestedSubscriberCallStillCoalesces` test |
| Bypass entry point overwrites a nested subscriber's request flag | Eliminated by P2 fix (review v3) | `RecalcRequested` cleared *before* the bypass walk, not after; `BypassEntryPoint_NestedSubscriberRequest_QueuesForNextTick` test |
| Coalescing a cutoff/flag call would silently flush as a default-args walk | Eliminated by P1 fix (review v4) | Coalesce eligibility rule: only default-args calls coalesce; cutoff/flag calls run inline; `RecalcDuringWarp_WithCutoff_RunsImmediatelyWithRewindFlags` + sibling tests |
| Stale `RecalcRequested = true` survives warp-exit and triggers a spurious next-warp tick | Eliminated by P2 fix (review v4) | Regular entry clears `RecalcRequested` after a successful default-args walk; `WarpExit_ClearsStaleRecalcRequested` test |
| Public-wrapper flag contract silently dropped (`utCutoff.HasValue`-derived flags) | Eliminated by P1 fix (review v5) | Public wrappers preserved verbatim; coalescing added only in private internal layer; `RecalcDuringWarp_WithCutoff_RunsImmediatelyWithRewindFlags` asserts both flags reach `Core` |
| Cutoff walk during warp clears a pending default-args coalesced request, dropping the queued flush | Eliminated by P2 fix (review v5) | Post-walk clear gated on `isDefaultArgs`; cutoff walks leave the flag alone; `RecalcDuringWarp_CutoffWalkDoesNotClearPendingDefault` test |
| Scratch sort buffer breaks PrePass's mutable `List<GameAction>` contract | Eliminated by P1 fix (review v6) | Two `List<GameAction>` scratch buffers (no struct array); stable in-place merge-sort helper; `PrePassMutation_RoundtripsThroughBuffer` test |
| Projection-disabled cutoff walk leaves stale future-action fields for UI consumers | Eliminated by P1 fix (reviews v6 + v7) | When `runProjection: false`, engine calls dedicated `ResetExcludedDerivedFields` helper that sets `Effective=false`, `Transformed*=0` on excluded suffix (distinct from the dispatch-baseline reset which would leave `Effective=true`); `RecalcWithProjectionFalse_ExcludedFutureActions_AreInactivated` test |
| Projection-walk module-clone allocation unbounded for cutoff/affordability path | Pinned by review v8 | `RecalculationEngineTests.CutoffWalk_AllocationsBounded` asserts < 4 KB per walk after warmup |
| `OnTimelineDataChanged` subscriber recursion corrupts orchestrator-level state (engine `walkInProgress` guard doesn't cover this path) | Pinned by review v8 | `RegularEntry_OnTimelineDataChangedReentry_DoesNotCorrupt` test asserts module final state is identical to a single-walk reference |
| Future regression makes `Update`-loop warp-tick gate expensive outside warp | Pinned by review v8 | `WarpRecalcCadence_NoOverheadOutsideWarp` micro-bench, < 100 ns per frame |
| `KerbalsModule.slots` accumulates across walks (not cleared in `Reset()`); 1 Hz warp ticks would bloat late-career saves | Pinned by review v8 | `KerbalsModuleTests.RepeatedWalks_SlotCountStable` asserts identical count after every walk on identical input |
| New `GameActionType` value lands without fuzzer coverage update | Low | `GameActionType_EnumValueCount_Locked` CI-gate test forces fuzzer table to be updated in the same PR |
| `RunWalk` reentrancy via `OnTimelineDataChanged` corrupts scratch buffers | Low | Guard bool in Item 2 throws on re-entry; pre-Item-4 audit of subscribers |
| Item 6 (drop projection on rewind) breaks a UI consumer of post-rewind projected fields | Med | Audit performed in §4-Item 6; in-game regression test added; ships last |
| Slot-limit reorder (Item 7) breaks contract/strategy module pre-pass | Low | Existing slot-limit tests catch this immediately; new `ReadsCurrentMaxOnPrePass` test |

---

## 9. Out of Plan / Future Work

- **In-memory snapshot/checkpoint memoization.** The original draft proposed mid-walk snapshots at commit and pre-supersede boundaries to make rewind/merge walks resume from a cached prefix instead of UT=0. Two structural defects forced dropping it from this plan:
  1. **Cache key contradicts the post-commit win.** Any snapshot key that includes `Ledger.StateVersion` (or any of the five version counters) is invalidated by exactly the events the optimisation wants to accelerate (commits, supersedes, tombstones). Salvage requires splitting `Ledger.StateVersion` into separate append-vs-mutation generations and proving append-only safety for `ContractsModule.PrePass` injections — neither is trivial and PrePass is provably *not* append-safe (a new `ContractAccept` can change which `ContractFail` synthetic actions get injected at earlier UTs).
  2. **Prefix derived fields are shared mutable state on `GameAction`.** A smaller cutoff walk between snapshot capture and resume can leave prefix actions' `Effective*Reward` / `EffectiveScience` / `Affordable` fields at default values, because today's `ResetDerivedFields` resets the entire timeline at walk start. Salvage requires either capturing per-action derived-field snapshots in the cache (~28 bytes × N actions × ring slots ≈ 1 MB at 10 000 actions) or changing `ResetDerivedFields` to reset only the dispatched range and proving every consumer is fine reading derived fields populated by an arbitrary prior walk.
  
  Both salvages are implementable but require deep correctness review and module-level surgery (split `Reset()` into walk-output vs PrePass-cache halves; tighten `IResourceModule` PrePass purity contract; add `ISnapshottableModule` to all eight modules with KerbalsModule's `slots`/`reservations`/`recordingMeta` round-trip being non-trivial). Defer to a dedicated plan that can do the analysis end-to-end. The 1 Hz warp tick (Item 4) hits its wall-clock budget without this optimisation on realistic ledger sizes (~10 000 actions ≈ 10–20 ms per walk, well under the 1 s budget); rewind/merge perf is the deferred concern.

- **Persistent snapshots.** Same idea as above but persisted to disk so the post-load first walk can resume instead of walking UT=0 → end. Bigger correctness surface (snapshot must be invariant under ledger format migrations). Out of scope.

- **Multi-resource budget projection during warp.** Today the projection walk only powers the cutoff path. A future enhancement could feed the 1 Hz tick a "what will my balance be at the next contract-deadline UT" preview field, like a mini sparkline in the top bar. Out of scope.

- **Async / background walk.** The walk is CPU-bound but pure (after Item 2 it's also alloc-free). A future plan could move the walk off the main thread when not on the rewind hot path. The mid-warp 1 Hz tick is a natural candidate. Single-threaded here.

- **Coalescing for non-warp event storms.** The 1 Hz tick coalesces only during active warp. A burst of `OnRecordingCommitted` events at warp 1× still triggers one full walk per event. If late-career saves surface this as a hitch, generalising the coalescing scope to any `Update`-driven tick is the natural next step. Not in this plan.

---

## 10. References

- Audit conversation (in-session, 2026-04-29): full call-site enumeration, cost-shape analysis.
- Internal Opus review (in-session, 2026-04-29): correctness fix on draft Item 1, cache-key gap on draft Item 2, pause-gate on draft Item 3, reentrancy on draft Item 5, audit for draft Item 7, missing ELS list cache item.
- External review v1 (2026-04-29): five P1/P2 defects — projection buffer reused before consumed, in-place sort loses stability contract, snapshot resume drops PrePass action mutations, end-of-walk capture cannot represent earlier checkpoints, warp bypass scope internally inconsistent. Folded into Items 2 and 4 (1 Hz warp tick); buffer-reuse and stable-sort fixes locked in.
- External review v2 (2026-04-29): three more defects — snapshot key invalidates the commit path the test expects, prefix derived fields can be stale after another cutoff resume, bypass clear loses nested recalc requests. Resolution: dropped the snapshot/checkpoint item entirely (moved to Future Work §9 with both structural defects documented); fixed bypass clear ordering (clear *before* the walk) in Item 4.
- External review v3 (2026-04-29): three more defects — suppressed cutoff requests lose semantics (a flag-only `RecalcRequested` cannot represent `utCutoff` / `bypassPatchDeferral` / `authoritativeRepeatableRecordState`), warp-exit flush is said to clear a flag it never clears (the regular entry needed an explicit post-walk clear), fuzzer undercounts action types (was 18, currently 23). All three folded into Item 4 and §6 in this revision.
- External review v4 (2026-04-29): two more defects in Item 4's pseudocode — `RecalculateAndPatch(utCutoff)` no longer set the existing `utCutoff.HasValue`-derived `bypassPatchDeferral` / `authoritativeRepeatableRecordState` flags (would have silently broken `RecalculateAndPatch(RewindAdjustedUT)` and `double.MaxValue` callers); cutoff walks should not clear pending default-args requests (the post-walk clear was unconditional, dropping queued default flushes). Both folded into Item 4: pseudocode now preserves the public-wrapper flag contract verbatim and gates the post-walk clear on `isDefaultArgs`.
- External review v5 (2026-04-29): three more defects — scratch sort buffer's `ScratchEntry` struct array would have broken `ContractsModule.PrePass`'s `List<GameAction>` mutation contract; Item 6's "projection-disabled leaves baseline" claim was wrong because `ResetDerivedFields` resets only the filtered list (not the excluded suffix); the bypass smoke test's assertion wording contradicted the v3/v4 clear-before policy. Resolution: Item 2 now uses two `List<GameAction>` buffers + a bottom-up merge-sort helper for stability; Item 6 explicitly resets the full action list when `runProjection: false`; the smoke test now asserts both `SuppressRecalcDuringWarp == true` and `RecalcRequested == false` post-call.
- External review v6 (2026-04-29): two more defects — stale `ScratchEntry` / `OriginalIndex` instructions remained in the active spec alongside the corrected `List<GameAction>` design (would have produced two contradicting declarations); `ResetDerivedFields` baseline sets `Effective = true`, so reusing it on the excluded suffix would have left future actions looking *active with default rewards* rather than inactive. Resolution: Item 2 active spec scrubbed of `ScratchEntry` / `OriginalIndex` references (revision-2 note marks them superseded); Item 6 introduces a dedicated `ResetExcludedDerivedFields(actions, cutoff)` helper with "didn't happen" semantics (`Effective=false`, `Transformed*=0`).
- Final internal Opus review v7 (2026-04-29): verdict ITERATE; flagged §10 file-path errors (`EffectiveState.cs`/`KerbalsModule.cs` not in `GameActions/`), Item 3 collapse (`ComputeELS` already returns `IReadOnlyList`), Item 4 4d insertion-point ambiguity (before/after `IsInitialized` gate), Item 6 `RewindInvoker.cs:643` hand-wave (now excluded from scope), and four test-coverage gaps (cutoff-walk allocations, orchestrator-level reentrancy, Update-loop overhead, `KerbalsModule.slots` walk-stability). All folded into revision 8.
- `docs/dev/development-workflow.md` — vision → scenarios → design doc → plan/build/review cycle.
- `Source/Parsek/GameActions/RecalculationEngine.cs` — current engine (700 LOC).
- `Source/Parsek/GameActions/LedgerOrchestrator.cs` — current orchestrator (3 000+ LOC).
- `Source/Parsek/EffectiveState.cs` — existing input caches (~600 LOC). Note: lives at the project root, NOT under `GameActions/`. Same for `KerbalsModule.cs`.
- `Source/Parsek/GameActions/FundsModule.cs:522-528`, `Source/Parsek/GameActions/ScienceModule.cs:430-435` — `GetAvailable*` short-circuits to projected values.
- `Source/Parsek/ParsekScenario.cs:53-73` — `SupersedeStateVersion` and `TombstoneStateVersion` declarations.
- `Source/Parsek/KerbalsModule.cs:138-208` — module Reset / PrePass / RecordingStore-derived state.
- `Source/Parsek/ParsekFlight.cs:6820-6860, 17854` — current warp transition handlers + `Time.timeScale < 0.01f` pause-gate pattern.
- `Source/Parsek/GameActions/KspStatePatcher.cs` — KSP singleton mutators wrapped in `SuppressionGuard`.
