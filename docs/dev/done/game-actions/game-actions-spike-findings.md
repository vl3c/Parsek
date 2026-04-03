# Game Actions — Phase 0 Spike Findings

Results from the four risk-reduction investigations run before implementation.

---

## Spike A: Reputation Curve Formula — RESOLVED

**Risk retired.** The exact formula has been extracted from decompiled Assembly-CSharp.dll.

### Algorithm

`Reputation.AddReputation(float r)` calls `addReputation_granular(r)` which:
1. Splits the nominal delta into integer-sized steps
2. Each step goes through `ModifyReputationDelta(input)` which applies a curve multiplier
3. The curve depends on whether the delta is positive (gain) or negative (loss)
4. The multiplier is read from a Unity AnimationCurve (cubic Hermite spline)

```
ModifyReputationDelta(delta):
  time = currentRep / 1000f           // normalize to [-1, +1]
  if delta < 0: return delta * reputationSubtraction.Evaluate(time)
  else:         return delta * reputationAddition.Evaluate(time)
```

### Curve Behavior

- **Gain curve:** multiplier ~2.0x at rep=-1000, ~1.0x at rep=0, ~0.0x at rep=+1000
  - Gains are doubled at minimum rep, unmodified at zero, effectively zero at maximum (soft ceiling)
- **Loss curve:** multiplier ~0.0x at rep=-1000, ~1.0x at rep=0, ~2.0x at rep=+1000
  - Losses are negligible at floor, unmodified at zero, doubled at ceiling (high rep is fragile)
- **No hard clamp** inside the granular loop. `SetReputation` clamps to [-1000, 1000] but `AddReputation` can slightly overshoot.

### Curve Keyframes (AnimationCurve, Hermite spline)

**reputationAddition (5 keys):**

| time | value | inSlope | outSlope |
|------|-------|---------|----------|
| -1.000108 | 2.001723 | 0.873274 | -0.025381 |
| -0.505605 | 1.500368 | -2.772799 | -2.772799 |
| 0.001540 | 0.999268 | 0.009784 | 0.009784 |
| 0.501354 | 0.503444 | -2.572293 | -2.572293 |
| 1.000023 | -0.000005 | -0.006748 | 1.003260 |

**reputationSubtraction (4 keys):**

| time | value | inSlope | outSlope |
|------|-------|---------|----------|
| -1.000136 | -0.000129 | -1216.706 | 510.160 |
| -1.000038 | 0.049983 | 2.479460 | 0.950051 |
| -0.000005 | 1.000065 | 0.950051 | 0.998054 |
| 1.000356 | 1.998481 | 0.998054 | 0.949444 |

### Difficulty Settings

Applied by the **caller** before `AddReputation`, NOT inside the curve:

| Preset | RepGainMultiplier | RepLossMultiplier |
|--------|------------------|------------------|
| Easy | 2.0 | 0.5 |
| Normal | 1.0 | 1.0 |
| Moderate | 0.9 | 1.5 |
| Hard | 0.6 | 2.0 |

### Other Constants (from GameVariables asset)

- `reputationKerbalDeath = -10.0` (code default was +10, Unity inspector overrides to -10)
- `reputationKerbalRecovery = 0.0` (code default was 25, overridden to 0)

### BUG FOUND: Double Curve Application in Existing Code

`ResourceApplicator.CorrectToBaseline` (line 287) computes `repCorrection = baselineRep - currentRep` and passes it through `AddReputation`, which applies the nonlinear curve. The delta is treated as a nominal value and curved again, producing a different actual change.

**Example:** current=900, target=800, correction=-100. The curve at rep=900 has loss multiplier ~1.7x, so actual change is -170.9. Result: rep=729 instead of 800. **Error: -71 rep.**

**Fix:** Use `Reputation.Instance.SetReputation(baselineRep, TransactionReasons.None)` which directly sets the value.

Same bug affects `TickStandalone` (line 58), `TickTrees` (line 146), `DeductBudget` (line 196) — these pass recorded deltas (already actual game deltas from before/after snapshot) through `AddReputation`, applying the curve a second time.

**Impact:** This is a pre-existing bug in the current system, not introduced by game actions work. Should be fixed independently.

**Status:** Fixed — all 4 callsites in `ResourceApplicator` now use `SetReputation` with pre-computed target values instead of `AddReputation` with deltas.

### Proposed Pure Function

```csharp
internal static (float actualDelta, float newRep) ApplyReputationCurve(
    float nominal, float currentRep, float repRange = 1000f)
{
    int num = (int)Mathf.Abs(nominal);
    float delta = Mathf.Sign(nominal);
    float accumulated = 0f;
    float rep = currentRep;

    for (int i = 0; i <= num; i++)
    {
        float input = (i != num) ? delta : (nominal - accumulated);
        float time = rep / repRange;
        float mult = (input < 0f)
            ? EvaluateSubtractionCurve(time)
            : EvaluateAdditionCurve(time);
        float step = input * mult;
        rep += step;
        accumulated += step;
    }

    return (accumulated, rep);
}
```

Where `EvaluateAdditionCurve`/`EvaluateSubtractionCurve` are cubic Hermite spline evaluations using the keyframes above.

---

## Spike B: Contract State Patching — MEDIUM-HIGH RISK

### What Parsek Already Has

- All 6 contract lifecycle events captured (`onOffered` through `onDeclined`)
- Full ConfigNode snapshots stored at accept time via `Contract.Save()`
- Baseline captures active contracts

### Feasibility

- **Serialization round-trip: RELIABLE** for stock contracts. KSP uses the same mechanism.
- **Reconstruction: REQUIRES type-registry lookup** — cannot just `new Contract()`, must instantiate the correct subclass (`PartTest`, `SurveyContract`, etc.) via KSP's internal type lookup.
- **State manipulation: FEASIBLE** via `Contract.Complete()/Fail()/Cancel()` with suppress flags.
- **Adding to ContractSystem: POSSIBLE** but must respect internal bookkeeping (active count, generation weights).

### Key Risks

1. **Contract Configurator mod** completely replaces the contract pipeline. Snapshots from CC contracts may not load correctly if CC version changes.
2. **Parameter/condition state** (waypoints, vessel references) may be stale after rewind.
3. **Procedural generation state** is not patched — KSP may generate duplicates unless GUID matching prevents re-offering.

### Existing Decision

`ActionReplay` explicitly excluded contracts (documented in `design-committed-action-replay.md`): "Contracts are dynamic and cannot be reliably reproduced."

### Recommended Approach

Hybrid: ConfigNode snapshots at accept time (already implemented) + event-driven state patching. On rewind, walk the ledger, reconstruct contracts from snapshots, set state via Complete/Fail/Cancel with SuppressResourceEvents. Leave procedural generation to KSP.

---

## Spike C: Kerbal Roster Manipulation — LOW RISK

**All operations feasible.** No blockers found.

| Operation | API | Already Used | Risk |
|---|---|---|---|
| Create kerbal | `KerbalRoster.GetNewKerbal()` | 3 callsites | None |
| Set name/trait | `ChangeName()` / `SetExperienceTrait()` | 2 each | None |
| Set XP | `FlightLog.AddEntry()` on `careerLog` | New | Low |
| Retired state | `rosterStatus = Assigned` + Parsek tracking | Same as reservation | None |
| Dismissal prevention | Harmony prefix on `KerbalRoster.Remove()` | Same pattern as TechResearchPatch | Low |
| Custom attributes | `courage`/`stupidity` are public fields | Trivial | None |
| Cap bypass | `GetNewKerbal()` has no cap check | 3 callsites confirm | None |

### Design Note

Stand-in attributes: design doc says "randomized." If deterministic replay is needed (same stand-in on recalculation), store courage/stupidity in the `KerbalStandIn` action. If re-rolling is acceptable, no storage needed.

---

## Spike D: KSC Event Hooks — LOW-MEDIUM RISK

### Coverage Table

| KSC Action | Capture | Block | Replay | Risk |
|---|---|---|---|---|
| Tech unlock | Done | Done (Harmony) | Done | Low |
| Facility upgrade | Done (polling) | Done (Harmony) | Done | Low |
| Part purchase | Done | Missing | Done | Low |
| Kerbal hire | Done | **Missing** | Done | Medium |
| Contract lifecycle | Done (all 6) | **Missing** | **Missing** | Medium |
| Milestones/Progress | **Not implemented** | No | No | Medium |

### Key Details

- All KSP events fire **POST state change** (synchronous, main thread)
- Blocking requires Harmony **prefixes** on the action methods (not the events)
- Three suppression flags exist: `SuppressCrewEvents`, `SuppressResourceEvents`, `IsReplayingActions`
- New work needed: `GameEvents.OnProgressComplete` subscription for milestones, Harmony prefix for kerbal hire blocking

### Risk for Task 18

Low for most items. Medium for kerbal hire blocking (new Harmony patch) and milestone subscription (new territory). The existing infrastructure handles most KSC actions already.

---

## Summary: Risk Map After Spikes

| Area | Pre-Spike Risk | Post-Spike Risk | Reason |
|------|----------------|-----------------|--------|
| Reputation curve | HIGH (unknown formula) | **LOW** (exact keyframes extracted) | D1 resolved |
| Contract patching | HIGH (unknown feasibility) | **MEDIUM** (stock works, CC mod risk remains) | Snapshots work, reconstruction feasible |
| Kerbal manipulation | MEDIUM (unknown API coverage) | **LOW** (all operations confirmed) | Every API proven or already used |
| KSC event hooks | MEDIUM (unknown coverage) | **LOW** (most already handled) | 4 of 6 action types fully implemented |

### Action Items

1. **Fix reputation bug** (`CorrectToBaseline` and `TickStandalone`/`TickTrees`/`DeductBudget`) — use `SetReputation` instead of `AddReputation` for pre-computed deltas. Independent of game actions work.
2. **Update D1 status** in deferred items: RESOLVED (curve extracted).
3. **Update D14 status**: reputation floor is effectively -1000 (loss curve multiplier approaches 0 near floor), no hard clamp in AddReputation but SetReputation clamps.
4. **Proceed with Phase 1** — no spikes produced "kill" results. All risks are manageable.
