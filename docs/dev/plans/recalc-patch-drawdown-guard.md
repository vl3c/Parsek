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

### 2.6 Resolved evidence: no legitimate non-time-travel downward delta exists

This section records the second-pass investigation that resolved the open questions.
The conclusion drives the threshold model (3.3) and the reputation decision (3.4).

#### 2.6.1 A "time jump" is always forward; backward warp routes through a rewind

`TimeJumpManager` (the only caller of `RecalculateAndPatchForTimeJump`) executes a
DISCRETE FORWARD UT skip. `ExecuteJump` / `ExecuteForwardJump` both abort unless
`IsValidJump(t0, target)` holds, and `IsValidJump` is `target > current`
(`TimeJumpManager.cs:218-228`, called at `:276` and `:429`). The recalc cutoff is the
post-jump (later) UT (`RecalculateLedgerAfterTimeJump` -> `RecalculateAndPatchForTimeJump`
-> `RecalculateAndPatchForCurrentTimelineUT(targetUT)`, `TimeJumpManager.cs:61-93`,
`LedgerOrchestrator.cs:1377-1379`).

The Warp-to-time feature (`docs/dev/done/plans/warp-to-time-timeline.md`) confirms
the only way to move the clock BACKWARD is to first run a Rewind-to-Launch
(`RecordingStore.InitiateRewind` -> `HandleRewindOnLoad` -> `RewindContext`), then
forward-jump. The rewind sets `RewindContext.IsRewinding` and lands in the Space
Center via the warp consumer, so a backward warp is covered by signal (1). The
follow-up forward jump runs from the post-rewind state and only ever advances.

Therefore the time-jump recalc cutoff only moves LATER, which can only INCLUDE MORE
committed actions, never fewer. No fifth authoritative-reduction signal is needed for
time jump. (Resolves Q3.)

#### 2.6.2 Future committed COSTS are reserved, not applied-on-crossing

`FundsModule.GetAvailableFunds()` returns `min(projected balance from cutoff
forward)` (`FundsModule.cs:596-613`; cashflow projection in
`TryGetProjectionDelta` / `SetProjectedAvailable`, `cs:623-697`). The economic model
(`docs/dev/done/design-going-back-in-time.md:32-46`,
`docs/dev/done/milestone-resource-reservation.md`) is explicit: future committed
recording COSTS are subtracted from "available" UP FRONT as a reservation
("available = save resources minus committed future costs"), while resource deltas
EARNED by recordings "are applied at the correct UT during ghost playback"
(`design-going-back-in-time.md:46,58`).

So crossing a committed future spend during a forward jump produces NO new downward
delta (the projected-min already counted it); crossing a committed future earning
produces an UPWARD delta. A forward jump never drives available funds/science below
current. (Reinforces 2.6.1.)

#### 2.6.3 Every legitimate live spend/penalty is reconciled to ~0 at the recalc

KSP applies the spend/penalty to the live singleton FIRST, then Parsek captures it:
`GameStateRecorder.OnFundsChanged(double newFunds, TransactionReasons)` fires with
the post-debit value (`GameStateRecorder.cs:985-1020`), `OnScienceChanged` likewise
(`cs:1073`), and rep penalties are captured post-curve
(`ReputationModule.cs:115-160`). The action lands in the ledger BEFORE the
post-commit / live-event recalc, so the recalc target already includes the spend and
the patch delta is the small reconciliation residual (~0), not the spend magnitude.
This is the load-bearing fact behind "clamp any unexplained downward delta": there is
no path on which a legitimate spend arrives at the patch AS a large downward delta.

#### 2.6.4 The patch only fires when ledger-mirrors-live should hold

`GetKspPatchDeferralReason` (`LedgerOrchestrator.cs:2044`) skips the patch entirely
while a live recorder / active uncommitted tree / pending tree exists (only the
cutoff/rewind paths bypass). So the dangerous downward patch fires only AFTER commit
or on a settled scene, exactly when the expected delta is ~0. Combined with 2.6.1-3,
any downward delta past rounding epsilon on a non-time-travel path is a corruption.

#### 2.6.5 Setting-toggle precedent (Q5)

The recalc/patch mechanism itself (the core correctness system) is UNCONDITIONAL:
there is no setting gating whether the ledger patches KSP state. The codebase gates
behind a default-ON toggle only GAMEPLAY-RESTRICTION features the player might want
off (`blockCommittedActions`, `ParsekSettings.cs:68-70`, which blocks the player's
own stock-UI actions) and DEV/DIAGNOSTIC features (tracing, splines). Pure
correctness / fidelity behaviors with no gameplay downside are unconditional and
explicitly carry no setting (the map-view trajectory polyline,
`ParsekSettings.cs:172-174`: "always on (no setting)"; anchor taxonomy default-on,
`cs:170`). A corruption-prevention safety net only ever PREVENTS a wipe and never
restricts legitimate play, so by this precedent it is unconditional (no toggle).
(Resolves Q5.)

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

### 3.3 The drawdown threshold (RESOLVED: clamp any unexplained downward delta)

The first draft proposed a percentage + absolute threshold (50% + a floor) to avoid
blocking large legitimate spends. Investigation (section 2.6, with file:line
evidence) overturned that model. The correct model is: **with no time-travel
context active, clamp ANY downward delta past a small rounding epsilon.** There is
no legitimate "large but allowed" downward patch on a non-time-travel path, so a
percentage band would only leave a hole through which medium-sized corruptions slip.

Why a threshold band is the WRONG model here:

- The deferral guard (`GetKspPatchDeferralReason`, section 2.2) means the dangerous
  patch only fires AFTER commit / on a settled scene, when the ledger is supposed to
  mirror live exactly, so the expected delta is ~0.
- Every legitimate downward movement (facility upgrade, tech purchase, rollout cost,
  strategy exchange, contract penalty, kerbal hire, rep penalty) is a COMMITTED
  LEDGER ACTION. KSP debits live state first, then Parsek captures the change as an
  action and recalcs; the target now includes that action, so the patch delta is the
  small reconciliation residual, not the spend (section 2.6.3, evidence:
  `GameStateRecorder.OnFundsChanged(double newFunds, ...)` fires post-debit at
  `GameStateRecorder.cs:985`).
- Future committed COSTS are RESERVED, not applied-on-crossing: `GetAvailableFunds`
  returns the minimum projected balance from the cutoff forward
  (`FundsModule.cs:606-613`, design `docs/dev/done/milestone-resource-reservation.md`
  and `design-going-back-in-time.md:34-46`), so a forward time jump past a committed
  spend produces NO new downward delta (the reservation already counted it). Future
  committed EARNINGS apply upward when crossed.
- A "time jump" is ALWAYS forward (`TimeJumpManager.IsValidJump` requires
  `target > current`, `TimeJumpManager.cs:218-228`); backward warp routes through a
  rewind, which sets the time-travel signal (section 2.6.1). Forward = up or flat.

Conclusion: on a non-time-travel path, a downward delta beyond rounding noise can
ONLY be a bug (a missing earning channel, the BUG-A class). So the guard predicate is
simply:

```
internal static bool IsGuardableDrawdown(double delta, double epsilon)
    => delta < -epsilon;
```

Epsilon = the resource's existing patch no-op epsilon, so the guard never fights the
patch's own rounding tolerance:
- Funds: 0.01 (matches `PatchFunds` no-op check at `KspStatePatcher.cs:649`).
- Science: 0.001 (matches `PatchScience` no-op check at `KspStatePatcher.cs:102`).
- Reputation: 0.01 (matches `PatchReputation` no-op check at `KspStatePatcher.cs:711`).

The existing `IsSuspiciousDrawdown` (10% / 1000) stays as the louder WARN tripwire
for the legacy log-watchers, but the CLAMP no longer depends on it: any downward
delta past epsilon with no time-travel context is clamped. The two compose (a small
downward leak now gets clamped even though it would never have tripped the 10%
WARN), which is strictly safer than the original threshold-band design.

No absolute floor, no percentage. "Never clamp a legitimate change, always clamp a
corruption" is achieved because the ONLY non-time-travel downward delta past epsilon
is a corruption.

### 3.4 Per-resource specifics

- SCIENCE: pool can be legitimately spent to ~0 by buying tech, but each purchase
  is a committed `ScienceSpending` action, so the post-commit patch delta is ~0.
  Any downward delta past epsilon with no time-travel context = leak. Per-subject
  science (`PatchPerSubjectScience`) is handled at the pool level only (section 6).
- FUNDS: same shape; spends (facility upgrade, rollout, hire, strategy setup) are
  committed actions reconciled to ~0. Guard the net `AddFunds` delta past epsilon.
- REPUTATION (RESOLVED: treat like funds/science). The first draft called rep
  "special" and proposed WARN-only. Investigation overturned that: every legitimate
  rep drop (contract fail/cancel, kerbal-death penalty, strategy currency exchange)
  is a committed ledger action that KSP applies first and Parsek captures with the
  post-curve magnitude (`ReputationModule.ProcessRepPenalty` /
  `ProcessContractPenaltyRep` at `ReputationModule.cs:115-160`; the Strategy source
  is pre-curved and bypasses re-application, `cs:122-135`), so the post-commit recalc
  target already includes it and the patch delta is ~0. Rep is therefore NOT
  semantically different from funds/science. The only differences are mechanical and
  do not change the decision:
  - It uses `SetReputation(target)` not an add. The guard expresses the clamp as
    "do not let the set drive below current": if `target < current - epsilon` and no
    time-travel context, `SetReputation(current)` (no-op) instead of `target`.
  - It can be negative. "Downward" is `target < current` regardless of sign, and the
    clamp sets the effective target to current. Works identically for negative rep.
  So rep gets the SAME clamp as funds/science, just applied to the SET semantics.

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
3. NOTIFY (non-blocking, FINAL - player decision 1): post one short, transient
   native ScreenMessage via `ParsekLog.ScreenMessage(message, duration)` - the SAME
   mechanism Parsek already uses for "Recording STARTED" (`ParsekLog.cs:483-495` ->
   `ScreenMessages.PostScreenMessage(..., ScreenMessageStyle.UPPER_CENTER)`; the
   "Recording STARTED" call is `ParsekLog.ScreenMessage("Recording STARTED", 2f)` at
   `FlightRecorder.cs:6187`). Duration ~2-3s. NOT a PopupDialog (the one-time-popup
   option was dropped). Proposed wording, plain and naming what was protected (the
   `ParsekLog.ScreenMessage` helper auto-prefixes `[Parsek] `):
   - Funds: `"Kept your earned funds"`
   - Science: `"Kept your earned science"`
   - Reputation: `"Kept your earned reputation"`
   (So the on-screen text reads e.g. `[Parsek] Kept your earned funds`. Keep it this
   short; the WARN log carries the numbers.) One-shot per session per resource to
   avoid spam on repeated recalcs (section 9). This path has a unit-test seam
   (`ParsekLog.ScreenMessageSinkForTesting`, `ParsekLog.cs:80,485-489`), so the
   notification is directly assertable in xUnit.
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
| Time jump (always forward) | `TimeJumpManager` -> `RecalculateAndPatchForTimeJump` | Forward only (`IsValidJump`, `TimeJumpManager.cs:218-228`); later cutoff includes more actions, never fewer. Future costs are reserved, not applied-on-crossing. No downward delta. (2.6.1-2) |
| Backward warp | `WarpToTime` -> Rewind-to-Launch then forward jump | The rewind sets `IsRewinding` -> AUTHORIZED-REDUCTION via signal (1); the forward jump only advances. (2.6.1) |
| Strategy exchange (Bail-Out Grant) | KSC live event -> committed action | KSP debits live first, action captured post-curve, recalc delta ~0. (2.6.3) |
| Contract penalty | committed `ContractFail`/`Cancel` action | KSP applies penalty first, captured as a committed action, recalc delta ~0. (2.6.3) |
| Facility upgrade / repair cost | committed `FundsSpending` action | funds debited live first, action committed, recalc delta ~0. (2.6.3) |
| Tech node purchase | committed `ScienceSpending` action | science spent live first, action committed, recalc delta ~0. (2.6.3) |
| Vessel build / rollout cost | `vessel-rollout` live event -> committed action | debited live first, committed, recalc delta ~0. (2.6.3) |
| Multi-channel commit | one recalc applying many committed deltas | each committed before the patch; net target reflects them; residual ~0. |

The unifying invariant: a legitimate spend is a COMMITTED LEDGER ACTION that KSP
applies to live state BEFORE Parsek captures it, so the recalc target already
includes it and the patch delta is ~0, not the spend. A leak is the opposite: an
earning that NEVER became a ledger action, so the target is below live by the whole
missing amount. The time-travel signal authorizes the only legitimate large downward
moves (rewind/re-fly/tombstone). Everything else downward past rounding epsilon, with
no time-travel context, is a corruption. There is NO legitimate scenario that arrives
at the non-time-travel patch as a downward delta past epsilon (section 2.6), so the
"clamp any unexplained downward delta" rule has zero false positives by construction.

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
- REPUTATION: full guard, SAME rule as funds/science (resolved, section 3.4). The
  clamp is expressed against the `SetReputation` semantics ("do not set below
  current") and measures "downward" as `target < current - epsilon` regardless of
  sign so negative rep is handled. Every legitimate rep drop is a committed action
  reconciled to ~0, so the clamp never blocks a genuine penalty.
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

## 7. Decisions (all RESOLVED, no open questions)

Every decision is settled: four resolved from code + design docs, two answered by
the player. Nothing in this plan is left open.

### 7.1 Resolved from code/docs (evidence in section 2.6)

- Q1 / threshold model. RESOLVED: clamp ANY downward delta past the resource's
  rounding epsilon when no time-travel context is active; no percentage, no absolute
  floor. There is no legitimate non-time-travel downward delta past epsilon
  (2.6.1-4), so a threshold band would only leave a hole for medium corruptions.
  (Section 3.3.)
- Q2 / reputation. RESOLVED: treat rep exactly like funds/science (full clamp against
  the `SetReputation` semantics). Every legitimate rep drop is a committed action
  reconciled to ~0; rep is not semantically special, only mechanically different (set
  vs add, can be negative), and both are handled. (Sections 3.4, 6.)
- Q3 / time jump. RESOLVED: a time jump is always forward and a backward warp routes
  through a rewind (which sets the signal), so the time-jump recalc never legitimately
  reduces resources with no time-travel context. No fifth signal needed. (Section 2.6.1.)
- Q4-internal / patch is the right seam. RESOLVED: the guard lives inside
  PatchScience / PatchFunds / PatchReputation (section 8.2).

### 7.2 Player decisions (FINAL)

PLAYER DECISION 1 - Notification. The player chose: use the SAME native, short,
transient on-screen message Parsek already shows for "Recording STARTED", i.e.
`ParsekLog.ScreenMessage(message, duration)` (`ParsekLog.cs:483-495`;
"Recording STARTED" precedent at `FlightRecorder.cs:6187`). So when the guard
prevents a wipe, it posts one short ~2-3s UPPER_CENTER ScreenMessage naming what was
protected (wording in section 4.2 step 3). The one-time-popup / PopupDialog option was
DROPPED. The notification is unit-test assertable via the existing
`ParsekLog.ScreenMessageSinkForTesting` seam (`ParsekLog.cs:80,485-489`).

PLAYER DECISION 2 - No toggle. The player chose: the guard is UNCONDITIONAL / always
on (matching the Q5 code finding and the always-on-correctness precedent, 2.6.5).
There is NO setting that can disable the protection. (A dev rollout MAY stage
clamp-vs-warn-only during development, but the SHIPPED behavior is fixed: always
clamp + always post the short ScreenMessage + always WARN-log. No
`guardSuspiciousDrawdown` field, no `protectEarnedProgress` checkbox, no
notification-mode enum.)

### 7.3 The single shipped behavior

When the recalc would drive live funds, science, or reputation DOWN past the rounding
epsilon AND no time-travel context (rewind / re-fly marker / merge journal / tombstone
tail) is active, the guard ALWAYS:

1. clamps the patch so the player keeps what was earned (no downward write), and
2. posts one short native ScreenMessage naming the protected resource, and
3. emits a WARN to KSP.log with the full numbers.

No configuration, no popup, no opt-out. Time-travel reductions still apply normally.

------------------------------------------------------------------------

## 8. Implementation outline

### 8.1 Files and functions to add / change

- `Source/Parsek/GameActions/KspStatePatcher.cs`
  - Add `internal static bool IsGuardableDrawdown(double delta, double epsilon)
    => delta < -epsilon;` (pure, unit-tested). No fraction, no floor (section 3.3).
  - Add `internal static double ApplyDrawdownGuard(double delta, double epsilon,
    bool authoritativeReduction, string resource, out bool clamped)` (pure decision:
    returns the delta to actually apply - `delta` unchanged when not guardable or when
    authorized, `0` when clamped - and reports `clamped`). Keep the logging in the
    caller-visible patch methods (match the existing `IsSuspiciousDrawdown`/WARN
    in-method pattern; assert via `TestSinkForTesting`).
  - Thread a new parameter `bool authoritativeReduction` through `PatchAll` ->
    `PatchScience` / `PatchFunds` / `PatchReputation`. Apply the guarded delta.
  - `PatchScience`: compute `delta`, then `delta = ApplyDrawdownGuard(delta, 0.001,
    authoritativeReduction, "science", out clamped)` before `AddScience(delta, ...)`.
  - `PatchFunds`: same with epsilon 0.01 before `AddFunds(delta, ...)`.
  - `PatchReputation`: compute implied delta `target - current`; if
    `ApplyDrawdownGuard(delta, 0.01, authoritativeReduction, "reputation", out clamped)`
    returns 0 (clamped), call `SetReputation(current)` (no-op) instead of `target`.
    "Downward" is `target < current - epsilon` regardless of sign (negative rep OK).
  - Keep `IsSuspiciousDrawdown` and the existing WARN unchanged (legacy 10% tripwire);
    the clamp is independent and fires on any downward delta past epsilon. They compose
    (the clamp is strictly broader).
  - On a clamp, post the one-shot ScreenMessage via `ParsekLog.ScreenMessage(text, 2.5f)`
    (player decision 1; wording in section 4.2 step 3). Guard the one-shot with a
    per-resource static bool so repeated recalcs do not re-toast.
  - Extend `ResetForTesting` for the one-shot per-session notification bools.

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
    authorizes the clamp bypass on time-travel paths).

- `Source/Parsek/ParsekSettings.cs`
  - NO CHANGES (player decision 2). The guard is unconditional: no
    `guardSuspiciousDrawdown` field, no `protectEarnedProgress` checkbox, no
    notification-mode enum, no `CustomParameterUI`. The clamp + ScreenMessage + WARN
    always fire; there is nothing for the player to configure.

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
  ledger may be missing an earning channel" ALWAYS, plus the one-shot-per-session
  short ScreenMessage via `ParsekLog.ScreenMessage` (player decision 1; unconditional,
  there is no notification-mode setting).
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

`IsGuardableDrawdown(delta, epsilon)`:
- delta more negative than -epsilon -> true.
- delta within [-epsilon, 0] -> false (rounding noise).
- delta == -epsilon exactly -> false (strict `< -epsilon`).
- positive delta -> false.
- a tiny downward delta (smaller magnitude than the old 10% WARN but past epsilon)
  -> true (proves the clamp is broader than the legacy WARN).

`IsAuthoritativeReduction`:
- all four inputs false -> false.
- each input true individually -> true.
- combinations -> true.

`ApplyDrawdownGuard` (pure decision):
- safe (positive delta) -> returns delta unchanged, clamped=false.
- within-epsilon downward -> returns delta unchanged, clamped=false.
- guardable + authoritative -> returns delta unchanged, clamped=false (time travel).
- guardable + NOT authoritative -> returns 0, clamped=true.

Log-assertion (drive `PatchScience`/`PatchFunds` with `SuppressUnityCallsForTesting`
where the singletons are null-guarded, OR exercise the pure path and assert on the
emitted lines): assert the GUARDED-DRAWDOWN WARN contains the resource, current,
would-be target, and "live value preserved"; assert the AUTHORIZED line contains
the signal values; assert no clamp line on a healthy delta.

One-shot notification: install `ParsekLog.ScreenMessageSinkForTesting`
(`ParsekLog.cs:80,485-489`) to capture posted messages, then assert exactly ONE
message per resource is posted on the first guarded recalc, its text names the
protected resource ("Kept your earned funds" / science / reputation), and NO further
message is posted on a second guarded recalc within the same session. Reset the
one-shot bools via `ResetForTesting` in Dispose.

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

- A legitimate spend reaching the patch as a downward delta and being clamped.
  Investigation shows this cannot happen on a non-time-travel path: spends are
  committed actions reconciled to ~0 (2.6.3), future costs are reserved not
  applied-on-crossing (2.6.2), and time jumps only advance (2.6.1). The clamp is also
  non-destructive, so even an unforeseen trigger only preserves the player's resource.
- A missing time-travel signal causing a legitimate reduction to clamp. Mitigated by
  enumerating all four signals (section 2.4) and confirming the time-jump path needs
  no fifth signal (2.6.1). If a signal is ever missed, the failure mode is a clamped
  legitimate reduction (player keeps resources they were supposed to lose) - annoying
  but not career-destroying, and loud in the WARN, so it surfaces fast.
- Rep semantics (SET vs ADD, legitimate negatives). Mitigated by expressing the clamp
  against the set ("do not set below current") and measuring downward as
  `target < current - epsilon` regardless of sign (sections 3.4, 6).

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

The shipped behavior is fixed and unconditional (player decision 2): always clamp +
always post the short ScreenMessage + always WARN-log, for funds, science, AND
reputation from v1 (rep is not special, section 3.4). There is no setting and no
feature gate. Phasing is purely a DEV strategy for validation depth, not anything the
player ever sees:

- Phase 1 (dev): land the pure helpers + the unconditional clamp + the one-shot
  ScreenMessage + the WARN, with xUnit coverage. A dev MAY temporarily run a
  warn-only build locally to compare clamp-vs-warn behavior, but warn-only never
  ships.
- Phase 2 (after a clean career playtest): confirm zero false clamps in the wild.
- In-game validation required before declaring done: a full career playtest with no
  time travel must produce ZERO guarded clamps and ZERO false WARNs (the healthy
  baseline); a deliberately-leaked ledger (or a forced too-low target) must clamp,
  preserve live state, AND post the ScreenMessage, on funds, science, AND rep; and a
  real rewind / re-fly / tombstone reduction must still apply (authorized). Verify
  the deployed DLL per the CLAUDE.md recipe before the playtest.

------------------------------------------------------------------------

## 12. Summary

The recalc patch trusts the ledger and applies downward deltas unconditionally; the
only existing protection is a log-only WARN that is even silenced on the
scene-change path. The guard adds an unconditional clamp-to-current safety net keyed
on a clean classification: a downward delta past the resource's rounding epsilon is
clamped UNLESS a time-travel context is active (rewind in progress, active re-fly
marker, active merge journal, or the tombstone tail), in which case it applies as
intended. Investigation proved there is no legitimate non-time-travel downward delta
past epsilon (spends are committed actions reconciled to ~0; future costs are reserved
not applied-on-crossing; time jumps only advance; backward warp routes through a
rewind), so "clamp any unexplained downward delta" has zero false positives by
construction and needs no percentage/floor threshold. The clamp covers funds,
science, and reputation identically (rep is not special). It is surgical (per
resource), idempotent (no oscillation), non-destructive (player keeps progress), and
self-healing (stops triggering once the underlying leak is fixed). All decisions are
resolved: the guard is unconditional with no setting (player decision 2), and when it
prevents a wipe it always WARN-logs the numbers and posts one short native
ScreenMessage naming the protected resource via `ParsekLog.ScreenMessage` - the same
"Recording STARTED"-style notice (player decision 1). The clamp being unconditional
matches the codebase precedent for correctness behaviors with no gameplay downside.
