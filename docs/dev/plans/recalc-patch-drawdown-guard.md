# Recalc-Patch Drawdown Guard (defense-in-depth)

Status: PLAN ONLY. No code in this branch beyond this document. Do not implement
from this doc without a follow-up build/review cycle.

Motivating bug: BUG-A (fixed in PR #1090). During pure-stock career play with no
Parsek feature used, a scene-change `RecalculateAndPatch` silently patched live
science 124.8 -> 1.0 and clawed back 47840 funds, because a Mun mission recorded
only its pre-time-warp launch window and the ledger converters dropped the
on-rails-warp earnings. PR #1090 fixed that one conversion leak. This plan adds a
general guard so that ANY future earning-channel leak cannot again silently and
irreversibly destroy career progress.

This is a follow-up explicitly anticipated by the BUG-A writeup
(`docs/dev/todo-and-known-bugs.md`): "a defense-in-depth guard that refuses a
large negative drawdown when no rewind/re-fly is active is a sensible follow-up
but was not needed to fix the root cause."

------------------------------------------------------------------------

## 1. Problem statement and threat model

### 1.1 What the patcher does today

`KspStatePatcher.PatchAll` (`Source/Parsek/GameActions/KspStatePatcher.cs`) writes
the recalculation engine's computed target totals back into KSP singletons:

- `PatchScience`: `delta = target - ResearchAndDevelopment.Instance.Science`, then
  `ResearchAndDevelopment.Instance.AddScience(delta, TransactionReasons.None)`.
  Plus `PatchPerSubjectScience` writes per-subject credited totals.
- `PatchFunds`: `delta = target - Funding.Instance.Funds`, then
  `Funding.Instance.AddFunds(delta, TransactionReasons.None)`.
- `PatchReputation`: `Reputation.Instance.SetReputation(target, ...)` (absolute set,
  not a delta add).
- `PatchTechTree`, `PatchFacilities`, `PatchContracts`, `PatchMilestones`.

The targets come from the ledger walk: `ScienceModule.GetAvailableScience()`,
`FundsModule.GetAvailableFunds()`, `ReputationModule.GetRunningRep()`. The patch
trusts the ledger as ground truth.

### 1.2 The single existing protection is log-only

`KspStatePatcher.IsSuspiciousDrawdown(delta, currentPool)`
(`KspStatePatcher.cs:2123`) returns true when `delta < 0`, `currentPool > 1000`,
and `|delta| > 0.10 * currentPool`. It is used ONLY to emit a WARN in
`PatchScience` and `PatchFunds`; the code then unconditionally applies the delta.
`PatchReputation` does not even consult it. `PostWalkActionReconciler.ReconcilePostWalk`
is also log-only and operates per-action (transformed-reward reconciliation), not
on the net pool-level target, so neither layer prevents the wipe.

Worse: `suppressSuspiciousDrawdownWarnings` is passed `true` by
`LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT` (the scene-change /
live-event / time-jump path), which silences even the WARN on exactly the path
that caused BUG-A.

### 1.3 What the guard defends against

A too-low recalc target driving live state DOWN with no time-travel context to
justify it. Concretely: the ledger is missing a legitimately-earned channel (a
converter / attribution / tagging bug), so the computed target is below current
live state, and the patch drags live science/funds (and potentially reputation,
tech, facilities, contracts) down to the buggy target. The loss is permanent
because `AddScience`/`AddFunds` mutate the stock singletons in place and the
result is saved.

### 1.4 What the guard must NOT break

The recalc/patch is the same mechanism that applies legitimate time-travel deltas.
Reducing live state DOWN is correct in some cases. The guard must let these
through:

1. Rewind-to-Separation OnLoad recalc (`ParsekScenario.cs:3202`,
   `RecalculateAndPatch(adjustedUT, suppressSuspiciousDrawdownWarnings: true)`):
   the timeline is rewound; future earnings are intentionally removed.
2. Post-invoke re-fly recalc (`RewindInvoker.cs:908`,
   `RecalculateAndPatch(double.MaxValue)`).
3. Post-supersede tombstone recalc (`SupersedeCommit.cs:2359` ->
   `RecalculateAndPatchAfterTombstones`): tombstoned kerbal-death actions and
   bundled rep penalties are removed from the effective ledger.
4. Time jump (`TimeJumpManager.cs:75`, `RecalculateAndPatchForTimeJump`).

And it must NOT block legitimate downward movements that are NOT time-travel and
NOT bugs, because those flow through the ledger as genuine net spending and the
target legitimately drops:

- Admin-building strategy currency exchanges (Bail-Out Grant and similar).
- Contract penalties (fail / cancel / deadline).
- Facility upgrade and repair costs.
- Tech node purchases (science spent on the tree).
- Vessel build / rollout costs.
- Reputation penalties (rep can and should fall on contract failure / kerbal death).

The crucial distinction this plan keys on: "the ledger legitimately CONTAINS net
spending" is fine, because the patch then computes a target consistent with the
current live total minus the just-committed spend (small, justified delta). The
DANGER is specifically "the patch target is FAR BELOW current live with no
time-travel context", which is the signature of a missing earning channel rather
than a genuine spend.

### 1.5 Scope boundary (NOT in scope)

- Recovering already-corrupted saves. If a save already has wiped science/funds
  baked into the .sfs, this guard does not heal it; it only prevents NEW
  corruption from this point forward.
- Per-action ledger correctness. Fixing converter/attribution leaks (the BUG-A
  class) is a separate, ongoing effort. This guard is the last line of defense
  AFTER such a leak slips through.
- Tech-tree / facility / contract "downgrade" detection beyond the science / funds
  / reputation pools. See section 6 for why these get lighter treatment.
- Changing the deferral behavior (`GetKspPatchDeferralReason`) or the
  ledger-mirrors-live invariant.

------------------------------------------------------------------------

## 2. Code investigation findings (current state)

### 2.1 Patch call-site map (when the patcher runs, with what target)

All public entry points live in `LedgerOrchestrator` and funnel into
`RecalculateAndPatchCore` -> (deferral check) -> `ApplyRecalculatedStateToKsp` ->
`KspStatePatcher.PatchAll`.

| Entry point | utCutoff | suppressSuspiciousDrawdownWarnings | Triggered by |
| --- | --- | --- | --- |
| `RecalculateAndPatch()` | null | false | KSP load no-future (`OnKspLoad`), scene-load no-future (`ParsekScenario:2653`), plain commit |
| `RecalculateAndPatch(double.MaxValue)` | MaxValue | false | re-fly post-invoke (`RewindInvoker:908`) |
| `RecalculateAndPatch(adjustedUT, suppress:true)` | adjustedUT | true | rewind OnLoad (`ParsekScenario:3202`) |
| `RecalculateAndPatchForCurrentTimelineUT(ut, reason)` | ut | **true** | scene-change/live-event/time-jump common path |
| `RecalculateAndPatchForCurrentTimelineIfFutureActions` | ut or null | true or false | flight scene-exit commit, pending-tree discard, MergeDialog |
| `RecalculateAndPatchForLiveTimelineEvent(ut, reason)` | ut or null | true or false | KSC spending, ksc-science, vessel-rollout, vessel-recovery |
| `RecalculateAndPatchForTimeJump(ut)` | ut | true | TimeJumpManager |
| `RecalculateAndPatchAfterTombstones(...)` | null | false | re-fly merge tail (SupersedeCommit) |

Call sites (grep `RecalculateAndPatch` across `Source/Parsek`): `ParsekScenario.cs`
(load, post-rewind, discard, deferred-seed), `ParsekFlight.cs` (scene-exit commit,
time jump, auto-discard), `ParsekKSC.cs` (KSC live events), `MergeDialog.cs`,
`TimeJumpManager.cs`, `RewindInvoker.cs`, `SupersedeCommit.cs`.

Key observation: BUG-A fired on the return-to-Space-Center path, which lands in
`RecalculateAndPatch()` (suppress=false), so the WARN did fire in the log but the
patch still applied. The scene-change-from-flight common path
(`RecalculateAndPatchForCurrentTimelineUT`) silences even the WARN.

### 2.2 The deferral guard already narrows the danger window

`GetKspPatchDeferralReason` (`LedgerOrchestrator.cs:2044`) returns non-null (and
the patch is SKIPPED) whenever `GameStateRecorder.HasLiveRecorder()`,
`HasActiveUncommittedTree()`, or `RecordingStore.HasPendingTree` is true, unless
`bypassPatchDeferral` is set (only the cutoff/rewind paths bypass). So:

- While a recording is live or uncommitted-in-flight, no patch is written.
- The dangerous downward patch only fires AFTER commit, when the
  ledger-mirrors-live invariant is supposed to hold and the expected delta is ~0.

This is load-bearing for the false-positive analysis: a genuine in-flight spend or
earning is committed to the ledger BEFORE the post-commit recalc, so the target
already reflects it and the delta is small.

### 2.3 Target computation

`RecalculationEngine.Recalculate(actions, utCutoff)` resets modules and walks the
Effective Ledger Set (`EffectiveState.ComputeELS()`, tombstone-filtered) forward.
Per-resource:

- `ScienceModule.GetAvailableScience()` = seed + earnings - tech spend (clamped).
- `FundsModule.GetAvailableFunds()` = min projected balance from cutoff forward
  (reservation), collapsing to `initial + earnings - spendings` for full walks.
- `ReputationModule.GetRunningRep()` = curve-applied running reputation.

`HasSeed` gates each patch: with no seed (early load), the patch is skipped to
preserve KSP values (#392). Seeds are established once via
`SeedInitialResourceBalances` and never re-upgraded from live state.

### 2.4 Time-travel signals available in memory

- `RewindContext.IsRewinding` (static bool). True from `BeginRewind` to
  `EndRewind`. The rewind OnLoad recalc at `ParsekScenario:3202` runs WHILE this is
  true (EndRewind is at `:3215`, after). Cleared after the rewind completes; does
  NOT survive into later normal-play scene changes. NOT persisted.
- `ParsekScenario.Instance.ActiveReFlySessionMarker` (`ReFlySessionMarker`, may be
  null). PERSISTED across save/load via OnSave/OnLoad. Non-null for the lifetime of
  a re-fly session (set at invocation, cleared on merge / return-to-KSC /
  quit-without-merge / retry / full-revert / load-time validation failure).
- `ParsekScenario.Instance.ActiveMergeJournal` (`MergeJournal`, may be null).
  PERSISTED. Non-null while a re-fly merge is mid-flight across crash-recovery
  checkpoints.
- `RecalculateAndPatchAfterTombstones` is its own entry point (only SupersedeCommit
  calls it), so the tombstone recalc is identifiable by call path even without a
  flag.

These four together are the reliable "a rewind/re-fly is in progress or just
happened" signal. None of them is true during ordinary pure-stock play, which is
exactly when the dangerous wipe must be blocked.

### 2.5 The `suppressSuspiciousDrawdownWarnings` flag is the wrong signal to reuse

It is true on BOTH a legitimate path (rewind OnLoad) AND the dangerous path
(scene-change current-UT). It was designed to silence noisy WARNs on
expected-reduction paths, not to authorize destructive reductions. The guard must
NOT key on it. The guard derives a SEPARATE authoritative-reduction signal (section
3.2) and passes it explicitly down the same plumbing the suppress flag uses.

------------------------------------------------------------------------

## 3. The decision predicate

### 3.1 Three outcomes per resource

For each guarded resource (science, funds, reputation) the guard classifies the
patch as exactly one of:

- (a) SAFE-TO-APPLY: delta >= 0 (an increase, or no change), OR a small downward
  delta within tolerance. Apply unchanged.
- (b) AUTHORIZED-REDUCTION: a large downward delta WITH an active/recent
  time-travel context. Apply unchanged (this is intended time travel).
- (c) GUARDED-DRAWDOWN: a large downward delta with NO time-travel context. This is
  the suspicious wipe. Take the guarded action (section 4).

Per-resource because their drawdown semantics differ (section 3.4).

### 3.2 The time-travel-authorized signal

A boolean `authoritativeReduction` computed once per `RecalculateAndPatchCore`
call and threaded down to `PatchAll` (alongside `suppressSuspiciousDrawdownWarnings`,
NOT reusing it). It is true when ANY of:

1. `RewindContext.IsRewinding` is true.
2. `ParsekScenario.Instance?.ActiveReFlySessionMarker != null`.
3. `ParsekScenario.Instance?.ActiveMergeJournal != null`.
4. The call is the tombstone path (`RecalculateAndPatchAfterTombstones` sets the
   flag explicitly; it is the only caller that needs to, since tombstone removal is
   a designed reduction and may run after EndRewind has cleared IsRewinding).

Rationale for each:
- (1) covers the rewind OnLoad recalc and the post-invoke recalc that run during an
  active rewind.
- (2) covers any recalc that fires while a re-fly session is live (including
  scene-change recalcs DURING the session, which legitimately reflect the
  superseded-branch removal).
- (3) covers mid-merge recalcs across the crash-recovery checkpoints.
- (4) covers the tombstone tail specifically.

This signal is computed inside `LedgerOrchestrator` (it has access to
`RewindContext` and `ParsekScenario.Instance`), not inside `KspStatePatcher` (which
is a pure-ish writer and should stay free of scene-state lookups for testability).
`KspStatePatcher` receives the boolean as a parameter.

A pure, internal-static helper makes it unit-testable:

```
internal static bool IsAuthoritativeReduction(
    bool isRewinding, bool hasReFlyMarker, bool hasMergeJournal, bool tombstonePath)
    => isRewinding || hasReFlyMarker || hasMergeJournal || tombstonePath;
```

The live wrapper reads the four inputs and calls it.

### 3.3 The drawdown threshold (when is a downward delta "large")

Reuse and extend the existing `IsSuspiciousDrawdown` shape so behavior stays
consistent with the WARN that operators already know:

- delta >= 0 -> never guarded.
- currentPool <= pool-floor -> never guarded (below the floor a wipe is small in
  absolute terms and the false-positive risk on tiny pools is high).
- |delta| > fraction * currentPool -> candidate for guard.

Current `IsSuspiciousDrawdown` uses pool-floor 1000 and fraction 0.10. The guard
should be MORE conservative than the WARN to avoid blocking legitimate large
spends that the WARN merely flags. Recommended split:

- Keep `IsSuspiciousDrawdown` (10% / 1000) as the WARN tripwire, unchanged.
- Add `IsGuardableDrawdown(delta, currentPool)` with a HIGHER bar for actually
  blocking: pool-floor and fraction tuned so a single legitimate large spend does
  not trip it. See section 7 (open question) for the exact numbers; the design
  defaults to fraction 0.50 and an absolute floor (for example, block only when the
  drop is BOTH > 50% of the pool AND > an absolute minimum such as 5000 of the
  resource), so that small-career exchanges and node purchases never trip it.

The two predicates compose: WARN on suspicious (10%), GUARD on guardable (50% +
absolute). Every legitimate-but-large spend in section 5 is far below the 50%
clamp in a healthy career because the spend was already committed to the ledger and
the patch delta is the small residual, not the whole spend.

### 3.4 Per-resource specifics

- SCIENCE: pool can be legitimately spent to ~0 by buying tech, but each purchase
  is a committed `ScienceSpending` action, so the post-commit patch delta is small.
  A target FAR below live with no time-travel = leak. Per-subject science
  (`PatchPerSubjectScience`) also needs guarding (section 6): a subject's credited
  total being zeroed mirrors the same leak.
- FUNDS: same shape; large spends (facility upgrade, rollout) are committed
  actions. Guard the net `AddFunds` delta.
- REPUTATION: special. Rep is a SET (`SetReputation`), not an add, and legitimately
  FALLS on contract failure and kerbal death even in normal play. Rep also has a
  bounded range and can be negative. The guard for rep must be LESS aggressive:
  only guard when the rep target drops by an implausibly large absolute amount with
  no time-travel context (a full or near-full collapse), since ordinary penalties
  are bounded and routed through committed actions. Recommendation: apply the clamp
  to rep but with a separate, looser threshold, and lean on the time-travel signal
  rather than the magnitude (rep collapses are most plausibly a missing
  ReputationEarning channel). See section 7 open question.

------------------------------------------------------------------------

## 4. The guarded action (what the guard DOES on a GUARDED-DRAWDOWN)

### 4.1 Options considered

1. Log-only (status quo): rejected. This is exactly what failed in BUG-A.
2. Abort the whole patch: rejected. A single bad resource would block legitimate
   patches to the other resources (tech, facilities, contracts), and skipping the
   patch entirely leaves KSP and the ledger diverged in unpredictable ways.
3. Defer + re-trigger: rejected as primary. Re-triggering the same recalc on the
   same buggy ledger reproduces the same target; it would oscillate or spin.
4. CLAMP the downward patch so live state never drops below its current value
   (treat the suspicious target as "at least current"): RECOMMENDED.
5. Snapshot-and-warn-loudly + clamp: RECOMMENDED as the full package (clamp is the
   mechanism, snapshot/notify/persist are the observability around it).

### 4.2 Recommendation: clamp-to-current, plus loud observability

On a GUARDED-DRAWDOWN for a resource:

1. CLAMP: do not apply the negative delta. Set the effective target to the current
   live value (delta becomes 0 for that resource). The player keeps what they
   earned. This makes future leaks NON-destructive: the worst case is that live
   state stays correct while the ledger is wrong, which is recoverable, instead of
   live state being driven to a wrong value, which is not.
2. LOG: emit a WARN (or ERROR-level INFO) with the full numbers (current, would-be
   target, clamped delta, resource, reason string, time-travel signal values) so
   the leak is loud in KSP.log. This is the diagnostic that a leak exists.
3. NOTIFY (non-blocking): post a single, rate-limited `ScreenMessages.PostScreenMessage`
   in-game ("Parsek blocked a suspicious career-resource drop; your progress was
   preserved. See KSP.log."). One-shot per session per resource to avoid spam.
   ScreenMessage is already used across the codebase
   (`ParsekScenario.cs:2603`, `:6054`).
4. PERSIST a lightweight marker (section 4.4) so a repeated guarded clamp across
   scene changes does not re-spam and so the condition is visible after reload.

Why clamp rather than refuse: refusing the whole patch leaves the OTHER resources
unpatched and the ledger / KSP diverged. Clamping is surgical: only the suspicious
resource's downward move is neutralized; everything else (and any UPWARD move on
the same resource) applies normally.

### 4.3 Recoverability and self-healing

BUG-A's loss was permanent because the wipe was applied and saved. With clamp:

- The live value is preserved, so the player loses nothing.
- If the underlying ledger leak is later fixed (as BUG-A was), the next recalc
  computes the correct (now-matching) target and the clamp simply stops triggering;
  no special heal code needed. The guard is self-healing once the leak is fixed.
- The clamp is idempotent: as long as the ledger stays wrong, every recalc sees the
  same current vs target and re-clamps to a no-op (delta 0), so live state never
  drifts and the guard never oscillates.

### 4.4 Persistence concern

A guarded clamp does NOT need to "survive" in the sense of changing future
behavior: the clamp recomputes from live + ledger every time and is naturally
idempotent. What SHOULD persist is the observability marker so the notification is
one-shot and the diagnostic survives reload:

- Add a small persisted counter / last-seen field on `ParsekScenario`
  (`DrawdownGuardLastClampUT`, `DrawdownGuardClampCount`) written in OnSave / read
  in OnLoad. Purely diagnostic; the clamp decision never depends on it.
- A guard refusal must NOT need to survive reload to be correct (the live value is
  already preserved in the stock singletons, which KSP saves). Persistence is for
  humans, not for the algorithm.

------------------------------------------------------------------------

## 5. False-positive analysis (legitimate downward scenarios)

For each, show why the guard does not block it.

| Scenario | Path | Why not blocked |
| --- | --- | --- |
| Rewind-to-Separation | `ParsekScenario:3202` while `IsRewinding` | AUTHORIZED-REDUCTION via signal (1). |
| Re-fly post-invoke | `RewindInvoker:908`, `IsRewinding` true | AUTHORIZED-REDUCTION via (1). |
| Re-fly mid-session scene change | any recalc while `ActiveReFlySessionMarker != null` | AUTHORIZED-REDUCTION via (2). |
| Re-fly merge tail (tombstones) | `RecalculateAndPatchAfterTombstones` | AUTHORIZED-REDUCTION via (4); also `ActiveMergeJournal` likely non-null (3). |
| Time jump backward | `TimeJumpManager` | Time jump uses a cutoff walk; if it reduces resources it is an intended timeline reposition. Covered by signal review (see open question 7.3 - time jump may need its own flag if it can run with no rewind/marker). |
| Strategy exchange (Bail-Out Grant) | KSC live event -> committed action -> small post-commit delta | delta is the small residual, below the 50% guard bar; pool not near-collapsed. |
| Contract penalty | committed `ContractFail`/`Cancel` action | small residual; rep penalties are bounded. |
| Facility upgrade / repair cost | committed `FundsSpending` action | small residual relative to the committed cost; funds already debited before the post-commit recalc. |
| Tech node purchase | committed `ScienceSpending` action | small science residual; the big drop already happened through the committed action, not the patch. |
| Vessel build / rollout cost | `vessel-rollout` live event -> committed action | small residual. |
| Multi-channel commit | one recalc applying many committed deltas | each is committed before the patch; net target reflects them; residual delta small. |

The unifying invariant: a legitimate spend is a COMMITTED LEDGER ACTION, so the
recalc target already includes it and the patch delta is the small reconciliation
residual, not the whole spend. A leak is the opposite: an earning that NEVER became
a ledger action, so the target is below live by the whole missing amount. The 50% +
absolute guard bar plus the time-travel signal separates these cleanly.

Residual false-positive risk: a player who, in a single recalc with no time travel,
legitimately spends more than 50% of a large pool AND more than the absolute floor
in one un-deferred move. This is implausible in stock play (such spends are
committed actions reconciled incrementally), but section 7.1 calls it out for the
human to weigh, and the clamp is non-destructive even if it triggers (the player
keeps the resource; worst case is the next correct recalc reapplies the intended
spend, or the player re-spends).

------------------------------------------------------------------------

## 6. Per-resource handling and which channels get protection

- SCIENCE (R&D pool): full guard (clamp + observability).
- SCIENCE (per-subject, `PatchPerSubjectScience`): guard the AGGREGATE only at the
  pool level. Do NOT clamp individual subject totals (a subject can legitimately
  drop to 0 on a revert, and per-subject clamping would fight legitimate
  rewrites). The pool-level clamp is the safety net; per-subject stays
  ledger-driven. Document this explicitly so a future reader does not "fix" the
  asymmetry.
- FUNDS: full guard (clamp + observability).
- REPUTATION: guard with a looser threshold (section 3.4); lean on time-travel
  signal. Clamp to current on a near-collapse with no time-travel context.
- TECH TREE: NOT guarded by clamp. Tech is set-membership, not a magnitude, and is
  only patched on cutoff/rewind walks (`techPatchCutoff` non-null), which are the
  time-travel paths. A non-rewind walk passes `targetTechIds = null` and PatchTechTree
  no-ops. So tech downgrades only happen on already-authorized paths. Add a log-only
  observability line if a non-cutoff walk ever produces a non-null target (defense
  against a future regression), but no clamp.
- FACILITIES: NOT guarded by clamp in v1. Facility downgrades are level changes, not
  magnitudes; the BUG-A class (silent magnitude wipe) does not apply the same way.
  Revisit only if a facility-downgrade leak is observed.
- CONTRACTS: NOT guarded by clamp. Contract state is structural; out of scope for a
  magnitude drawdown guard.

------------------------------------------------------------------------

## 7. Open questions / decisions for the human

7.1 Guard threshold numbers. Proposed: WARN stays at 10% / 1000 (unchanged);
    CLAMP at 50% AND an absolute floor (5000 funds, 50 science, looser for rep).
    Are these acceptable, or should the clamp bar be lower (more protective, higher
    false-positive risk) or higher (more permissive)? The absolute floor is what
    keeps small-career legitimate spends safe.

7.2 Reputation handling. Rep legitimately falls in normal play (penalties) and is a
    SET not an ADD. Should rep be clamped at all, or only WARN + notify (because a
    rep "wipe" is less catastrophic and harder to distinguish from legitimate
    multi-penalty collapse)? Recommendation: WARN + notify only for rep in v1, add
    clamp later if a rep-leak is observed. Confirm.

7.3 Time jump. Does `RecalculateAndPatchForTimeJump` ever run with NO rewind
    context and NO re-fly marker while legitimately reducing resources? If a
    backward time jump can reduce funds/science without `IsRewinding`, it needs to
    be added as an explicit authoritative-reduction caller (a fifth signal), or the
    guard would clamp it. Needs a code/playtest confirmation of the time-jump
    resource semantics before wiring.

7.4 Notification UX. Is a `ScreenMessages.PostScreenMessage` acceptable, or should
    a guarded clamp also raise a `PopupDialog` the first time (more intrusive but
    harder to miss)? Recommendation: ScreenMessage one-shot; PopupDialog feels too
    heavy for a defense-in-depth tripwire.

7.5 Should the clamp be behind a default-ON setting (section 8 phased rollout) or
    unconditional from v1? Recommendation: ship behind a default-ON
    `guardSuspiciousDrawdown` setting so a playtester can disable it if it ever
    mis-fires, then consider removing the toggle once proven.

------------------------------------------------------------------------

## 8. Implementation outline

### 8.1 Files and functions to add / change

- `Source/Parsek/GameActions/KspStatePatcher.cs`
  - Add `internal static bool IsGuardableDrawdown(double delta, double currentPool,
    double fraction, double absoluteFloor)` (pure, unit-tested).
  - Add `internal static double ApplyDrawdownGuard(double delta, double currentValue,
    double target, bool authoritativeReduction, bool guardEnabled, string resource,
    out bool clamped)` (pure decision: returns the delta to actually apply -
    `delta` unchanged when safe/authorized, `0` when clamped - and reports
    `clamped`). All logging done by the caller from the returned `clamped` + numbers
    so the pure helper stays log-free and testable, OR log inside with a TestSink
    (match the existing `IsSuspiciousDrawdown`/WARN pattern - the existing patcher
    logs inside, so keep that style and assert via `TestSinkForTesting`).
  - Thread a new parameter `bool authoritativeReduction` (and optionally
    `bool guardEnabled`) through `PatchAll` -> `PatchScience` / `PatchFunds` /
    `PatchReputation`. Apply the guarded delta instead of the raw delta.
  - `PatchScience`: compute `delta`, then
    `delta = ApplyDrawdownGuard(...)` before `AddScience(delta, ...)`.
  - `PatchFunds`: same before `AddFunds(delta, ...)`.
  - `PatchReputation`: convert the SET into a guarded check (compute implied delta
    `target - current`, guard, and if clamped, SetReputation(current) i.e. no-op).
  - Keep `IsSuspiciousDrawdown` and the existing WARN unchanged (the WARN is the
    10% tripwire; the clamp is the 50%+floor action). They compose.
  - Extend `ResetForTesting` for any new one-shot notification guard bool.

- `Source/Parsek/GameActions/LedgerOrchestrator.cs`
  - Add `internal static bool IsAuthoritativeReduction(bool isRewinding,
    bool hasReFlyMarker, bool hasMergeJournal, bool tombstonePath)` (pure).
  - In `RecalculateAndPatchCore` / `ApplyRecalculatedStateToKsp`, compute
    `authoritativeReduction` from `RewindContext.IsRewinding`,
    `ParsekScenario.Instance?.ActiveReFlySessionMarker != null`,
    `ParsekScenario.Instance?.ActiveMergeJournal != null`, and a `tombstonePath`
    flag threaded from `RecalculateAndPatchAfterTombstones`. Pass it to `PatchAll`.
  - Do NOT reuse `suppressSuspiciousDrawdownWarnings` for this; keep them separate
    parameters (the suppress flag controls the noisy 10% WARN; the new flag
    authorizes the 50% clamp bypass).

- `Source/Parsek/ParsekSettings.cs`
  - Add `public bool guardSuspiciousDrawdown = true;` with a CustomParameterUI under
    the appropriate section. Read via `ParsekSettings.Current` (null-safe; default
    to ON when settings unavailable, matching the project's null-safe pattern).

- `Source/Parsek/ParsekScenario.cs`
  - Optional diagnostic persistence: `DrawdownGuardClampCount` /
    `DrawdownGuardLastClampUT` in OnSave/OnLoad (per the Post-Change Checklist for
    serialized fields). Purely observability.

### 8.2 The seam

The guard hooks in at the LOWEST point that still knows the per-resource current vs
target: inside `PatchScience` / `PatchFunds` / `PatchReputation`, immediately
before the `AddScience` / `AddFunds` / `SetReputation` call. The authoritative-
reduction decision is computed once at the `RecalculateAndPatchCore` boundary and
passed down. This keeps the magnitude logic next to the live read (no staleness)
while keeping the scene-state lookup out of the pure writer.

### 8.3 Logging and observability (per CLAUDE.md)

Every decision logged:
- Safe (delta >= 0 or within tolerance): existing INFO/VERBOSE lines unchanged.
- Authorized reduction: VERBOSE line noting "large drawdown authorized by
  time-travel context (rewinding=.., reFlyMarker=.., mergeJournal=..,
  tombstone=..)" with the numbers.
- Guarded clamp: WARN line "PatchX: GUARDED DRAWDOWN clamped delta=.. current=..
  wouldBeTarget=.. resource=.. (no time-travel context) - live value preserved;
  ledger may be missing an earning channel" plus a one-shot ScreenMessage.
- Tag `KspStatePatcher` (existing). Subsystem-tagged, numeric, InvariantCulture.

### 8.4 Persistence / Post-Change Checklist

If the diagnostic fields are added: verify `ParsekScenario` OnSave/OnLoad, verify
test generators are unaffected (no new recording schema), run `dotnet test`. No
recording-format change is involved (CurrentRecordingSchemaGeneration untouched).

------------------------------------------------------------------------

## 9. Idempotency / repeat behavior

A corrupted ledger recalcs on every scene change. The guard must behave sanely on
repeat:

- The clamp is idempotent: clamped delta is 0, so live value is unchanged; the next
  recalc reads the same current and the same wrong target and re-clamps to 0 again.
  No drift, no oscillation, no slow leak.
- The WARN line is per-resource and fires each recalc by design (it documents the
  ongoing leak), but it is gated to GUARDED cases only, so a healthy career emits
  nothing.
- The ScreenMessage is one-shot per session per resource (a static "already
  notified" bool reset in `ResetForTesting` and on scene-quit), so the player is not
  spammed across repeated scene changes.

------------------------------------------------------------------------

## 10. Testing strategy

### 10.1 xUnit (pure helpers + log-assertion), the project pattern

Add to `Source/Parsek.Tests/` (likely a new `DrawdownGuardTests.cs`, plus extend
`PatchFundsSanityTests.cs`). Use `ParsekLog.TestSinkForTesting` capture and
`KspStatePatcher.SuppressUnityCallsForTesting = true` (existing pattern). All
classes touching shared static state get `[Collection("Sequential")]`.

`IsGuardableDrawdown`:
- big drop from big pool above absolute floor -> true.
- big percent drop but below absolute floor -> false (small career protected).
- above absolute floor but below percent bar -> false.
- positive delta -> false.
- pool below floor -> false.
- boundary cases at the fraction and the absolute floor.

`IsAuthoritativeReduction`:
- all four inputs false -> false.
- each input true individually -> true.
- combinations -> true.

`ApplyDrawdownGuard` (pure decision):
- safe (positive delta) -> returns delta unchanged, clamped=false.
- guardable + authoritative -> returns delta unchanged, clamped=false (time travel).
- guardable + NOT authoritative + guard enabled -> returns 0, clamped=true.
- guardable + NOT authoritative + guard DISABLED (setting off) -> returns delta,
  clamped=false (but WARN still emitted - assert the WARN line).
- below-floor drop with no context -> returns delta unchanged (not guardable).

Log-assertion (drive `PatchScience`/`PatchFunds` with `SuppressUnityCallsForTesting`
where the singletons are null-guarded, OR exercise the pure path and assert on the
emitted lines): assert the GUARDED-DRAWDOWN WARN contains the resource, current,
would-be target, and "live value preserved"; assert the AUTHORIZED line contains
the signal values; assert no clamp line on a healthy delta.

One-shot notification: assert the ScreenMessage / one-shot bool fires once and not
on the second guarded recalc within a session.

### 10.2 In-game tests (`InGameTests/RuntimeTests.cs`)

Needed because the real `AddScience`/`AddFunds`/`SetReputation` and the live
singletons only exist in KSP:

- `[InGameTest(Category = "Ledger", Scene = GameScenes.SPACECENTER)]`: seed a known
  science/funds total, force a recalc whose ledger target is artificially below live
  (via a test seam, for example a one-shot "force target" hook), and assert the live
  `ResearchAndDevelopment.Instance.Science` / `Funding.Instance.Funds` are UNCHANGED
  (clamped) and the WARN fired.
- A paired test with `RewindContext`/marker forced true asserting the same
  too-low target IS applied (authorized reduction) so the in-game path proves the
  signal wiring, not just the pure helper.

### 10.3 Regression guard

Add a test asserting that the existing legitimate paths (a small post-commit
reconciliation delta, a strategy-exchange-sized residual) do NOT clamp, to lock in
the false-positive analysis.

------------------------------------------------------------------------

## 11. Risks, edge cases, phased rollout

### 11.1 Risks

- Mis-tuned thresholds blocking a legitimate large spend. Mitigated by: the
  absolute floor, the committed-action invariant (spends are reconciled
  incrementally, so the residual is small), the default-ON setting toggle, and the
  non-destructive nature of clamp.
- A missing time-travel signal causing a legitimate reduction to clamp. Mitigated
  by enumerating all four signals (section 2.4) and the open question on time jump
  (7.3). If a signal is missed, the failure mode is a clamped legitimate reduction
  (player keeps resources they were supposed to lose) - annoying but not
  career-destroying, and visible in the loud WARN.
- Rep semantics (SET vs ADD, legitimate negatives). Mitigated by the looser rep
  threshold and the v1 recommendation to WARN+notify only for rep.

### 11.2 Edge cases

- HasSeed false (early load): patch already skipped; guard never runs. Fine.
- Sandbox mode (null singletons): patch already no-ops; guard never runs. Fine.
- First-ever seed establishment: the seed path sets the initial value; the guard
  only acts on subsequent reductions, not on seeding. Verify the guard is keyed on
  the patch delta AFTER HasSeed is true.
- Loading a save authored elsewhere where live state legitimately differs: OnKspLoad
  recalc. This is the one case where a downward patch with no time-travel context
  could be legitimate (the loaded .sfs already has the lower value, and the ledger
  is the source of truth). But on a fresh load, KSP's singletons are loaded FROM the
  .sfs, so live == saved, and the recalc should match (delta ~0). A large drawdown
  on load is itself the BUG-A signature. Treat OnKspLoad like any other path:
  guard it. (Confirm in playtest that a normal load produces delta ~0.)

### 11.3 Phased rollout

- Phase 1: ship the pure helpers + the clamp behind default-ON
  `guardSuspiciousDrawdown`, with loud WARN + one-shot ScreenMessage, for science
  and funds only. Rep stays WARN+notify (no clamp). xUnit + in-game tests.
- Phase 2 (after a clean career playtest confirms zero false clamps): consider rep
  clamp, and consider removing the toggle once proven.
- In-game validation required before declaring done: a full career playtest with no
  time travel must produce ZERO guarded clamps and ZERO false WARNs (the healthy
  baseline), and a deliberately-leaked ledger (or a forced too-low target) must
  clamp and preserve live state. Verify the deployed DLL per the CLAUDE.md recipe
  before the playtest.

------------------------------------------------------------------------

## 12. Summary

The recalc patch trusts the ledger and applies downward deltas unconditionally; the
only existing protection is a log-only WARN that is even silenced on the
scene-change path. The guard adds a clamp-to-current safety net keyed on a clean
three-way classification: positive/small deltas apply; large downward deltas WITH a
time-travel context (rewind in progress, active re-fly marker, active merge journal,
or the tombstone tail) apply as intended; large downward deltas with NO time-travel
context are clamped so live career state can never again be silently wiped. The
clamp is surgical (per resource), idempotent (no oscillation), non-destructive
(player keeps progress), and self-healing (stops triggering once the underlying leak
is fixed). It is loud (WARN + one-shot in-game notice) so leaks are caught, and it
explicitly does not block the legitimate spends and time-travel reductions
enumerated in section 5.
