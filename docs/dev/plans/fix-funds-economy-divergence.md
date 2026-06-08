# Fix funds / economy divergence (two career-economy bugs)

Status: IMPLEMENTATION-READY plan, incorporates adversarial review round 1
(approve-with-changes; design validated in all four sign combinations,
accuracy/wording corrections folded in). No code written on this branch yet.
Implement on `fix-funds-economy-divergence` (off `origin/main`). Verified against
HEAD `96f5c5ad1` and the evidence log `logs/2026-06-08_2110_funds-bug/KSP.log`
(169392 lines).

Two independent bugs corrupt the career economy in opposite directions. They are
unrelated in mechanism but share the same blast radius (the ledger / patcher
economy pipeline), so they are planned together and (recommended) shipped as two
commits on this one branch.

------------------------------------------------------------------------

## 1. Summary of both bugs (verified)

### Bug 2 (PRIMARY) - scene-load funds REFUND via an asymmetric drawdown guard

A plain scene transition silently REFUNDED a facility-upgrade spend.

Verified log evidence:

- `KSP.log:164416` (21:07:59.404): `Game state: FundsChanged -451000
  (StructureConstruction) -> 66387` - the player upgraded a facility, live funds
  dropped 517386 -> 66387 (the preceding `AddEvent` row `164415` shows the
  `StructureConstruction` FundsChanged event being stored).
- `KSP.log:166417` (21:08:06.192): `RecalculateAndPatch: drawdown-guard
  authoritativeReduction=False (rewinding=False, reFlyMarker=False,
  mergeJournal=False, tombstone=False, rewindResAdjust=False)` - the next recalc
  ran with NO time-travel authority (a plain SPACECENTER -> EDITOR transition; not
  a rewind / re-fly).
- `KSP.log:166422`: `PatchFunds: 66386.5 -> 415466.3 (delta=349079.8,
  target=415466.3)` - the patch REFUNDED 349079.8, putting most of the facility
  spend back.
- `KSP.log:166423` (same recalc): `PatchReputation: GUARDED DRAWDOWN clamped
  resource=Reputation running=45.54 live=71.61 ... clampedTo=71.61` - in the SAME
  recalc, reputation WAS guard-clamped because its target was BELOW live. This is
  the asymmetry, captured in one frame: the downward direction is protected, the
  upward direction is not.

Running-balance arithmetic (verified from the per-action `[Funds]` walk
`164400`-`166416`, final rows at 21:08:06.190):

```
seed                        72840
+ total earnings           590657.28   (final "totalEarnings=590657.28161...")
- total spendings          ~248031     (vessel builds + recovery; NO facility cost)
= running balance           415466.27   (final "runningBalance=415466.27429...")
```

`GetAvailableFunds()` = `415466.3` -> that is the patch `target`. The `-451000`
facility spend is NOT in the running balance because
`GameStateEventConverter.ConvertEvent` (HEAD `GameStateEventConverter.cs:228`,
`case FundsChanged: return ConvertStrategyExchangeFunds(...)`) converts ONLY the
two Bail-Out-Grant strategy reasons and returns null for every other reason,
including `StructureConstruction`. So the facility spend is observed and patched
as a facility LEVEL change (via `FacilitiesModule` / `FacilityStatePatcher`) but
never enters the funds ledger as a spending. The ledger funds model is structurally
`running > live` whenever the player has spent on facilities.

Mechanism: `KspStatePatcher.IsGuardableDrawdown` (HEAD
`KspStatePatcher.cs:2405`) is `runningBalance < currentLive - epsilon` - it fires
ONLY when running is BELOW live. `ApplyDrawdownGuard` (HEAD
`KspStatePatcher.cs:2420`) clamps the target UP to live only in that downward case.
When `running > live` (the facility-spend leak), the guard does nothing and
`PatchFunds` (HEAD `KspStatePatcher.cs:721`, call site `757`) applies the full
upward delta, refunding the spend. `PatchScience` (HEAD `:89`, call site `:143`)
and `PatchReputation` (HEAD `:808`, call site `:842`) share the exact same
`ApplyDrawdownGuard` call shape and therefore the same upward hole.

### Bug 1 - stock contract completion re-fires; Parsek bakes N copies into the ledger

A single contract completion fired the stock award sequence 4 times, and Parsek
recorded 4 ledger rows for it.

Verified log evidence (`Awarding ... funds to player for contract completion` is a
STOCK `Contract` log line, NOT Parsek):

- `KSP.log:17201, 17227, 17250, 17271` (19:15:43.089-.111, span 22 ms): four
  `Awarding 63250 funds to player for contract completion` lines for ONE contract
  (`1eb2baf9-afce-48c7-82d6-364dae85f57a`, "Science data from surface of The
  Mun."). Each repeat is a FULL stock award cycle (funds + science + reputation +
  the Completion Rewards banner + a fresh `ContractOffered`). Live funds climbed
  305131 -> 368381 -> 431631 -> 494881.
- A second contract over-completed 3x: `KSP.log:158648, 158672, 158695`
  (21:06:49, 50560.12 each). Seven `Awarding ... funds` lines total in the log.

Parsek's role (verified):

- `GameStateRecorder.OnContractCompleted` (HEAD `GameStateRecorder.cs:501`,
  subscribed at `:307` via `GameEvents.Contract.onCompleted`) emits one
  `ContractCompleted` event per stock fire. It was invoked 4 times because stock
  fired `onCompleted` 4 times. Parsek never calls `Contract.Complete()`.
- The resource events DID correctly collapse: `GameStateStore.AddEvent` (HEAD
  `GameStateStore.cs:31`) coalesces same-type + same-tag resource events within
  `ResourceCoalesceEpsilon = 0.1 s` (`GameStateStore.cs:21`). The
  `FundsChanged`/`ScienceChanged`/`ReputationChanged` rows stayed at totals 30/31/32
  (only one `Coalesced FundsChanged` line at `17229`, the rest rate-limited).
- The `ContractCompleted` events did NOT collapse: `IsResourceEvent`
  (`GameStateStore.cs:104`) returns false for `ContractCompleted`, so it is not
  coalesced. The store accumulated FOUR rows: `AddEvent: ContractCompleted ...
  (total=33)`, `(total=34)`, `(total=35)`, `(total=36)` at `KSP.log:17225, 17247,
  17269, 17289`, all same contractId, same UT 475409.6, same recordingId tag.
- At commit, `GameStateEventConverter.ConvertEvents` (called from
  `LedgerOrchestrator.OnRecordingCommitted`, HEAD `LedgerOrchestrator.cs:277`) has
  NO intra-batch dedup (it converts each event; `GameStateEventConverter.cs:87-98`)
  -> four `ContractComplete` GameActions (`ConvertContractCompleted`,
  `GameStateEventConverter.cs:702`), each carrying funds=63250, rep=9, sci=4.
- `DeduplicateAgainstLedger` (HEAD `LedgerOrchestrator.cs:847`, called at
  `:307`) only removes candidates that match the EXISTING ledger, not duplicates
  WITHIN the candidate batch, so all 4 survive. `Ledger.AddActions` (HEAD
  `Ledger.cs:89`) appends blindly, no dedup. Result: 4x the contract reward
  (4 x 63250 = 253000) baked into the ledger as ContractComplete earnings.
- The no-recorder direct path is also vulnerable: `OnContractCompleted` calls
  `LedgerOrchestrator.OnKscSpending(evt)` (HEAD `GameStateRecorder.cs:534`) when
  `ShouldForwardDirectLedgerEvent` is true; `OnKscSpending` (HEAD
  `LedgerOrchestrator.cs:2812`) does `Ledger.AddAction` with no dedup at all.

Confidence on the CAUSE: HIGH that Parsek does not cause the re-fire (no Parsek
Harmony patch touches the contract-completion path - `Patches/` has
`ContractAcceptPatch` (accept-only Prefix) and `ProgressRewardPatch`
(`ProgressNode.AwardProgress`, milestones), neither on `Contract.Complete` /
award). HIGH that the runaway is STOCK / external re-entrancy (each repeat re-emits
the full stock banner + a fresh stock `ContractOffered`, which only the stock
`ContractSystem` produces). MODERATE on the precise stock trigger (no
ContractConfigurator / Strategia loaded; stock contracts only - loaded mods:
ModuleManager, ClickThroughBlocker, Harmony, ToolbarControl,
HideEmptyTechTreeNodes, Kopernicus, KSPCommunityFixes, KSPTextureLoader,
ModularFlightIntegrator). The log does not capture WHY stock fired `onCompleted`
4x, so the root stock cause is NOT pinned. Therefore the Parsek-side fix is
DEFENSIVE de-duplication at observation time, not a cause-fix.

------------------------------------------------------------------------

## 2. Bug 2 fix - symmetric no-authority clamp

### 2.1 The required shape (and why naive symmetry is WRONG)

The prompt's hypothesis ("make the no-time-travel clamp symmetric so live is the
source of truth in both directions") is correct in spirit but must NOT be a blanket
"clamp target to live in both directions". A blanket clamp breaks the legitimate
RESERVATION case, which is the entire reason `IsGuardableDrawdown` keys on the
running balance rather than the target:

- Reservation case (covered by `DrawdownGuardTests.Blocker2_*`): `running == live`
  (or `running >= live`), `target < live` because a committed FUTURE spend reserves
  against the spendable amount. This MUST stay unclamped (write the lower target so
  overspend prevention holds). A symmetric "clamp to live both ways" would clamp UP
  to live and re-enable the reserved spend. REGRESSION.

The actual upward leak is distinguished by the RUNNING balance, exactly mirroring
the downward guard:

- Downward leak (existing): `running < live` with no authority -> earning channel
  missing -> clamp target UP to live (floor).
- Upward leak (Bug 2, NEW): `running > live` with no authority -> a real spend the
  ledger does not model (facility upgrade) -> clamp target DOWN to live (ceiling).

In the reservation case `running >= live`, so the new upward guard does not fire
on `running == live`; and `target` being below live with `running == live` is the
reservation, untouched. The two guards are symmetric on the RUNNING discriminator,
NOT on the target.

### 2.2 Code changes (file: `Source/Parsek/GameActions/KspStatePatcher.cs`)

1. Add a pure predicate mirroring `IsGuardableDrawdown`:

   ```
   internal static bool IsGuardableUplift(double runningBalance, double currentLive, double epsilon)
       => runningBalance > currentLive + epsilon;
   ```

2. Extend `ApplyDrawdownGuard` (HEAD `:2420`) to handle BOTH directions when
   `!authoritativeReduction`. New logic:

   - If `IsGuardableDrawdown(running, live, eps)` -> clamp UP:
     `clampedTarget = max(patchTarget, live)` (unchanged existing behavior).
   - Else if `IsGuardableUplift(running, live, eps)` -> clamp DOWN:
     `clampedTarget = min(patchTarget, live)`. Set `clamped = true` ONLY when the
     value actually changed (see 2.4 last bullet).
   - Else -> return `patchTarget` unchanged (covers reservation:
     `running >= live`, `IsGuardableUplift` false because `running` is within eps
     of `live`, and `target < live` is preserved).

   SIGNATURE - do NOT change the existing one (source-breaking). The existing
   `out bool clamped` signature has ~10 call sites in `DrawdownGuardTests.cs`
   (lines 146, 160, 173, 187, 200, 214, 227, 351) that would fail to compile if a
   new `out` param were added. Resolution: ADD AN OVERLOAD. Keep the current
   `ApplyDrawdownGuard(patchTarget, running, live, eps, authoritative, resource,
   out bool clamped)` as a thin wrapper that delegates to a NEW 2-out overload
   `ApplyDrawdownGuard(..., out bool clamped, out ClampDirection direction)`
   (new internal enum `{ None, Up, Down }`). Existing tests stay untouched and
   keep compiling; only the 3 production `Patch*` sites and the NEW direction tests
   call the 2-out overload.

   Authoritative path is unchanged: when `authoritativeReduction == true`, NEITHER
   clamp fires, so time-travel restores move funds in either direction (a re-fly
   that legitimately lowers funds, OR a rewind that raises them back, both pass).

3. `EmitDrawdownGuardClamp` (HEAD `:2443`): extend wording so the DOWN direction
   logs distinctly, e.g. `GUARDED UPLIFT clamped` vs the existing `GUARDED
   DRAWDOWN clamped`, both carrying `running` / `live` / `wouldBeTarget` /
   `clampedTo`. The DOWN-direction toast text is "Held your funds at the spent
   value" (ASCII, no em dash). Add a DEDICATED session latch
   `fundsUpliftClampToastShownThisSession` (and the science/rep analogs if those
   resources clamp down) so the uplift toast fires once per session independently
   of the existing drawdown latch; reset all latches in
   `ResetDrawdownGuardSessionLatches` (HEAD `:2390`).

4. Call sites - `PatchFunds` (`:757`), `PatchScience` (`:143`), `PatchReputation`
   (`:842`): pass the new `out direction`, and when `clamped`, branch
   `EmitDrawdownGuardClamp` on direction to pick the right WARN/toast and set
   `target = effTarget`. No other change to the delta/apply arithmetic.

### 2.3 Do PatchScience / PatchReputation need the symmetric fix too?

YES, all three. They share `ApplyDrawdownGuard`, so the predicate change applies
uniformly. The same leak shape exists for any resource the ledger under-models:

- Science: a stock science SPEND the ledger does not model (e.g. a tech unlock
  channel gap) would leave `runningScience > live` and refund it on recalc. The
  science discriminator is already the pending-adjusted running balance
  (`ComputePendingAdjustedRunningScience`, HEAD `:709`); the new uplift guard reads
  the same value AND the patch `target` is the matching pending-adjusted value, so
  the two in-flight pending adjusters (KSC science credit / tech-unlock debit) keep
  a transient `running` vs `live` mismatch from a KNOWN in-flight reason from
  false-clamping, exactly as they do for the downward guard. The DOWN-clamp safety
  in the transient window comes specifically from the tech-unlock DEBIT adjuster:
  during a tech unlock the live singleton has already dropped while the matching
  ledger debit is still catching up, so the raw running would read ABOVE live (an
  apparent uplift); the debit adjuster SUBTRACTS that same pending debit from BOTH
  the discriminator and the target, pulling running back toward live and the target
  down with it, so no spurious DOWN clamp fires. Keep the recommended one InGameTest
  asserting no spurious DOWN clamp during a live KSC science-earn / tech-unlock
  window.
- Reputation: `PatchReputation` uses `SetReputation` (absolute), discriminator ==
  target. An external rep loss the ledger does not model would give `running >
  live` and the patch would restore rep upward. The DOWN clamp prevents that.
  (Note `target == running` here, so a DOWN clamp to live is a no-op when
  `target == running == something > live`? No: `min(target, live) = live` since
  `target = running > live`. Correct.)

### 2.4 Edge cases

- `running` within epsilon of `live` (both directions): neither guard fires
  (existing eps semantics preserved; covered by existing `*_EpsilonNoOp_*` tests,
  add an uplift-within-eps test).
- Reservation (`running >= live`, `target < live`): no uplift (running within eps
  of live for a pure load) -> unchanged. Add an explicit test combining a
  reservation target below live with `running == live` to prove no DOWN clamp.
- Authoritative reduction true: no clamp either way (existing + add an uplift +
  authoritative test).
- `target` already at/below live while `running > live` (e.g. a partial reservation
  on top of a facility leak): `min(target, live) = target`, `clamped` should be
  false in that sub-case (the target is already not refunding). Decide:
  `IsGuardableUplift` is on `running`, but the CLAMP only bites when
  `patchTarget > live`. Recommend: set `clamped = true` only when the clamp
  actually changed the value (`min(patchTarget, live) < patchTarget`), so a
  no-effect clamp does not emit a misleading WARN/toast. Mirror this for the UP
  direction too (it already returns `max` which may be a no-op when `target > live`
  - the existing `ApplyDrawdownGuard_TargetAboveLive_ReturnsTargetWhenClamped` test
  asserts `clamped=true` even when target>live, so changing UP semantics would break
  that test; keep UP as-is and apply the "only flag if value changed" rule to the
  NEW DOWN branch only, documenting the asymmetry).

### 2.5 Risk analysis vs authoritativeReduction / time-travel

- The guard is gated on `!authoritativeReduction`, computed by
  `LedgerOrchestrator.IsAuthoritativeReduction` (5 signals: rewinding, reFlyMarker,
  mergeJournal, tombstone, rewindResAdjust). The Bug-2 frame logged all five false
  (`KSP.log:166417`), so the new DOWN clamp WOULD have fired there and held funds at
  66386 (correct: the player really spent it). In every genuine time-travel context
  one signal is true, so neither clamp fires and the patch moves funds freely in
  either direction. No legitimate time-travel restore is affected.
- The `RunRewindReadbackGuard` (HEAD `:2576`) is a SEPARATE, independently-gated
  layer (armed only at the rewind apply boundary via `RewindReadbackGuard.Armed`)
  and is downward-only by design (it flags a recalc writing BELOW the real career
  floor). It is NOT changed by this fix and does not interact with the new uplift
  clamp (the uplift clamp only acts when NOT authoritative; the readback guard only
  acts when armed at rewind). They are orthogonal.
- WHY the DOWN clamp is safe (documented INVARIANT, not just "no counter-case").
  The clamp is correct BECAUSE two existing gates guarantee a non-authoritative
  recalc never legitimately holds an earning the ledger recorded but stock did NOT
  already apply live:
  1. The PATCH DEFERRAL GATE. `LedgerOrchestrator.GetKspPatchDeferralReason` (HEAD
     `LedgerOrchestrator.cs:2113`) defers the patch ENTIRELY (no clamp runs at all)
     whenever there is a live recorder, an active uncommitted flight tree, or a
     pending tree (`:2118` returns null only when `!hasActiveUncommittedTree &&
     !hasPendingTree`). So while a recording is in flight, stock KSP is applying
     earnings to the live singletons in real time and the recalc/patch does not
     fire. In-flight earnings therefore never reach a clamp.
  2. The COLD-LOAD DEFERRED-SEED WAIT. On cold load the recalc runs only after
     `DeferredSeedAndRecalculate` waits for a non-zero singleton, and `PatchFunds`
     early-returns on `!HasSeed`, so a not-yet-populated live value cannot be
     mistaken for a leak.
  Given those gates, every non-authoritative recalc that actually patches sees a
  live value that stock has already brought to truth, so live IS ground truth and
  clamping an upward target down to live is correct (Bug 2 is exactly this: a real
  facility spend already applied live, with the ledger lagging high). PRESERVE THIS
  INVARIANT: if a future change ever lets a commit-time recalc fire while the ledger
  genuinely LEADS live (an earning Parsek records that stock did NOT apply live),
  the DOWN clamp would wrongly suppress it. Any such change must re-evaluate this
  guard (and likely route through an authoritative signal instead).
- `RunRewindReadbackGuard` interaction unchanged (see prior bullet): orthogonal.

------------------------------------------------------------------------

## 3. Bug 1 fix - defensive contract-completion de-duplication

### 3.1 Established mechanism + confidence

- Stock KSP (or an external interaction) fires `Contract.onCompleted` N times for
  one contract within a few ms (CONFIRMED from the log: 4x and 3x, each a full
  stock award cycle). Root stock cause NOT pinned (the log does not capture it);
  confidence the runaway is stock-side and not Parsek-caused is HIGH.
- Parsek faithfully emits N `ContractCompleted` events; they are NOT coalesced
  (not a resource event), accumulate in `GameStateStore`, and bake N
  `ContractComplete` ledger rows at commit (and N rows via `OnKscSpending` on the
  no-owner path). CONFIRMED end-to-end against the log + source.

Decision: DEFENSIVE de-dup so the inflation is never baked into a future
recording/ledger. Do NOT attempt a stock cause-fix (unpinned, and Parsek does not
own the stock contract system).

### 3.2 Where to put the de-dup

Primary chokepoint: `GameStateRecorder.OnContractCompleted` (HEAD
`GameStateRecorder.cs:501`). Debounce a repeated completion for the same
`contract.ContractGuid` within a short UT window. The debounce check + early RETURN
must run at the TOP of `OnContractCompleted`, BEFORE the `Emit(ref evt, ...)` store
write (`:526`) AND BEFORE the `ShouldForwardDirectLedgerEvent -> OnKscSpending`
direct-ledger forward (`:533`). Returning before BOTH is what covers the two
inflation paths (tagged-commit via the store, and no-owner-direct via OnKscSpending),
which a `GameStateStore.AddEvent` change alone would not (AddEvent does not see the
OnKscSpending direct call).

Mechanism (follows the `PendingMilestoneEventById` precedent exactly):

- Add a static dict `lastContractCompletionUtByGuid` (contract guid -> last
  completion UT), mirroring `PendingMilestoneEventById` (HEAD
  `GameStateRecorder.cs:1426`). On entry, if the same guid completed within the
  dedup window (UT within `GameStateStore.ResourceCoalesceEpsilon = 0.1 s`,
  reusing the existing constant; UT is stable across the burst at 475409.6), log a
  single `Verbose`/`Info` "deduped duplicate ContractCompleted" line and RETURN
  before Emit / OnKscSpending. Otherwise record the guid->UT and proceed.
- TEARDOWN follows the milestone pattern, NOT a scene subscription (the recorder
  does NOT hook `onGameSceneSwitchRequested`). Add a `ClearContractCompletionDedup
  (reason)` that empties the dict, and CALL IT from the same three places
  `ClearPendingMilestoneEvents` is called: `Subscribe()` (HEAD
  `GameStateRecorder.cs:302`), `Unsubscribe()` (`:351`), and `ResetForTesting()`
  (`:185`). That clears the window on every scene-entry/exit and between tests, so
  a legitimate RE-completion in a different session/timeline is not suppressed.
- Apply the SAME debounce to `OnContractFailed` / `OnContractCancelled`? Scope
  decision: the log only shows ContractCompleted re-firing. Keep the fix narrow to
  ContractCompleted (the only observed and the only positive-reward inflation).
  Note the others as a possible follow-up; do not gold-plate.

Pure helper for testability: extract the decision as
`internal static bool IsDuplicateContractCompletion(string contractGuid, double ut,
IDictionary<string,double> lastSeenByGuid, double window)` - returns true (=
duplicate, suppress) when the guid is present with `|ut - lastSeen| <= window`,
otherwise records `lastSeenByGuid[guid] = ut` and returns false. Pure with respect
to Unity (the caller passes the static dict), so it is directly unit-testable per
the project rule (pure/static internal). `OnContractCompleted` stays thin: call the
helper against the static dict, RETURN on true.

Secondary defense-in-depth (RECOMMENDED, low cost): add intra-batch contract-lifecycle
de-dup so even a pre-existing burst already in the store cannot double-bake. Two
options - pick ONE:

- (a) In `GameStateEventConverter.ConvertEvents` (HEAD `GameStateEventConverter.cs`
  loop ~`:40-99`): track converted contract-lifecycle actions by (Type, ContractId,
  UT-bucket) within the batch and skip a duplicate. Pure, log a summary count.
- (b) In `DeduplicateAgainstLedger` (HEAD `LedgerOrchestrator.cs:847`): also dedup
  the candidate list against ITSELF for contract-lifecycle types before/while
  comparing to the existing ledger.

Recommendation: ship the recorder debounce (3.2 primary) as the fix; include (a)
as a cheap belt-and-suspenders in the same commit because the converter is the
single commit chokepoint and the existing `GetActionKey`/`GetDedupOccurrenceUt`
helpers (HEAD `:907` / `:887`) already give the exact match key. Do NOT touch
`GameStateStore.AddEvent` coalescing (it does not cover the direct path and mixing
non-resource events into the resource-coalesce loop is riskier than a dedicated
recorder debounce).

HARD CONSTRAINT respected: this acts at OBSERVATION/COMMIT time for FUTURE
recordings only. No existing recorded file (.prec / GameStateStore-on-disk / ledger
rows) is rewritten; no load-time rewrite path is added.

### 3.3 Limitation (state explicitly in the writeup)

- Recordings/ledgers already inflated by this burst stay as-is (immutable). They
  are NOT repaired by this fix.
- The original burst's extra payouts ALSO already hit the player's LIVE funds in
  real time (stock applied each `Awarding ... funds` cycle: 305131 -> 494881). Bug
  2's fix does NOT claw that back or repair it - that live inflation is done and
  belongs to a separate, explicitly-authorized repair if ever wanted.
- What Bug 2's fix DOES do: it stops the inflated LEDGER from refunding/inflating
  live funds FURTHER on a future non-authoritative recalc. With the symmetric uplift
  clamp, a recalc whose ledger target sits ABOVE current live (because of baked-in
  extra contract rewards, or a facility leak) is clamped to live and cannot push
  live funds up beyond live. (It can still apply during a genuine authoritative
  time-travel recalc, which is out of scope here.) Do not read this as "Bug 2 fixes
  the live inflation" - it bounds future drift, it does not undo past drift.

------------------------------------------------------------------------

## 4. Tests

All in `Source/Parsek.Tests/`. Log-capture pattern (`ParsekLog.TestSinkForTesting`),
`[Collection("Sequential")]` where shared statics are touched.

### Bug 2 - extend `DrawdownGuardTests.cs`

- `IsGuardableUplift_RunningMoreThanEpsilonAboveLive_IsGuardable` - running 415466,
  live 66386 -> true (the Bug-2 signature).
- `IsGuardableUplift_RunningWithinEpsilonAboveLive_IsNotGuardable` - rounding noise.
- `IsGuardableUplift_RunningEqualsLive_IsNotGuardable` - boundary.
- `IsGuardableUplift_RunningBelowLive_IsNotGuardable` - downward is the other guard.
- `ApplyDrawdownGuard_RunningAboveLive_NoSignal_ClampsDownToLive` - patchTarget
  415466, running 415466, live 66386, no authority -> clamped=true, direction=Down,
  result=66386 (the exact Bug-2 numbers).
- `ApplyDrawdownGuard_RunningAboveLive_Authoritative_ReturnsTargetUnchanged` -
  same numbers, authoritativeReduction=true -> clamped=false, result=415466
  (time-travel restore unaffected).
- `ApplyDrawdownGuard_RunningAboveLive_TargetAlreadyBelowLive_DoesNotFlagNoOpClamp` -
  running 415466 > live, but target 50000 < live 66386 -> min is target, value
  unchanged -> clamped=false (no misleading WARN). (Implements 2.4 last bullet.)
- `Reservation_RunningEqualsLive_TargetBelowLive_NoUpliftClamp` - running 33000 ==
  live 33000, target 18000 -> neither guard fires, result 18000 (proves the
  reservation case still passes with the symmetric change; complements the existing
  `Blocker2_*`).
- `EmitDrawdownGuardClamp_Funds_Down_WarnsWithUpliftWordingAndToastsOnce` - asserts
  the DOWN-direction WARN ("GUARDED UPLIFT clamped" or chosen wording) + numbers +
  single session toast.
- Mirror one DOWN-direction case each for science and reputation
  (`ApplyDrawdownGuard_RunningAboveLive_NoSignal_ClampsDownToLive` parametrized, or
  three small facts) to prove all three resources clamp down.

In-game test (`InGameTests/RuntimeTests.cs`): extend / add alongside the existing
`DrawdownGuard_ClampsLeakAndAuthorizesSignal` an upward-leak variant
`DrawdownGuard_RefundLeak_ClampedDownToLive` that drives a real
`Funding.Instance.AddFunds` so the live `PatchFunds` write path (not just the pure
helper) is exercised - the xUnit path early-returns on the null singleton.

### Bug 1 - new `ContractCompletionDedupTests.cs` (or extend `GameStateEventTests.cs`)

- `IsDuplicateContractCompletion_FirstSeen_NotDuplicate_RecordsLastSeen`.
- `IsDuplicateContractCompletion_SameGuidWithinWindow_IsDuplicate` - four calls,
  same guid, UT 475409.6 each -> first accepted, next three flagged duplicate.
- `IsDuplicateContractCompletion_SameGuidOutsideWindow_NotDuplicate` - a genuine
  later re-completion (UT far apart) is allowed through.
- `IsDuplicateContractCompletion_DifferentGuid_NotDuplicate`.
- If shipping converter belt-and-suspenders (3.2a): in
  `GameStateEventConverterTests.cs`,
  `ConvertEvents_DuplicateContractCompletedSameUt_ConvertsOnce` - feed 4
  ContractCompleted events (same guid/UT/tag) -> exactly one ContractComplete
  action.

------------------------------------------------------------------------

## 5. Doc updates (staged in the same commits)

Current version: `0.10.1` (CHANGELOG `## 0.10.1`, AssemblyInfo `0.10.1.0`). Add
under `### Bug Fixes` (1 line per item, <=2 sentences, user-facing, ASCII, no em
dash).

### CHANGELOG.md (Bug 2 commit)

```
- Fixed a critical bug where a normal scene change (for example leaving the Space
  Center for the Editor) could silently refund money you had just spent, such as a
  facility upgrade, restoring your funds to before the purchase. The keep-what-you-
  earned safety net now also protects against an unexpected increase: when no rewind
  or re-fly is active your current funds, science, and reputation are the source of
  truth, so a bookkeeping gap can no longer hand money back.
```

### CHANGELOG.md (Bug 1 commit)

```
- Fixed a bug where a single contract could be recorded as completing several times
  in a row (a stock re-fire), multiplying its recorded reward in Parsek's ledger.
  Duplicate completions of the same contract within the same instant are now
  ignored, so each contract is recorded once.
```

### docs/dev/todo-and-known-bugs.md

Add two `## Done` entries (top of file, matching the existing BUG-x format), each
with Symptom / Root cause / Fix / log line refs / test names. Cross-reference: the
Bug 2 entry notes it extends the existing "Recalc-patch drawdown guard" entry
(2026-06-07) from downward-only to symmetric; the Bug 1 entry notes the
inflated-recording limitation and that Bug 2 independently stops live re-inflation.

### .claude/CLAUDE.md

No change required (no file-layout / build / workflow / key-pattern change). The
symmetric guard is an extension of an existing documented mechanism.

------------------------------------------------------------------------

## 6. Commit plan

TWO commits on this one branch, one per bug, each self-contained with its tests +
doc updates:

1. Commit A - Bug 2 symmetric drawdown/uplift guard (KspStatePatcher + DrawdownGuardTests
   + RuntimeTests + CHANGELOG + todo). This is the PRIMARY, well-understood fix and
   stands alone.
2. Commit B - Bug 1 contract-completion debounce (GameStateRecorder + optional
   converter dedup + new tests + CHANGELOG + todo).

Rationale for two: the bugs are mechanically independent, touch different
subsystems (patcher vs recorder/converter), and a reviewer can evaluate the
higher-risk symmetric-guard change in isolation from the lower-risk debounce. One
PR off this branch carrying both commits.

------------------------------------------------------------------------

## 7. Open questions / residual risks (RESOLVED in review round 1)

All seven resolved by the reviewer (all accepted). Recorded here for the implementer.

1. RESOLVED. Bug 2 DOWN-clamp toast copy = "Held your funds at the spent value"
   (ASCII, no em dash), with a dedicated latch `fundsUpliftClampToastShownThisSession`
   reset in `ResetDrawdownGuardSessionLatches` (HEAD `:2390`). Folded into 2.2 step 3.
2. RESOLVED. "Only flag clamped when the value actually changed" is applied to the
   NEW DOWN branch only; the existing UP behavior (and its
   `ApplyDrawdownGuard_TargetAboveLive_ReturnsTargetWhenClamped` assertion) is left
   untouched. The UP/DOWN asymmetry is accepted and documented (2.2 step 2, 2.4).
3. RESOLVED. The science down-clamp is safe in the transient window because the
   tech-unlock DEBIT adjuster lowers BOTH the discriminator and the target (2.3,
   corrected). Keep one InGameTest asserting no spurious DOWN clamp during a live
   KSC science-earn / tech-unlock window.
4. RESOLVED. Bug 1 root stock cause stays unpinned; defensive-only debounce is
   accepted (the fix bounds the inflation regardless of the stock trigger). No
   stock repro is required before merge.
5. RESOLVED. Bug 1 debounce window = `ResourceCoalesceEpsilon = 0.1 s` (UT key),
   matching the resource-coalesce design and the observed burst.
6. RESOLVED. Bug 1 scope = `OnContractCompleted` only; ContractFailed/Cancelled are
   not in the observed defect class and are left out (not gold-plated).
7. RESOLVED. Existing inflated ledgers/recordings are NOT repaired (immutable; matches
   the no-modify-recordings hard rule). Any repair is a separate, explicitly-authorized
   save/ledger operation.

Residual risk to carry forward (NOT an open question, an invariant to preserve):
the Bug-2 DOWN clamp depends on the patch-deferral gate + cold-load deferred-seed
wait guaranteeing live is ground truth on every non-authoritative recalc (see 2.5).
A future change that lets a commit-time recalc fire while the ledger genuinely leads
live would need to re-evaluate this guard.
