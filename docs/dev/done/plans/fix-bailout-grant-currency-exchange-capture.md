# Capture stock CurrencyExchanger strategy currency (Bail-Out Grant)

Status: implementing (2026-05-20). Follow-up to #439 Phase A/C.

## Problem

Stock KSP's Bail-Out Grant is a one-shot Administration strategy that exchanges
reputation for funds via a `CurrencyExchanger` effect. On `Strategy.Activate()`
the effect runs real wallet mutations in `OnRegister()`:

- `Reputation.Instance.AddReputation(-(rep+1000)*share, TransactionReasons.StrategyInput)`
- `Funding.Instance.AddFunds((rep+1000)*share*rate, TransactionReasons.StrategyOutput)`

All `initialCost*` are 0, so Parsek's `OnStrategyActivated` (which reads only
`InitialCostFunds/Reputation/Science`) captures nothing. The two `AddFunds` /
`AddReputation` calls fire `OnFundsChanged` / `OnReputationChanged`, which Parsek
records as `FundsChanged` / `ReputationChanged` events, but
`GameStateEventConverter.ConvertEvent` drops both event types to `null`. Result:
the exchange never enters the ledger. The next recalc patches funds to
`seed + tracked - tracked` (missing the `StrategyOutput` credit) and reputation
to `runningRep` (missing the `StrategyInput` debit), so Parsek silently REVERTS
the strategy: the granted funds vanish and the spent reputation is refunded.

This is the one stock case the #439 income-diversion analysis did not cover:
#439 correctly handled the ongoing `CurrencyConverter` (which only mutates an
in-flight `CurrencyModifierQuery`, captured via post-transform contract values),
but the one-time `CurrencyExchanger` does a genuine direct wallet mutation under
the distinct reasons `StrategyInput` / `StrategyOutput`.

Severity: low likelihood (Bail-Out Grant is obscure and only activatable at
non-positive reputation), real impact (granted funds disappear). Scope: stock
produces exactly two legs (funds gain via `StrategyOutput`, rep loss via
`StrategyInput`).

## Design

Capture the two confirmed stock legs as ledger actions anchored at the event UT,
tagged source `Strategy`:

- `FundsChanged(StrategyOutput)` with delta > 0 -> `FundsEarning`
  (`FundsSource = Strategy`, `FundsAwarded = delta`). Funds are linear; the
  recalc adds it directly.
- `ReputationChanged(StrategyInput)` with delta < 0 -> `ReputationPenalty`
  (`RepPenaltySource = Strategy`, `NominalPenalty = -delta`). Treated as
  ALREADY EFFECTIVE: `ReputationModule` must NOT re-apply the reputation curve
  for this source.

### Why the reputation leg bypasses the curve

KSP's `AddReputation` already applied the granular curve, so the recorded
`ReputationChanged` delta is the final actual change. The exchanger's nominal
input is `(repAtActivation + 1000) * share` (itself rep-dependent and only
available via reflection into the effect's private fields). Re-deriving a nominal
is fragile and re-applying the forward curve to the actual delta would
double-apply it. Because this is a one-time KSC event at a fixed UT (not a
recording-associated reward that should re-curve when the timeline reorders),
storing the actual delta as already-effective is both simpler and faithful to
what KSP did.

### Ingestion

KSC strategy use has no recording commit, so the events are forwarded to the
ledger immediately from `OnFundsChanged` / `OnReputationChanged` via
`LedgerOrchestrator.OnKscSpending(evt)` (mirroring the contract handlers),
gated by `ShouldForwardDirectLedgerEvent` so a strategy used during a flight
recording still flows through the commit-time path instead. `OnKscSpending`
runs `ConvertEvent`, adds the action, and triggers a current-UT recalc+patch.

### Reconciliation

The post-walk reconciler is action-driven and only compares actions whose
`ClassifyPostWalk` returns `Reconcile = true`; it never warns about unmatched
events. `ReputationPenaltySource.Strategy` already returns `Reconcile = false`.
`FundsEarningSource.Strategy` is added to the FundsEarning skip list so the
captured legs are not falsely reconciled against a `FundsChanged(Other)` event.
Per-action KSC reconcile (`ReconcileKscAction`) already classifies FundsEarning
and ReputationPenalty as Transformed/skip, so no false WARN there either.

### Load / double-count safety

New saves persist the directly-forwarded ledger actions; the full
event-to-action re-conversion (`MigrateOldSaveEvents`) runs only for legacy
saves that have no ledger file, so there is no per-load double-count.

## Changes

1. `GameAction.cs`: add `FundsEarningSource.Strategy = 6` (append-only; serialized as int).
2. `GameStateEventConverter.cs`: split `FundsChanged` / `ReputationChanged` out of
   the null-return group; convert `StrategyOutput` funds gains and `StrategyInput`
   reputation losses; all other reasons still return null.
3. `GameStateRecorder.cs`: forward `FundsChanged(StrategyOutput)` and
   `ReputationChanged(StrategyInput)` to `OnKscSpending` under the
   `ShouldForwardDirectLedgerEvent` gate.
4. `ReputationModule.cs`: `ProcessRepPenalty` bypasses the curve when
   `RepPenaltySource == Strategy` (literal already-effective loss).
5. `PostWalkActionReconciler.cs`: skip reconcile for `FundsEarningSource.Strategy`.
6. `GameActionDisplay.cs`: display label for `FundsEarningSource.Strategy`.

## Out of scope

- Modded / non-stock `CurrencyExchanger` legs that move science, or that produce
  reputation/funds in the opposite direction (e.g. Funds->Reputation output).
  Only the two stock-confirmed legs are captured; other reasons remain dropped.
  A symmetric generalization can append later without schema migration.
- Reconciliation safety net (paired-event delta check) for the captured legs.

## Tests

- Converter: `StrategyOutput` funds event -> `FundsEarning(Strategy)` with the
  delta; `StrategyInput` rep event -> `ReputationPenalty(Strategy)` with the
  magnitude; other-reason `FundsChanged`/`ReputationChanged` still return null.
- `ReputationModule`: Strategy-source penalty applies the literal delta without
  the curve; non-Strategy penalty still curves (regression guard).
