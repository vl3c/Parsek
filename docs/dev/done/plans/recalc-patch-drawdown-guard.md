# Recalc-Patch Drawdown Guard (defense-in-depth)

Status: IMPLEMENTATION-READY. No code in this branch beyond this document; implement on
a follow-up branch. REVISED after a clean Opus design review found the original "clamp
any non-time-travel downward delta" thesis false: the predicate now keys on the
NON-RESERVED running balance and adds a fifth time-travel signal for the deferred
plain-rewind recalc (sections 2.6.6 / 2.6.7, 3.2 / 3.3). A follow-up review refinement
(MINOR-A) made the signal-5 fail-safe race-free via the next scene's
`ParsekScenario.OnAwake` clear instead of a blanket scene-switch clear (section 3.2).

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

### 2.6 Resolved evidence (corrected after the Blocker review)

This section records the investigation behind the predicate. IMPORTANT CORRECTION: a
clean Opus design review found that the earlier "no legitimate non-time-travel
downward delta exists; clamp anything past epsilon" thesis was FALSE against the code,
via two reachable false-positive paths. Subsections 2.6.6 (Blocker 1) and 2.6.7
(Blocker 2) document them with file:line evidence, and 3.2 / 3.3 are revised to
match. 2.6.1-2.6.5 below remain individually correct as stated, but they do NOT
add up to "downward == bug" - 2.6.7 is the missing case. Read 2.6.6 / 2.6.7 as the
controlling facts.

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
post-commit / live-event recalc, so the RUNNING balance already includes the spend and
stays at/above live; the patch residual is ~0. This is the load-bearing fact for
Class B in section 5: a legitimate spend keeps the running balance intact, so it never
trips the running-balance guard - even on a cutoff walk where the reservation
separately lowers the spendable target (Blocker 2 / 2.6.7).

#### 2.6.4 The patch only fires when ledger-mirrors-live should hold

`GetKspPatchDeferralReason` (`LedgerOrchestrator.cs:2044`) skips the patch entirely
while a live recorder / active uncommitted tree / pending tree exists (only the
cutoff/rewind paths bypass). So the dangerous downward patch fires only AFTER commit
or on a settled scene. NOTE (corrected): the earlier conclusion drawn here - "so any
downward delta is a corruption" - is WRONG, because the reservation legitimately
lowers the patch TARGET below live on cutoff walks (2.6.7). The corrected predicate
keys on the running balance, not the patch delta (3.3).

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

#### 2.6.6 BLOCKER 1 - plain Rewind-to-Launch runs its authoritative recalc with ALL prior signals false

The earlier draft keyed the rewind case on `RewindContext.IsRewinding` and cited the
recalc at `ParsekScenario.cs:3202`. That call site is the WRONG one. The authoritative
career-resource recalc for a plain Rewind-to-Launch is the DEFERRED coroutine:

- `HandleRewindOnLoad` sets `RewindUTAdjustmentPending = true` and
  `StartCoroutine(ApplyRewindResourceAdjustment())` (`ParsekScenario.cs:3188-3189`),
  then synchronously runs the pre-singleton recalc at `:3202` (while IsRewinding is
  true), then `RewindContext.EndRewind()` at `:3215`.
- The synchronous `:3202` patch runs when KSP's Funding/R&D/Reputation singletons are
  NOT yet populated (they load on a separate schedule after OnLoad), so it is not the
  authoritative write.
- `ApplyRewindResourceAdjustment` (`:6191`) yields, sets the adjusted UT, waits up to
  ~2s for the singletons (`:6225-6230`), then calls
  `RecalculateAndPatch(adjustedUT, suppressSuspiciousDrawdownWarnings: true)` at
  `:6237`. This is the authoritative funds/science/rep recalc for the rewound target.
  (Whether it reduces the running balance below live is NOT rigorously established - it
  depends on the loaded pre-launch value vs the rewound running balance. The point is
  that IF it reduces, the reduction is legitimate and must be authorized; signal (5)
  authorizes it either way, and if running >= live the guard would not fire anyway.)
- By the time `:6237` runs: `IsRewinding` is FALSE (EndRewind ran at `:3215`,
  confirmed by the `:6233-6234` comment "RewindContext.EndRewind() has already cleared
  the global"); `ActiveReFlySessionMarker` is NULL for a plain rewind
  (`ClearActiveReFlyMarkerForPlainRewind()` at the top of `HandleRewindOnLoad`, `:3094`,
  sets it null at `:3235`); `ActiveMergeJournal` is null (a plain rewind is not a
  re-fly merge); and it is not the tombstone path. ALL FOUR original signals are
  false.

So if this recalc reduces the running balance, the prior guard would CLAMP A
LEGITIMATE rewind drawdown - exactly the "intended reduction" the guard must allow.
Fix: the fifth signal, `RewindContext.RewindResourceAdjustmentInProgress`, set
synchronously before `StartCoroutine` and cleared by the coroutine try/finally plus the
OnAwake fail-safe (section 3.2). It authorizes the `:6237` patch unconditionally.
Verified against the `:6237` call site specifically.

#### 2.6.7 BLOCKER 2 - the reservation legitimately drives the target BELOW live on plain (non-time-travel) loads

`GetAvailableFunds()` / `GetAvailableScience()` return the reservation-aware spendable
amount, which on a CUTOFF walk is the projected MINIMUM balance (running balance minus
future committed costs), NOT the running balance. The projection installs that value
only when `utCutoff.HasValue`:

- `RecalculationEngine.Recalculate` runs `ApplyProjectedAvailability` (which calls
  `SetProjectedAvailable` -> `hasProjectedAvailableFunds = true`) ONLY inside
  `if (utCutoff.HasValue)` (`RecalculationEngine.cs:177-194`).
- With the projected value installed, `GetAvailableFunds` returns it
  (`FundsModule.cs:606-613`); `GetAvailableScience` likewise (`ScienceModule.cs:433-440`).
- This is INTENDED (design `docs/dev/done/design-going-back-in-time.md:34-46`,
  `milestone-resource-reservation.md`): the reservation patches KSP's top-bar DOWN to
  the spendable amount so the player cannot overspend funds already committed to future
  recordings.

And cutoff walks fire on PLAIN, non-time-travel paths with NO time-travel signal:
- `DeferredSeedAndRecalculate` calls `RecalculateAndPatchForCurrentTimelineUT(currentUT)`
  when `HasActionsAfterUT(currentUT)` on a normal load (`ParsekScenario.cs:6133-6138`).
- `ShouldUseCurrentUtCutoffForPostRewindFlightLoad` ->
  `RecalculateAndPatchForPostRewindFlightLoad` is gated with NO time-travel signal
  (`ParsekScenario.cs:525-543`, `:586-588`).
- `RecalculateAndPatchForLiveTimelineEvent` / `...IfFutureActions` route to the cutoff
  walk when future actions exist (`LedgerOrchestrator.cs:1382-1417`).

Concrete repro: a committed mission's build cost is a future-UT `FundsSpending`. After
a rewind + resume + save + reload, the next plain load's cutoff walk reserves that
future cost, so target = (running balance) - (reserved cost) < live, with NO signal.
The earlier guard ("clamp any downward delta") would clamp this, defeating the
reservation and letting the player overspend (MAJOR 4). "Live == saved" does NOT save
the thesis, because the reservation lowers the TARGET below the loaded-live value even
when live == saved.

Resolution (evaluated against the code): the soundest fix is the review's option (b),
which SUBSUMES option (a). Compare the guard against the NON-RESERVED running balance
(`GetRunningBalance()` / `GetRunningScience()`), not `GetAvailableFunds()` /
`GetAvailableScience()`. A reservation lowers `available` below `running` but leaves
`running` intact (>= live), so it does NOT trip the guard. A missing-earning-channel
bug lowers `running` itself below live, so it DOES. On a full walk
(`utCutoff == null`) no projection runs and `available == running`, so option (b)
collapses to option (a) there - and BUG-A fired on exactly that full-walk return-to-KSC
path, so the motivating bug is still caught. Reputation has no reservation
(`ReputationModule` has only `GetRunningRep()`), so its target already IS the running
balance and the discriminator is the plain target-vs-live check. Predicate details in
3.3.

------------------------------------------------------------------------

## 3. The decision predicate

### 3.1 Outcomes per resource (keyed on the RUNNING balance vs live)

For each guarded resource (science, funds, reputation) the guard classifies the patch
as exactly one of, using the NON-RESERVED running balance as the discriminator (3.3,
Blocker 2):

- (a) SAFE-TO-APPLY: `runningBalance >= currentLive - epsilon` (the career total is
  intact). Apply the reservation-aware patch target UNCHANGED. This includes the
  legitimate RESERVATION case where the patch target (`available`) is below live but
  the running balance is not - the reservation is written so overspend stays prevented.
- (b) AUTHORIZED-REDUCTION: `runningBalance < currentLive - epsilon` WITH any of the
  five time-travel signals set. Apply unchanged (intended time travel: rewind, re-fly,
  merge, tombstone, deferred rewind adjustment).
- (c) GUARDED-DRAWDOWN: `runningBalance < currentLive - epsilon` with NO time-travel
  signal. This is the missing-earning-channel wipe (BUG-A class). Clamp: apply
  `max(target, live)` so live is preserved (section 4).

Per-resource because their reservation/semantics differ (section 3.4): rep has no
reservation, so its running balance is its target.

### 3.2 The time-travel-authorized signal (FIVE inputs - Blocker 1 added a fifth)

A boolean `authoritativeReduction` computed once per `RecalculateAndPatchCore`
call and threaded down to `PatchAll` (alongside `suppressSuspiciousDrawdownWarnings`,
NOT reusing it). It is true when ANY of:

1. `RewindContext.IsRewinding` is true.
2. `ParsekScenario.Instance?.ActiveReFlySessionMarker != null`.
3. `ParsekScenario.Instance?.ActiveMergeJournal != null`.
4. The call is the tombstone path (`RecalculateAndPatchAfterTombstones` sets the
   flag explicitly; it is the only caller that needs to, since tombstone removal is
   a designed reduction and may run after EndRewind has cleared IsRewinding).
5. NEW (Blocker 1): a deferred rewind resource adjustment is in progress -
   `RewindContext.RewindResourceAdjustmentInProgress`.

Rationale for each:
- (1) covers the SYNCHRONOUS pre-singleton rewind recalc at `ParsekScenario.cs:3202`
  (runs while IsRewinding is still true; EndRewind is at `:3215`).
- (2) covers any recalc that fires while a re-fly session is live (including
  scene-change recalcs DURING the session, which legitimately reflect the
  superseded-branch removal).
- (3) covers mid-merge recalcs across the crash-recovery checkpoints.
- (4) covers the tombstone tail specifically.
- (5) covers the AUTHORITATIVE DEFERRED rewind drawdown at `ParsekScenario.cs:6237`
  (`ApplyRewindResourceAdjustment` -> `RecalculateAndPatch(adjustedUT, suppress:true)`).
  This is the real career-resource write for a plain Rewind-to-Launch, and at that
  point ALL of (1)-(4) are FALSE: the coroutine resumes after `EndRewind()` cleared
  IsRewinding (`:6233-6234` comment) and after `ClearActiveReFlyMarkerForPlainRewind()`
  nulled the marker (`:3094` -> `:3235`), and a plain rewind has no merge journal or
  tombstone path. Signal (5) authorizes this patch UNCONDITIONALLY: whether the
  running balance is actually below live there is immaterial (it depends on the loaded
  pre-launch value vs the rewound running balance, and is not rigorously established;
  if running >= live the guard would not fire anyway, and if running < live signal (5)
  authorizes it). See Blocker 1 (section 2.6.6) for the full call-site trace.

Implementing signal (5) - lifetime and the race-free fail-safe (MINOR-A): add a static
flag to `RewindContext`: `RewindResourceAdjustmentInProgress` (default false), with
`BeginRewindResourceAdjustment()` / `EndRewindResourceAdjustment()` setters (logged) and
a clear in `ResetForTesting`.

Host-lifetime facts (verified): `ApplyRewindResourceAdjustment` is a coroutine started
via `StartCoroutine` on the per-scene `ParsekScenario` ScenarioModule (`:3189`), which a
plain Rewind-to-Launch loads in SPACECENTER. `ParsekScenario` is NOT DontDestroyOnLoad
(it is a `ScenarioModule`, a fresh instance per scene, `ParsekScenario.cs:30-32`); on a
scene change the SPACECENTER instance is destroyed and Unity STOPS the coroutine WITHOUT
running its remaining `finally` blocks. Therefore:

- AUTHORITATIVE CLEAR (covers the normal + exception paths): the coroutine's own
  try/finally. Set the flag synchronously in `HandleRewindOnLoad` immediately BEFORE
  `StartCoroutine(ApplyRewindResourceAdjustment())` (`:3188-3189`) via
  `RewindContext.BeginRewindResourceAdjustment()` (so it is true before EndRewind clears
  IsRewinding at `:3215`), and wrap the `:6237` patch in the coroutine in
  `try { ...patch... } finally { RewindContext.EndRewindResourceAdjustment(); }`. The
  finally clears on both a clean patch and a thrown patch. NOTE the flag is NOT cleared
  by `RewindContext.EndRewind` (which runs synchronously at `:3215`, before the coroutine
  resumes); it is owned solely by the coroutine's lifetime.
- FAIL-SAFE for the only uncovered case (host destroyed mid-wait, so the finally never
  runs AND the `:6237` patch never runs): do NOT use a blanket
  `onGameSceneSwitchRequested` clear - that races the live coroutine during its
  up-to-120-frame (~2s) singleton wait (`:6225-6230`) and could null signal (5) before
  the deferred patch, exactly the bug MINOR-A flags. Instead clear the flag in the NEXT
  scene's `ParsekScenario.OnAwake` (`:841-850`). OnAwake runs once per fresh instance,
  BEFORE that instance's OnLoad (so it never clears a flag the same instance is about to
  set in its own `HandleRewindOnLoad`), and only AFTER the previous SPACECENTER instance
  (and its coroutine) has been destroyed - so it cannot race a live coroutine: the old
  host and the new host never coexist. If the coroutine completed normally, the finally
  already cleared the flag and the OnAwake clear is a no-op; if the host was destroyed
  mid-wait, the `:6237` patch never fired (nothing to mis-guard) and OnAwake clears the
  stranded flag before any new-scene recalc can read it. This makes signal (5) true for
  the entire window up to and including the `:6237` patch, and impossible to strand-true
  into normal play.

This signal is computed inside `LedgerOrchestrator` (it has access to `RewindContext`
and `ParsekScenario.Instance`), not inside `KspStatePatcher` (which is a pure-ish
writer and should stay free of scene-state lookups for testability). `KspStatePatcher`
receives the boolean as a parameter.

A pure, internal-static helper makes it unit-testable:

```
internal static bool IsAuthoritativeReduction(
    bool isRewinding, bool hasReFlyMarker, bool hasMergeJournal,
    bool tombstonePath, bool rewindResourceAdjustmentInProgress)
    => isRewinding || hasReFlyMarker || hasMergeJournal
       || tombstonePath || rewindResourceAdjustmentInProgress;
```

The live wrapper reads the five inputs and calls it.

### 3.3 The drawdown predicate (REVISED after Blocker 2: compare the NON-RESERVED running balance, not the reservation-aware target)

The prior draft compared the patch delta (target - live) and claimed "any downward
delta past epsilon with no time-travel context is a bug, clamp it". Blocker 2 (section
2.6.7, with file:line evidence) proved that thesis FALSE: the patch TARGET is the
reservation-aware spendable amount (`GetAvailableFunds()` / `GetAvailableScience()`),
which on a CUTOFF walk is DELIBERATELY below the running balance to reserve
already-committed future spends and prevent overspend. Cutoff walks run on plain,
non-time-travel paths (`DeferredSeedAndRecalculate` -> current-UT cutoff on a normal
load with future actions; `RecalculateAndPatchForPostRewindFlightLoad`;
`RecalculateAndPatchForLiveTimelineEvent` when future actions exist - section 2.6.7).
So a legitimate reservation legitimately drives target below live with NO time-travel
signal. Clamping on "target < live" would defeat the reservation and let the player
overspend (MAJOR 4). The thesis is wrong.

The fix distinguishes the two CAUSES of a low target:

- RESERVATION drawdown (legitimate): the RUNNING balance is intact, but `available`
  is below it because future committed spends are reserved. Here
  `runningBalance >= currentLive` (or within epsilon) but `available < runningBalance`.
  This must NOT be clamped - it is the overspend-prevention invariant.
- MISSING-EARNING-CHANNEL drawdown (the BUG-A corruption): the RUNNING balance ITSELF
  fell below live because earned resources never became ledger actions. Here
  `runningBalance < currentLive - epsilon`. This is what must be clamped.

The discriminator is the NON-RESERVED running balance, which the modules already
expose: `FundsModule.GetRunningBalance()` (`FundsModule.cs:589-593`),
`ScienceModule.GetRunningScience()` (`ScienceModule.cs:416-420`). Reputation has NO
reservation system at all (`ReputationModule` exposes only `GetRunningRep()`, the
patch target already IS the running value, `ReputationModule.cs:289`), so for rep the
running balance and the target are the same and the discriminator collapses to the
plain "target < live" check.

Revised predicate (the guard input is the running balance, NOT the patch target):

```
internal static bool IsGuardableDrawdown(
    double runningBalance, double currentLive, double epsilon)
    => runningBalance < currentLive - epsilon;
```

Per-resource the caller passes:
- Funds: `runningBalance = funds.GetRunningBalance()`, `currentLive = Funding.Instance.Funds`.
- Science: `runningBalance = science.GetRunningScience()`, `currentLive = ResearchAndDevelopment.Instance.Science`.
- Reputation: `runningBalance = reputation.GetRunningRep()`, `currentLive = Reputation.Instance.reputation`.

Epsilon = the resource's existing patch no-op epsilon so the guard never fights the
patch's own rounding tolerance:
- Funds: 0.01 (matches `PatchFunds` no-op check at `KspStatePatcher.cs:649`).
- Science: 0.001 (matches `PatchScience` no-op check at `KspStatePatcher.cs:102`).
- Reputation: 0.01 (matches `PatchReputation` no-op check at `KspStatePatcher.cs:711`).

What the clamp DOES when it fires (revised): it does NOT just zero the delta to the
target; it raises the EFFECTIVE TARGET floor to `currentLive` so the patch never
writes below live. Concretely the applied target becomes `max(patchTarget, currentLive)`.
Note that on a guarded (corruption) frame `runningBalance < currentLive`, and the
reservation can only lower `available` further below `runningBalance`, so `patchTarget`
(=`available`) is also `< currentLive`; raising the floor to `currentLive` preserves
the player's live value. On a NON-guarded frame (reservation-only drawdown,
`runningBalance >= currentLive`) the clamp does not fire and the reservation-aware
target is written unchanged - overspend prevention intact.

Why running-balance is the right and sufficient discriminator (the BUG-A motivating
case is still caught): on a FULL walk (`utCutoff == null`), no projection runs, so
`available == runningBalance == initial + earnings - spendings` (the modules return
the non-projected branch, `FundsModule.cs:606-613`, `ScienceModule.cs:433-440`). BUG-A
fired on the return-to-KSC FULL-walk `RecalculateAndPatch()` path, where
`runningBalance` itself was below live (the Mun earnings were missing from the ledger),
so `IsGuardableDrawdown(runningBalance, live, eps)` is true and the corruption is
clamped. On a CUTOFF walk, a missing-earning leak ALSO lowers `runningBalance` (the
earning never entered the walk), so the same predicate catches it there too, while a
pure reservation (running intact) does not trip it. One predicate, both walk kinds,
zero false positives on reservation.

The existing `IsSuspiciousDrawdown` (10% / 1000) stays as the louder legacy WARN
tripwire on the patch delta; the clamp is independent and keyed on the running-balance
discriminator. No percentage, no absolute floor on the clamp. "Never clamp a
legitimate change, always clamp a corruption" now holds because the discriminator is
the actual career total (running balance), not the spendable amount (which reservation
legitimately lowers).

### 3.4 Per-resource specifics

- SCIENCE: pool can be legitimately spent to ~0 by buying tech, but each purchase
  is a committed `ScienceSpending` action, so the RUNNING balance reflects it and
  stays >= live. The guard fires only when `GetRunningScience()` itself is below live
  past epsilon (the leak signature, 3.3). The reservation lowering only the spendable
  `available` (cutoff walks) does NOT trip it. Per-subject science
  (`PatchPerSubjectScience`) is handled at the pool level only (section 6, MINOR 5).
- FUNDS: same shape; spends (facility upgrade, rollout, hire, strategy setup) are
  committed actions reflected in `GetRunningBalance()`. Guard fires only when
  `GetRunningBalance()` is below live past epsilon, never on a reservation-only
  drawdown of `available`.
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

On a GUARDED-DRAWDOWN for a resource (i.e. `GetRunningBalance()` / `GetRunningScience()`
/ `GetRunningRep()` is below live past epsilon AND no time-travel context):

1. CLAMP: raise the effective target floor to the current live value - apply
   `max(patchTarget, currentLive)` instead of `patchTarget`, so the patch never writes
   below live (3.3). The player keeps what they earned. This makes future leaks
   NON-destructive: the worst case is that live state stays correct while the ledger is
   wrong, which is recoverable, instead of live state being driven to a wrong value,
   which is not. (Because the clamp only fires when running < live, and reservation can
   only lower the target further, the would-be target is below live, so the floor at
   live is the preserving choice.)
2. LOG: emit a WARN (or ERROR-level INFO) with the full numbers (current live,
   running balance, would-be reservation-aware target, clamped-to value, resource,
   reason string, all five time-travel signal values) so the leak is loud in KSP.log.
   For pool-clamped SCIENCE, the WARN MUST also note that per-subject credited totals
   were patched UNCLAMPED (MINOR 5, section 6) so the Science Archive may transiently
   disagree with the clamped pool until the leak is fixed - this is a documented
   limitation, called out at the clamp site.
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
   short; the WARN log carries the numbers.) The toast is gated by a SESSION-SCOPED
   latch per resource (MAJOR 3, section 9), NOT reset on scene change, so a persistent
   leak toasts ONCE and stays quiet across every subsequent scene change. This path
   has a unit-test seam (`ParsekLog.ScreenMessageSinkForTesting`, `ParsekLog.cs:80,485-489`),
   so the notification is directly assertable in xUnit.
4. The WARN line itself fires on every guarded recalc (it documents the ongoing leak
   for diagnostics); only the in-game toast is one-shot. No persisted marker is needed
   for correctness (the clamp recomputes from live + running every time and is
   idempotent); the optional diagnostic counter in 4.4 is purely for humans.

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
  same running balance below the same live value and re-applies the same floor
  (`max(target, live) == live`), so live state never drifts and the guard never
  oscillates.
- BLAST RADIUS of a (now-prevented) false positive (MAJOR 4): if the guard ever
  clamped a LEGITIMATE reservation drawdown (the Blocker 2 bug, now fixed by keying on
  the running balance), the clamp would be idempotent (no oscillation) BUT would defeat
  the reservation invariant - KSP's top-bar would show the un-reserved running balance,
  letting the player spend funds already committed to future recordings and overspend
  the timeline. That is WHY Blocker 2 had to be fixed at the predicate (running-balance
  discriminator), not merely mitigated: an idempotent-but-wrong clamp is still wrong.

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

There are TWO classes of legitimate downward movement. The guard is not blocked by
either, but for DIFFERENT reasons: time-travel reductions are allowed by the
authoritative-reduction SIGNAL, and reservation drawdowns are allowed because the
guard keys on the RUNNING balance (which a reservation leaves intact), not the
spendable target.

Class A - time-travel reductions (allowed by signal):

| Scenario | Path | Why not blocked |
| --- | --- | --- |
| Plain Rewind-to-Launch, pre-singleton recalc | `ParsekScenario:3202` while `IsRewinding` | signal (1). |
| Plain Rewind-to-Launch, DEFERRED authoritative recalc | `ParsekScenario:6237` (after EndRewind + marker clear) | signal (5) `RewindResourceAdjustmentInProgress` - the ONLY signal true here (Blocker 1, 2.6.6). |
| Re-fly post-invoke | `RewindInvoker:908`, `IsRewinding` true | signal (1). |
| Re-fly mid-session scene change | any recalc while `ActiveReFlySessionMarker != null` | signal (2). |
| Re-fly merge tail (tombstones) | `RecalculateAndPatchAfterTombstones` | signal (4); also `ActiveMergeJournal` typically non-null (3). |
| Backward warp | `WarpToTime` -> Rewind-to-Launch then forward jump | the rewind sets (1)/(5); the forward jump only advances (2.6.1). |

Class B - non-time-travel legitimate downward movements (allowed because running
balance stays >= live, so `IsGuardableDrawdown` is false):

| Scenario | Path | Why not blocked |
| --- | --- | --- |
| Reservation of a committed future cost (cutoff walk) | `DeferredSeedAndRecalculate` / post-rewind-load / live-event cutoff walks | the reservation lowers `available` below running, but `GetRunningBalance()` stays >= live, so the guard does not fire; the reservation-aware target is written unchanged (Blocker 2, 2.6.7). |
| Strategy exchange (Bail-Out Grant) | KSC live event -> committed action | KSP debits live first, action captured, running balance reflects it (>= live), residual ~0. (2.6.3) |
| Contract penalty | committed `ContractFail`/`Cancel` action | applied live first, captured, running reflects it, ~0 residual. (2.6.3) |
| Facility upgrade / repair cost | committed `FundsSpending` action | debited live first, running reflects it, ~0 residual. (2.6.3) |
| Tech node purchase | committed `ScienceSpending` action | spent live first, running reflects it, ~0 residual. (2.6.3) |
| Vessel build / rollout cost | `vessel-rollout` live event -> committed action | debited live first, running reflects it, ~0 residual. (2.6.3) |
| Multi-channel commit | one recalc applying many committed deltas | all committed before the patch; running reflects them; ~0 residual. |

The unifying invariant for Class B: a legitimate spend is a COMMITTED LEDGER ACTION
that KSP applies to live state BEFORE Parsek captures it, so the RUNNING balance
already includes it and stays at/above live; a reservation lowers only the spendable
`available`, never the running balance. A LEAK is the opposite: an earning that never
became a ledger action, so the RUNNING balance itself falls below live. The guard
clamps iff the running balance is below live with no time-travel signal - so it allows
every Class A and Class B scenario above and clamps only the leak.

------------------------------------------------------------------------

## 6. Per-resource handling and which channels get protection

- SCIENCE (R&D pool): full guard (clamp on `GetRunningScience() < live - eps`).
- SCIENCE (per-subject, `PatchPerSubjectScience`, `KspStatePatcher.cs:129`): NOT
  clamped - it runs UNCLAMPED (MINOR 5). The guard protects only the aggregate POOL.
  Per-subject credited totals stay ledger-driven (a subject can legitimately drop to 0
  on a revert, and per-subject clamping would fight legitimate rewrites). KNOWN
  LIMITATION: when the pool is clamped (a leak), the per-subject credited totals are
  still patched to the (too-low) ledger values, so the stock Science Archive can
  transiently disagree with the clamped pool total until the underlying leak is fixed.
  The pool clamp WARN explicitly states this (section 4.2 step 2) so it is visible.
  Self-heals once the leak is fixed (the next recalc agrees and nothing is clamped).
- FUNDS: full guard (clamp on `GetRunningBalance() < live - eps`).
- REPUTATION: full guard. Rep has NO reservation (`ReputationModule` exposes only
  `GetRunningRep()`), so its running balance IS the patch target and the discriminator
  is the plain `GetRunningRep() < live - eps` check. The clamp is expressed against the
  `SetReputation` semantics ("do not set below live"); "downward" is measured by
  magnitude (`< live - eps`) regardless of sign so negative rep is handled. Every
  legitimate rep drop is a committed action that leaves the running rep at/above live,
  so the clamp never blocks a genuine penalty.
- TECH TREE: NOT guarded by clamp. Tech is set-membership, not a magnitude, and is
  only patched when `techPatchCutoff` is non-null. Per Blocker 2, a non-null cutoff is
  NOT necessarily a time-travel path (plain cutoff walks exist), but tech removal still
  only happens through the explicit baseline+ledger target set, not a magnitude
  drawdown, so the BUG-A wipe class does not apply. No clamp; keep the existing
  PatchTechTree logging.
- FACILITIES: NOT guarded by clamp in v1. Facility downgrades are level changes, not
  magnitudes; the BUG-A class (silent magnitude wipe) does not apply the same way.
  Revisit only if a facility-downgrade leak is observed.
- CONTRACTS: NOT guarded by clamp. Contract state is structural; out of scope for a
  magnitude drawdown guard.

------------------------------------------------------------------------

## 7. Decisions (all RESOLVED, no open questions)

Every decision is settled: four design questions resolved from code + design docs,
two answered by the player, and TWO BLOCKERS from a clean Opus design review resolved
at the predicate level (2.6.6 / 2.6.7, 3.2 / 3.3). Nothing in this plan is left open.

### 7.0 Blocker resolutions (design review)

- BLOCKER 1 (plain-rewind drawdown runs with all prior signals false). RESOLVED: add a
  fifth authoritative-reduction signal, `RewindContext.RewindResourceAdjustmentInProgress`,
  set synchronously before the deferred coroutine is scheduled and cleared after its
  `:6237` patch (2.6.6, 3.2). Verified against the `ParsekScenario.cs:6237` call site.
- BLOCKER 2 (reservation legitimately drives the target below live on plain loads).
  RESOLVED: key the guard on the NON-RESERVED running balance
  (`GetRunningBalance()` / `GetRunningScience()` / `GetRunningRep()`), not the
  reservation-aware patch target. A reservation leaves the running balance intact, so
  it never trips the guard; a missing-earning leak lowers the running balance itself,
  so it does. Subsumes the "full-walk only" alternative (2.6.7, 3.3).

### 7.1 Resolved from code/docs (evidence in section 2.6)

- Q1 / threshold model. RESOLVED: no percentage and no absolute floor; the clamp fires
  when the NON-RESERVED running balance is below live past the resource's rounding
  epsilon with no time-travel context (revised from the disproven "clamp any downward
  delta" after Blocker 2). (Section 3.3.)
- Q2 / reputation. RESOLVED: treat rep like funds/science. Rep has no reservation, so
  its running balance IS the target and the discriminator is the plain
  `GetRunningRep() < live - eps` check, expressed against `SetReputation` semantics.
  (Sections 3.4, 6.)
- Q3 / time jump. RESOLVED: a time jump is always forward and a backward warp routes
  through a rewind (which sets signal (1)/(5)), so the time-jump recalc never
  legitimately reduces the running balance with no signal. (Section 2.6.1.)
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
  - Add `internal static bool IsGuardableDrawdown(double runningBalance,
    double currentLive, double epsilon) => runningBalance < currentLive - epsilon;`
    (pure, unit-tested). Keyed on the RUNNING balance, not the patch delta (Blocker 2,
    section 3.3). No fraction, no floor.
  - Add `internal static double ApplyDrawdownGuard(double patchTarget,
    double runningBalance, double currentLive, double epsilon,
    bool authoritativeReduction, string resource, out bool clamped)`:
    if `!authoritativeReduction && IsGuardableDrawdown(runningBalance, currentLive, eps)`
    -> `clamped = true; return max(patchTarget, currentLive);` else
    `clamped = false; return patchTarget;`. Pure decision; keep the WARN/toast in the
    caller-visible patch methods (match the existing in-method WARN pattern; assert via
    `TestSinkForTesting`).
  - Thread new parameters `bool authoritativeReduction` AND the running-balance accessor
    through `PatchAll` -> `PatchScience` / `PatchFunds` / `PatchReputation`. The
    patch methods already hold the module, so they call `funds.GetRunningBalance()` /
    `science.GetRunningScience()` / `reputation.GetRunningRep()` directly; only
    `authoritativeReduction` is a new cross-cutting param.
  - `PatchScience`: compute `targetScience` as today; then
    `double effTarget = ApplyDrawdownGuard(targetScience, science.GetRunningScience(),
    currentScience, 0.001, authoritativeReduction, "science", out clamped);` recompute
    `delta = (float)(effTarget - currentScience)` before `AddScience(delta, ...)`.
    NOTE: `PatchPerSubjectScience` still runs UNCLAMPED (MINOR 5); when `clamped`, the
    pool WARN states the per-subject divergence.
  - `PatchFunds`: same with `funds.GetRunningBalance()`, epsilon 0.01, before `AddFunds`.
  - `PatchReputation`: rep has no reservation, so pass `reputation.GetRunningRep()` as
    BOTH the patchTarget and the runningBalance; if `clamped`, `SetReputation(currentRep)`
    (no-op) instead of the target. "Downward" is by magnitude (`< live - eps`) so
    negative rep is handled.
  - Keep `IsSuspiciousDrawdown` and the existing WARN unchanged (legacy 10% tripwire on
    the patch delta); the clamp is independent and keyed on the running-balance
    discriminator.
  - On a clamp, post the toast via `ParsekLog.ScreenMessage(text, 2.5f)` (player
    decision 1; wording in 4.2 step 3) gated by a per-resource SESSION-SCOPED latch
    (MAJOR 3, section 9) so it fires once and stays quiet across scene changes.
  - Add per-resource latch statics + extend `ResetForTesting` to clear them.

- `Source/Parsek/RewindContext.cs`
  - Add `internal static bool RewindResourceAdjustmentInProgress { get; private set; }`
    plus `BeginRewindResourceAdjustment()` / `EndRewindResourceAdjustment()` setters
    (logged, mirroring the existing Begin/End style). Clear it in `ResetForTesting`.
    This is signal (5) for the deferred plain-rewind drawdown (Blocker 1, 2.6.6, 3.2).
    Owned by the coroutine's lifetime: set synchronously before scheduling, cleared by the
    coroutine's try/finally around the `:6237` patch (authoritative) and by the next
    scene's `ParsekScenario.OnAwake` (fail-safe for host-destroyed-mid-wait); NOT cleared
    by `EndRewind` (which runs before the coroutine resumes).

- `Source/Parsek/ParsekScenario.cs`
  - In `HandleRewindOnLoad`, call `RewindContext.BeginRewindResourceAdjustment()`
    synchronously immediately before `StartCoroutine(ApplyRewindResourceAdjustment())`
    (`:3188-3189`).
  - In `ApplyRewindResourceAdjustment`, wrap the `RecalculateAndPatch(adjustedUT,
    suppress:true)` at `:6237` in `try { ...patch... } finally {
    RewindContext.EndRewindResourceAdjustment(); }`. This is the AUTHORITATIVE clear; the
    finally covers clean and thrown patch paths.
  - FAIL-SAFE (MINOR-A, host-destroyed-mid-wait case only): clear the flag in
    `ParsekScenario.OnAwake` (`:841-850`), NOT on `onGameSceneSwitchRequested`. OnAwake of
    the next scene's fresh `ParsekScenario` instance runs before that instance's OnLoad
    and only after the prior (SPACECENTER) instance + its coroutine are destroyed, so it
    cannot race a live coroutine (old and new hosts never coexist) and cannot clear a flag
    the same instance is about to set. A blanket scene-switch clear is explicitly REJECTED
    because it races the coroutine's ~2s singleton wait. (See 3.2 for the full lifetime
    analysis: `ParsekScenario` is a per-scene `ScenarioModule`, NOT DontDestroyOnLoad, so
    Unity stops the coroutine without running its finally on host teardown; the OnAwake
    clear is the race-free recovery for that one uncovered case, where the `:6237` patch
    never fired so there is nothing to mis-guard.)
  - Optional diagnostic persistence: `DrawdownGuardClampCount` / `DrawdownGuardLastClampUT`
    in OnSave/OnLoad. Purely observability (the clamp itself never depends on it).
  - NIT-B (no new scene-switch subscription needed): the final fail-safe uses the
    EXISTING `ParsekScenario.OnAwake` override, so NO new `GameEvents` subscription is
    added. (Note `ParsekScenario` does not currently subscribe to
    `onGameSceneSwitchRequested`; the existing scene-change stamping is
    `ParsekFlight.OnSceneChangeRequested` on `onGameSceneLoadRequested`, FLIGHT-only,
    `ParsekFlight.cs:1115`, and would not fire for the SPACECENTER rewind path anyway.)
    If a future revision needs a real subscription instead, follow the
    `MapRenderProbe.cs:112` template (Awake `GameEvents.X.Add`, OnDestroy `GameEvents.X.Remove`,
    instance once-guard) - but the OnAwake-clear design avoids needing one.

- `Source/Parsek/GameActions/LedgerOrchestrator.cs`
  - Add `internal static bool IsAuthoritativeReduction(bool isRewinding,
    bool hasReFlyMarker, bool hasMergeJournal, bool tombstonePath,
    bool rewindResourceAdjustmentInProgress)` (pure, FIVE inputs - section 3.2).
  - MINOR 6 (explicit plumbing): `RecalculateAndPatchCore`
    (`LedgerOrchestrator.cs:1679`) needs a NEW parameter `bool tombstonePath` (default
    false) added to its signature, threaded to the three Core call sites
    (`:1329` `RecalculateAndPatch`, `:1354` `RecalculateAndPatchForCurrentTimelineUT`,
    `:1436` `RecalculateAndPatchAfterTombstones`). Only `RecalculateAndPatchAfterTombstones`
    passes `tombstonePath: true`; the other two pass false. Core then computes
    `authoritativeReduction = IsAuthoritativeReduction(RewindContext.IsRewinding,
    ParsekScenario.Instance?.ActiveReFlySessionMarker != null,
    ParsekScenario.Instance?.ActiveMergeJournal != null, tombstonePath,
    RewindContext.RewindResourceAdjustmentInProgress)` and passes it through
    `ApplyRecalculatedStateToKsp` -> `PatchAll`. (This threading is not detectable by
    inspection of the patch methods alone; it must be added at all three Core callers.)
  - Do NOT reuse `suppressSuspiciousDrawdownWarnings`; keep the two params separate (the
    suppress flag controls the legacy 10% WARN; the new flag authorizes the clamp
    bypass on the five time-travel signals).

- `Source/Parsek/ParsekSettings.cs`
  - NO CHANGES (player decision 2). The guard is unconditional: no
    `guardSuspiciousDrawdown` field, no `protectEarnedProgress` checkbox, no
    notification-mode enum, no `CustomParameterUI`.

### 8.2 The seam

The guard hooks in at the LOWEST point that still knows the per-resource running
balance and live value: inside `PatchScience` / `PatchFunds` / `PatchReputation`,
immediately before the `AddScience` / `AddFunds` / `SetReputation` call. The
authoritative-reduction decision (five signals) is computed once at the
`RecalculateAndPatchCore` boundary and passed down; the running-balance read happens
in the patch method from the module it already holds. This keeps the discriminator
next to the live read (no staleness) while keeping the scene-state lookup out of the
pure writer.

### 8.3 Logging and observability (per CLAUDE.md)

Every decision logged:
- Safe (running >= live, or within tolerance): existing INFO/VERBOSE lines unchanged.
- Authorized reduction (running < live but a signal is set): VERBOSE line noting
  "drawdown authorized by time-travel context (rewinding=.., reFlyMarker=..,
  mergeJournal=.., tombstone=.., rewindResAdjust=..)" with running/live numbers.
- Guarded clamp: WARN line "PatchX: GUARDED DRAWDOWN clamped resource=.. running=..
  live=.. wouldBeTarget=.. clampedTo=live (no time-travel context) - earned value
  preserved; ledger may be missing an earning channel" ALWAYS (every guarded recalc),
  plus (for SCIENCE) the per-subject-unclamped note (MINOR 5), plus the
  session-latched short ScreenMessage via `ParsekLog.ScreenMessage` (player decision 1).
- Tag `KspStatePatcher` (existing). Subsystem-tagged, numeric, InvariantCulture.

### 8.4 Persistence / Post-Change Checklist

If the diagnostic fields are added: verify `ParsekScenario` OnSave/OnLoad, verify
test generators are unaffected (no new recording schema), run `dotnet test`. No
recording-format change is involved (CurrentRecordingSchemaGeneration untouched).

------------------------------------------------------------------------

## 9. Idempotency / repeat behavior

A corrupted ledger recalcs on every scene change. The guard must behave sanely on
repeat:

- The clamp is idempotent: the applied floor is `max(target, live) == live`, so live
  value is unchanged; the next recalc reads the same running-below-live and re-applies
  the same floor. No drift, no oscillation, no slow leak.
- The WARN line is per-resource and fires each guarded recalc by design (it documents
  the ongoing leak), but it is gated to GUARDED cases only, so a healthy career emits
  nothing.
- The ScreenMessage latch is SESSION-SCOPED (MAJOR 3), keyed on the clamp CAUSE
  (per-resource), and is NOT reset on scene change. The earlier draft reset it
  on-scene-quit, which would re-fire the toast on EVERY scene change for a persistent
  leak - spam. Correct design: a per-resource static bool (e.g.
  `fundsClampToastShownThisSession`) set on the first clamp toast and cleared ONLY in
  `ResetForTesting` and (optionally) on a genuine new-session boundary
  (`onGameStatePostLoad` of a DIFFERENT save / process restart), never on a plain
  scene change. An ongoing condition toasts once per session and stays quiet.

------------------------------------------------------------------------

## 10. Testing strategy

### 10.1 xUnit (pure helpers + log-assertion), the project pattern

Add to `Source/Parsek.Tests/` (likely a new `DrawdownGuardTests.cs`, plus extend
`PatchFundsSanityTests.cs`). Use `ParsekLog.TestSinkForTesting` capture and
`KspStatePatcher.SuppressUnityCallsForTesting = true` (existing pattern). All
classes touching shared static state get `[Collection("Sequential")]`.

`IsGuardableDrawdown(runningBalance, currentLive, epsilon)`:
- running more than epsilon below live -> true (leak).
- running within [live-epsilon, live] -> false (rounding noise).
- running == live - epsilon exactly -> false (strict `< live - epsilon`).
- running >= live -> false (healthy).
- CRITICAL Blocker-2 case: running >= live BUT a separate reservation-aware target is
  below live -> false (the predicate takes the running balance, not the target, so a
  reservation drawdown does NOT trip it).

`IsAuthoritativeReduction` (five inputs):
- all five inputs false -> false.
- each input true individually (including the NEW
  `rewindResourceAdjustmentInProgress`) -> true.
- combinations -> true.

`ApplyDrawdownGuard(patchTarget, runningBalance, currentLive, epsilon, authoritative, ...)`:
- running >= live -> returns patchTarget unchanged, clamped=false (covers the
  reservation case: target may be < live but running is not).
- running < live, authoritative=true -> returns patchTarget unchanged, clamped=false.
- running < live, authoritative=false -> returns `max(patchTarget, live)` (== live when
  target < live), clamped=true.

Log-assertion (drive `PatchScience`/`PatchFunds` with `SuppressUnityCallsForTesting`
where the singletons are null-guarded, OR exercise the pure path and assert on the
emitted lines): assert the GUARDED-DRAWDOWN WARN contains the resource, running, live,
would-be target, and "earned value preserved"; assert the SCIENCE clamp WARN contains
the per-subject-unclamped note (MINOR 5); assert the AUTHORIZED line contains the five
signal values; assert no clamp line on a healthy (running>=live) recalc; assert no
clamp line on a reservation-only drawdown (running>=live, target<live).

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
  science/funds total, force a recalc whose RUNNING balance is artificially below live
  (via a test seam, for example a one-shot "force running" hook), and assert the live
  `ResearchAndDevelopment.Instance.Science` / `Funding.Instance.Funds` are UNCHANGED
  (clamped) and the WARN fired.
- A paired test with each signal (incl. `RewindResourceAdjustmentInProgress`) forced
  true asserting the same too-low running balance IS applied (authorized reduction), so
  the in-game path proves the signal wiring, not just the pure helper.

### 10.3 Regression guards (incl. the two BLOCKER paths - MINOR 7)

Lock in the false-positive analysis with explicit tests for both blocker paths:

- BLOCKER 1 regression: plain Rewind-to-Launch (no re-fly) must STILL APPLY the
  authoritative drawdown, not clamp. Drive the decision with `IsRewinding=false`,
  marker=null, journal=null, tombstonePath=false, BUT
  `RewindResourceAdjustmentInProgress=true`, running < live -> assert NOT clamped
  (authorized). xUnit on `IsAuthoritativeReduction` / `ApplyDrawdownGuard`; plus an
  in-game test that exercises a real rewind and asserts the post-rewind funds/science
  landed at the rewound target (not clamped to pre-rewind live).
- BLOCKER 2 regression: plain load with committed future costs must STILL APPLY the
  reservation drawdown of the spendable target. Drive `ApplyDrawdownGuard` with
  running >= live (intact) but patchTarget < live (reservation) and all signals false
  -> assert NOT clamped and the reservation-aware target is returned unchanged.
  Synthetic-ledger xUnit: a committed future `FundsSpending`, a cutoff walk, assert
  `GetAvailableFunds() < GetRunningBalance()` and that the guard returns the
  reservation target (so the top-bar still reserves and overspend is prevented).
- Existing-legitimate-paths regression: a small post-commit reconciliation delta and a
  strategy-exchange-sized residual do NOT clamp (running stays >= live).

------------------------------------------------------------------------

## 11. Risks, edge cases, phased rollout

### 11.1 Risks

- A legitimate RESERVATION drawdown being clamped (this was Blocker 2). RESOLVED by
  keying the guard on the running balance, not the patch target (3.3): a reservation
  lowers only `available`, never running, so it does not trip the guard. If this fix
  were wrong the blast radius (MAJOR 4) is defeating the reservation and letting the
  player overspend - which is WHY it had to be fixed at the predicate. Covered by the
  Blocker-2 regression test (10.3).
- A legitimate TIME-TRAVEL reduction being clamped because a signal is missing (this
  was Blocker 1 for the deferred plain-rewind path). RESOLVED by the fifth signal
  `RewindResourceAdjustmentInProgress` (3.2, 2.6.6). Residual risk: a future
  time-travel path that reduces the running balance with none of the five signals set
  would be clamped. Failure mode is a clamped legitimate reduction (player keeps
  resources they were supposed to lose) - annoying but not career-destroying, loud in
  the WARN, and surfaces fast. Covered by the Blocker-1 regression test (10.3).
- Rep semantics (SET vs ADD, legitimate negatives, no reservation). Mitigated by
  expressing the clamp against the set ("do not set below live"), measuring downward by
  magnitude regardless of sign, and noting rep's running balance IS its target
  (sections 3.4, 6).
- Per-subject science divergence on a pool clamp (MINOR 5): documented limitation,
  called out in the clamp WARN; self-heals when the leak is fixed (section 6).

### 11.2 Edge cases

- HasSeed false (early load): patch already skipped; guard never runs. Fine.
- Sandbox mode (null singletons): patch already no-ops; guard never runs. Fine.
- First-ever seed establishment: the seed path sets the initial value; the guard only
  acts on subsequent reductions, not on seeding. The patch already skips when
  `!HasSeed`; the guard runs only AFTER HasSeed is true, so it never fights seeding.
- Loading a normal save (OnKspLoad / deferred-seed): NIT 8 correction. The earlier
  draft claimed "a normal load produces delta ~0". That is FALSE when committed future
  costs exist: a cutoff walk (taken when `HasActionsAfterUT(currentUT)`,
  `ParsekScenario.cs:6133-6138`) installs the reservation, so the patch TARGET
  (`available`) is legitimately below live by the reserved amount, i.e. delta is NOT
  ~0. The guard handles this correctly BECAUSE it keys on the RUNNING balance, not the
  delta: on a normal load the running balance == loaded live (KSP loads singletons from
  the .sfs), so `running < live` is false and the guard does not fire even though the
  reservation lowered the target. A real BUG-A leak instead lowers the running balance
  itself below live and IS clamped. Validate in playtest: a normal load with committed
  future costs writes the reservation-aware (lower) target with NO clamp and NO toast.

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
only existing protection is a log-only WARN that is even silenced on the scene-change
path. The guard adds an unconditional safety net that clamps a downward patch to the
player's current live value UNLESS the reduction is legitimate. The discriminator,
after a design review found the naive "clamp any downward delta" thesis false, is the
NON-RESERVED RUNNING balance (`GetRunningBalance` / `GetRunningScience` /
`GetRunningRep`), not the reservation-aware patch target: a reservation legitimately
lowers the spendable target below live on plain cutoff walks (Blocker 2), so keying on
the target would defeat overspend prevention; the running balance instead falls below
live ONLY when an earning channel is missing (the BUG-A corruption). The clamp is
suppressed when any of FIVE time-travel signals is set - rewind in progress, active
re-fly marker, active merge journal, the tombstone tail, and (Blocker 1) the deferred
rewind resource-adjustment in progress, which is the ONLY signal true when a plain
Rewind-to-Launch writes its authoritative drawdown. The clamp covers funds, science,
and reputation (rep has no reservation, so its running balance is its target). It is
surgical (per resource), idempotent (re-applies the same floor, no oscillation),
non-destructive (player keeps progress), and self-healing (stops once the leak is
fixed); per-subject science is patched unclamped, a documented limitation called out in
the clamp WARN (MINOR 5). The guard is unconditional with no setting (player decision
2); when it prevents a wipe it always WARN-logs running/live/target and posts one
session-latched short native ScreenMessage naming the protected resource via
`ParsekLog.ScreenMessage`, the same "Recording STARTED"-style notice (player decision
1). The clamp being unconditional matches the codebase precedent for correctness
behaviors with no gameplay downside.
