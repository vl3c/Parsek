# Game Actions, Ledger Recalc, and Reservation Playtest Checklist

Log-greppable verification for the game-state recorder, ledger recalculation
(on commit / rewind / warp-exit / load), science and funds earning, resource
reservation, and the kerbals reservation system. Use career mode (resource
tracking requires it). Grep target: `Kerbal Space Program/KSP.log`.

Before starting:

- Enable Verbose logging (Settings > Diagnostics), or the per-module recompute
  internals (`ScienceModule`, `Funds`, `Reputation`) will not appear. The
  Info-level confirmations marked "(always logs)" show up regardless.
- The recorder captures events live during flight/KSC. Ledger conversion happens
  on commit. Recalculation plus KSP state patching happens on commit, rewind,
  warp-exit, and load.
- Warp caveat (design doc section 15.3): warp visuals are not yet implemented,
  but the warp-exit recalc trigger IS wired. Verify time-warp-forward by the
  recalc firing on warp exit, not by ghost/facility visuals during warp.

## Combined grep (run first to eyeball everything)

```bash
grep -E "\[Parsek\]\[(INFO|WARN)\]\[(GameStateRecorder|GameStateEventConverter|Ledger|RecalcEngine|LedgerOrchestrator|KspStatePatcher|ScienceModule|Funds|Reputation|Milestones|Contracts|KerbalsModule|CrewReservation|CrewReservations)\]" "Kerbal Space Program/KSP.log"
```

## 1. Game-state recorder captured live events

Do: accept a contract, run an experiment then transmit/recover, complete a
contract, unlock a tech node, hire/assign a kerbal.

Grep: `GameStateRecorder] Game state:`

Expect lines such as `Game state: FundsChanged`, `ScienceChanged`,
`ReputationChanged`, `ContractAccepted`, `ContractCompleted`, `TechResearched`,
`CrewHired`, `MilestoneAchieved`. Each in-game action should appear. Watch for
`Emit drift:` WARNs (event fired with no/stale recording tag).

## 2. Recorder to ledger conversion (on commit)

Do: commit a recording that earned science/funds.

Grep: `GameStateEventConverter] ConvertEvents: converted=` and
`ConvertScienceSubjects: converted=`

Expect `converted=N` matching the events that recording produced. Then
`[Ledger] AddActions batch: added=` and `Added action: type=` confirm they
landed in the ledger.

## 3. Recalculation engine fired

Grep: `RecalcEngine] Recalculate complete: actionsTotal=` (always logs)

Expect one summary line per recalc with `actionsAfterCutoff`, `cutoffUT`,
`filteredOut`, `walkedSorted`. Cross-check the trigger reason from the
orchestrator:

- After commit: full recalc, no cutoff.
- After rewind: `LedgerOrchestrator] Current-UT ledger recalculation: reason=post-rewind-load cutoffUT=...`
- After warp-exit / time-jump: `...reason=time-jump cutoffUT=...`
- Live KSC event (Verbose): `Live-event recalc decision: reason=... hasFutureLedgerActions=...`

## 4. Science from experiments (earn + cap + patch)

Do: run an experiment, recover or transmit, commit.

Grep (Verbose): `ScienceModule` lines (hard-cap walk: effectiveScience, headroom).
Grep (always logs): `KspStatePatcher] PatchScience:`, e.g.
`PatchScience: 0.0 -> 8.0 (delta=8.0, target=8.0)`.

Expect `target` equals the science the ledger derived, and the post-value
matches the in-game R&D total. A `PatchScience: suspicious drawdown` WARN means
a recompute tried to subtract more than was present (investigate). Per-subject
diminishing returns: `PatchPerSubjectScience: patched=N, cleared=M`.

## 5. Money from events

Do: complete a contract or recover a vessel for funds.

Grep: `GameStateRecorder] Game state: FundsChanged` (capture), then `[Funds]`
recompute (Verbose), then `KspStatePatcher] PatchFunds: X -> Y (delta=, target=)`
(always logs).

Expect `target` matches in-game funds. For a vessel recovery, check
`LedgerOrchestrator` recovery-funds pairing lines.

## 6. Reputation, milestones, contracts (bonus coverage)

Grep (always logs): `PatchReputation: X -> Y (target=)`,
`PatchMilestones: credited=..., unreached=...`,
`PatchContracts: removedStale=..., removedFinishedTombstoned=...`.
`Milestones] Credited milestone` confirms a first-time achievement
(first launch, first orbit, etc.) flowed through.

## 7. Resource reservation on rewind (key reservation test)

Reservation only visibly bites when there are committed future spendings after
the current UT. To exercise it:

Do: earn science, unlock a tech node (spend), then rewind to a point before that
unlock (or warp-jump back).

Grep: `PatchScience:` / `PatchFunds:` after the rewind. The `target` should be
the available (reservation-clamped) value, lower than the raw running balance,
because the future tech unlock reserves headroom (design sections 12.5 / 15.6).

Expect available science/funds patched below the raw balance, and the in-game
R&D/funds total reflecting what is spendable right now. If there are no future
spendings, available equals current (reservation is a correct no-op; note that
in results).

## 8. Kerbals reservation system

Do: fly and commit a recording with crew aboard, then rewind / re-fly so the
same kerbals are in use by a ghost while flying again.

Grep:

- `CrewReservations] Recomputed {reason}: N reservations remain` (always logs):
  fires on every recalc, confirms the reservation set is recomputed on
  rewind/warp/load.
- `CrewReservation] SwapReservedCrewInFlight` and
  `Orphan placement: 'X' -> 'Y' placed in part ...`: stand-in substitution when
  a reserved kerbal is busy.
- `KerbalsModule] PopulateCrewEndStates: ...` and
  `PopulateCrewEndStates(batch): processed=... populated=... skipped=...`:
  end-state inference per recording.
- `KerbalsModule] Recreated stand-in 'X' (trait) ...`: stand-in roster
  recreation after load.
- `Crew] Rescued N orphaned crew member(s)`: crew freed back to Available.

Expect the reservation count to change consistently as you rewind (kerbals a
superseded branch used get freed), no kerbal both flying live and locked by a
ghost, and stand-ins filling seats for busy originals.

## 9. KSP state actually patched back (the writes)

All `KspStatePatcher] Patch*:` lines prove the recalculated ledger truth was
written into KSP. After rewind/warp-exit, expect a batch together: `PatchScience`,
`PatchFunds`, `PatchReputation`, `PatchMilestones`, `PatchContracts`,
`PatchTechTree: available=..., madeAvailable=...`. Any `null ... skipping` WARN
means a module was not wired for that patch.

## Suggested in-game playtest order (hits every check)

1. New career, accept 2 contracts. Check section 1.
2. Launch, run an experiment, recover, commit. Check sections 2, 3, 4.
3. Complete a contract for funds. Check sections 5, 6.
4. Unlock a tech node.
5. Rewind to before the launch (or warp-jump). Check section 3 (rewind reason),
   7 (reservation clamp), 8 (crew recompute), 9 (patch batch).
6. Time-warp forward past a committed event. Check section 3 (time-jump reason)
   and 9.
