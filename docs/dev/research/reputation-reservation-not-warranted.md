# Reputation reservation: investigated, not warranted

Status: investigation closed (2026-05-20). Conclusion: do NOT add a reputation
reservation/escrow system mirroring Funds/Science. This document records the
premise check, the evidence, and the reasoning so the question does not get
re-opened without new information.

## The question

Funds and Science escrow committed-future spend out of the bar the player sees:
`KspStatePatcher.PatchFunds` patches `FundsModule.GetAvailableFunds()` and
`PatchScience` patches `ScienceModule.GetAvailableScience()`, both of which are
the balance MINUS a reserved future drawdown. `PatchReputation` patches
`ReputationModule.GetRunningRep()`, the true current value, with no reservation.

Should reputation get the same reservation treatment? The premise to verify
first: is the reputation wallet ever genuinely DEBITED by a committed-future
action in a way a reservation would need to protect against?

## Short answer

No. A reputation reservation would have no consumer, no semantic meaning, and
would actively break stock KSP gameplay. The current behavior (patch the true
`runningRep`) is correct and intentional.

## How the reservation system actually works (and what it is for)

- The live recalc uses a UT cutoff at "now" when committed-future actions exist
  (`LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions` /
  `RecalculateAndPatchForLiveTimelineEvent`). The main walk then reflects state
  as of "now": `runningBalance`, `runningScience`, `runningRep`.
- A SEPARATE full-timeline projection pass runs only for modules that implement
  `ICashflowProjectionModule` (`RecalculationEngine.ProjectAvailability`,
  `ApplyProjectedAvailability`). It walks future actions and computes
  `available = min projected balance`, clamped to >= 0. Funds and Science
  implement this interface; `ReputationModule` does not.
- The ONLY purpose of that reserved/available number is to block a real-time
  SPEND that a committed-future action needs, against a resource floored at
  zero. Its only consumers are `LedgerOrchestrator.CanAffordScienceSpending`
  and `CanAffordFundsSpending`, used by `TechResearchPatch` (block unlocking a
  node) and the facility-upgrade path. See `TechResearchPatch.cs` line ~37.

So "reservation" is a spend-affordability gate, not a correction to the balance.
The balance itself (runningBalance / runningScience / runningRep) already
materializes every committed debit at the moment its UT passes the cutoff.

## Why reputation does not fit the model

1. There is no `ReputationSpending` action type (`GameActionType` enum,
   `GameAction.cs`). Reputation is only earned, penalized, or seeded. It is
   never SPENT against a gate.
2. There is no `CanAffordReputationSpending` and no other consumer that would
   read an "available reputation."
3. Reputation has no zero floor. It ranges roughly [-1000, +1000] and goes
   negative freely (`SetReputation` clamps only to that range; the loss curve
   tapers near -1000 but there is no hard non-negativity constraint). Reserving
   to keep it non-negative is meaningless.

The design doc already states this intent:

- Section 5.6: "the two mechanisms keep new player spendings from making science
  or funds negative anywhere on the committed timeline. Reputation can go
  negative by design (penalties are unconditional)."
- Section 6: "There is no ledger-enforced invariant that depends on a minimum or
  exact reputation value (unlike funds, where spending affordability must be
  validated). Reputation affects contract offerings and strategy availability,
  but those are KSP-native checks at the current moment, not constraints the
  ledger enforces during the walk."

(See `docs/parsek-game-actions-and-resources-recorder-design.md`.)

## A reservation would actively break stock gameplay

Stock KSP gates several things on the CURRENT reputation balance. `PatchReputation`
sets `Reputation.Instance.reputation` to the true `runningRep` precisely so those
native checks see reality:

- `Strategies.Strategy.CanBeActivated` pre-checks
  `Reputation.Instance.reputation < InitialCostReputation` and blocks activation.
- Contracts gate offers/acceptance on minimum reputation.

If Parsek patched a reserved (lower) value, it would falsely block the player
from activating strategies and accepting contracts they can actually afford. For
funds, suppressing spendable balance is the intended gate; for reputation the
same suppression is a regression.

## The strategy / CurrencyConverter question (decompiled stock KSP)

Decompiled `Strategies.Strategy`, `Strategies.Effects.CurrencyConverter`,
`Strategies.Effects.CurrencyOperation`, `Strategies.CurrencyExchanger`, and
`Reputation` from `Assembly-CSharp.dll` (KSP 1.12.x).

CurrencyConverter strategies (Fundraising Campaign Rep->Funds, Unpaid Research
Rep->Science) are INCOME DIVERSION, never wallet debits:

- `CurrencyConverter.OnRegister()` subscribes
  `GameEvents.Modifiers.OnCurrencyModifierQuery`. The mutate logic computes
  `num = qry.GetInput(input) * share` then `qry.AddDelta(input, -num);
  qry.AddDelta(output, rate * num)`. It only ever mutates an in-flight
  `CurrencyModifierQuery` and never calls `AddReputation`. It skims a fraction
  of reputation the player is otherwise GAINING from a qualifying transaction.
- Parsek already handles this correctly: `StrategiesModule.TransformContractReward`
  is a documented identity no-op because the recorded contract reward values
  (`RepCompletion`, etc.) are already the post-transform amounts that KSP
  produced before the change event fired. Nothing to reserve.

The genuine one-off reputation WALLET debits do exist, but they are all live
KSC actions at the current UT, never committed-future:

- Strategy setup cost: `Strategy.Activate()` calls
  `Reputation.Instance.AddReputation(-InitialCostReputation, StrategySetup)`
  (Fundraising, Unpaid Research, Aggressive Negotiations, Leadership Initiative).
- Bail-Out Grant: a `CurrencyExchanger` one-time Rep->Funds spend on activation
  (reasons `StrategyInput` / `StrategyOutput`). NOTE: this investigation found
  Parsek did not capture the Bail-Out Grant exchange (its `InitialCost*` are all
  zero and the `FundsChanged` / `ReputationChanged` events were dropped), so the
  next recalc reverted it. Fixed separately: see
  `docs/dev/done/plans/fix-bailout-grant-currency-exchange-capture.md`. This is a
  current-UT KSC capture gap, not a reservation question.

Parsek captures strategy activation from the live `Strategy.Activate()` postfix
(`StrategyLifecyclePatch`) at `Planetarium.GetUniversalTime()`
(`GameStateRecorder.OnStrategyActivated`, reads `strategy.InitialCostReputation`
into `setupRep`). The resulting `StrategyActivate` action carries
`SetupReputationCost`, which `ReputationModule.ProcessStrategySetupReputation`
applies through the curve at that UT. Because the UT is always "now" or earlier,
it is never a future obligation to escrow; it lands in `runningRep` immediately
and correctly.

## Genuinely future-committable reputation debits

Two paths can reduce reputation at a future UT inside a committed timeline:

- Kerbal death during a committed / looping recording (reason `VesselLoss`,
  `ReputationPenaltySource.KerbalDeath`).
- Contract failure at a future deadline (`ContractFail` / `ContractCancel`
  carrying `RepPenalty` from `contract.ReputationFailure`; the projection
  shadow walk even synthesizes deadline-expired contract fails).

Both are applied correctly by `ReputationModule` through the non-linear curve
when their UT passes the cutoff, exactly mirroring how `runningBalance` handles
future funds penalties. What is "missing" relative to funds is only the
escrow/availability number, and per the reasoning above that escrow has no
consumer and no meaning for reputation.

## Known harmless asymmetries (noted, not bugs to fix)

- `FundsModule.TryGetProjectionDelta` reserves a strategy's `SetupCost` and a
  future `ContractFail` `FundsPenalty`; `ReputationModule` reserves neither
  `SetupReputationCost` nor future rep penalties. Inert for strategy setup
  because activation UT is always <= "now"; intentional for rep penalties per
  the no-floor / no-gate design.
- `ReputationChanged` GameStateEvents convert to null
  (`GameStateEventConverter.ConvertEvent`). Reputation enters the ledger only
  through dedicated discrete channels (ContractComplete `TransformedRepReward`,
  ContractFail/Cancel `RepPenalty`, MilestoneAchievement `MilestoneRepAwarded`,
  StrategyActivate `SetupReputationCost`). This is why the strategy rep setup
  cost is not double-counted against KSP's own `AddReputation`.

## Key evidence (file references)

- `Source/Parsek/GameActions/KspStatePatcher.cs`: `PatchFunds` /
  `PatchScience` / `PatchReputation` (around lines 556, 67, 621).
- `Source/Parsek/GameActions/FundsModule.cs`: `GetAvailableFunds`,
  `ICashflowProjectionModule`, `TryGetProjectionDelta`.
- `Source/Parsek/GameActions/ReputationModule.cs`: `GetRunningRep`,
  `ApplyReputationCurve`, `ProcessStrategySetupReputation`; no projection
  interface.
- `Source/Parsek/GameActions/RecalculationEngine.cs`: `ProjectAvailability` /
  `ApplyProjectedAvailability` (projection only for `ICashflowProjectionModule`).
- `Source/Parsek/GameActions/GameAction.cs`: `GameActionType` enum (no
  `ReputationSpending`).
- `Source/Parsek/GameActions/LedgerOrchestrator.cs`: `CanAffordFundsSpending` /
  `CanAffordScienceSpending` (no reputation equivalent).
- `Source/Parsek/Patches/TechResearchPatch.cs`: consumer of the science
  reservation gate.
- `Source/Parsek/GameActions/StrategiesModule.cs`: `TransformContractReward`
  documented no-op.
- `Source/Parsek/GameActions/GameStateEventConverter.cs`: `ReputationChanged`
  converts to null; dedicated rep channels.
- `Source/Parsek/Patches/StrategyLifecyclePatch.cs` and
  `Source/Parsek/GameStateRecorder.cs` `OnStrategyActivated`: strategy setup
  cost captured live at current UT.
- `docs/parsek-game-actions-and-resources-recorder-design.md` sections 5.6 and
  6: reputation can go negative by design; no min-rep invariant.
