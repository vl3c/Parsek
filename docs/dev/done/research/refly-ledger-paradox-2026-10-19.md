# Re-Fly ledger paradox: analysis and options (2026-10-19)

**Outcome.** The design that answers this analysis (Option 1 plus the companion fixes and the owner's
switching ruling) is `docs/parsek-rewind-to-separation-design.md` section 2.12. This file is the
evidence behind it and is not updated as the design moves.

Read-only analysis of main at 4503a53. No code was changed. Every claim below is marked
CONFIRMED (read in code, tests or a flown log) or INFERRED (follows from the code, not traced
end to end). File:line references are to that commit.

## 1. The rules every option is checked against

- V, 2026-10-19: never change behavior in a way that moves the ledger toward negative value;
  never introduce ANY time travel paradox. Re-Fly exists so a player can fly both stages of one
  mission (upper stage to orbit, booster to a landing). It is the only place Parsek rewrites
  past events.
- Roadmap lines 7-9: causality (UT order) and additivity ("committed recordings and game
  actions can be added but never deleted"); "no state reversal, and no paradoxes".
- Game actions design 1.4 / 3.10, Layer 1: "Total earnable resources never decrease. Existing
  spendings that were valid when committed cannot become invalid." 2.2 (5): "Prevention, not
  detection." 2.2 (8): "Conservative over precise." 2.3 (10): "no unresolvable deficits."
  12.6 / 15.8: an `Affordable=false` spending "indicates a bug or data corruption".
- Flight recorder design 2.2 (10): "No deletion, no paradox." Ghosts are the only reliable
  physical paradox prevention.
- Coop design principle 6: "Paradox avoidance over fairness ... never moves anything backward
  in time"; destructive restructuring is loser-side only, on the actor's own data.
- Project principle: one invariant beats a feature checklist.

## 2. What happens today (CONFIRMED)

1. **Merge retires the replaced flight's value.** `SupersedeCommit.CommitTombstones`
   (SupersedeCommit.cs ~2484-2800) tombstones every row of the superseded subtree after the
   rewind point that `TombstoneEligibility.IsSupersedeTombstoneEligible`
   (TombstoneEligibility.cs:59-124) accepts: science, lab science, funds and reputation
   earnings, milestones, contract accept / complete / fail, recoveries, rescues, XP, scans,
   facility rows, strategy rows and in-flight spends. Kept: seeds, rollout cost, untagged KSC
   rows, rows before the rewind point.
2. **Nothing checks later spending.** No affordability or minimum-balance read exists in
   SupersedeCommit, MergeDialog*, MergeJournalOrchestrator or RewindInvoker. The recalc runs
   as an "authoritative reduction" (LedgerOrchestrator.cs:3437), which bypasses the drawdown
   guard on purpose.
3. **When a later committed spend is left unpaid:**
   - Funds: the cost is charged anyway and the running balance goes negative
     (FundsModule.cs:338-366, 582-633). The facility stays upgraded, the kerbal hired, the
     strategy active. The bar is patched to 0 (KspStatePatcher.cs:1823-1854) and the tooltip
     shows "Short by" / "Balance: <negative>" (CurrencyReservationOverlay.cs:178-256).
   - Science: the tech spend is refused (ScienceModule.cs:314-337, Warn "possible bug or data
     corruption") and a node that was still in the future at the rewind is never unlocked
     (KspStatePatcher.cs:540, 1097). Committed research silently disappears.
   - Reputation: has no reservation at all (ReputationModule.cs:24).
4. **Retry gate.** Only a credited `ScienceEarning` blocks a crashed slot from being re-flown
   (`IsRetryBlockingRecordingAction`, SupersedeCommit.cs:2199-2245). Milestones, contract
   completions and failures, funds, reputation, lab science and rescues do not.
5. **The design's Layer 1 promise is broken by the 2026-05-06 broadening.** The original Re-Fly
   (v0.9, `3578d6ca`) tombstoned only kerbal deaths and their penalty. Commit `f4763de1` and
   review `d66626ba` broadened it because the narrow scope let the old branch double-pay
   contracts, pre-consume science caps, leak facility spends, keep orphaned contracts and keep
   old crew reserved. The broadening fixed double credit and opened the deficit hole. Narrowing
   it back would bring the double credit back, so neither scope is safe on its own.

## 3. Every case found

### 3.1 Re-Fly merge

| # | Case | Status |
|---|---|---|
| R1 | Replaced flight's funds (contract reward, recovery, milestone) paid a later KSC upgrade / hire / strategy. Merge retires them; the later spend stays and funds go negative. Example: +100k at UT 1500, upgrade -110k at UT 2000 from 20k, re-fly crashes, merge: balance -90k at UT 2000. | CONFIRMED |
| R2 | Replaced flight's lab or strategy science paid a later tech node. Merge retires it; the node is never unlocked if it was still in the future. Lab science does not block retry. | CONFIRMED |
| R3 | First-time milestone: during the session stock already counts it done (the invoke walks the whole ledger, RewindInvoker.cs:1445), so the new flight cannot earn it; merge retires the original. The reward is lost; if stock re-fires it later it is re-earned at a later UT, so the balance dips in between. LAB-10 log: `set achieved 'Kerbin/Science'` then `cleared achieved`. | CONFIRMED |
| R4 | Once-ever credit moves LATER: a later committed duplicate milestone, contract completion or capped science row becomes the effective one, so spends between the two UTs lose their funding. | mechanics CONFIRMED, reachable setup INFERRED |
| R5 | Contract outcomes re-open: a retired completion or fail puts the contract back to Active from a snapshot. Mission Control's slot cap is never enforced by the ledger, so a later accept that used the slot over-subscribes it. A re-opened rescue contract whose kerbal is already home can never complete. | CONFIRMED / rescue INFERRED |
| R6 | Exploit, contract failure erased: during the session the failed contract is already terminal in stock, so a re-fly that fails it the same way cannot fail it again; merge retires the original fail and its penalty. A contract with no deadline keeps the penalty erased for good. | asymmetry CONFIRMED, outcome INFERRED |
| R7 | Exploit, bank then retire: a flight earns on a slot whose value does not block retry (funds, milestone, contract); in normal play the player later spends it at the Space Center; later still the player rewinds and re-flies that slot, and the merge removes the earning while the purchase stays. Nothing is spent during the re-fly itself (spending is only possible at the Space Center). | CONFIRMED (follows from R1 and the retry gate) |
| R8 | A kerbal rescued or created by the replaced flight and flown in a later committed recording stays on the roster with no ledger origin. | CONFIRMED |
| R9 | A re-fly kills a kerbal a later committed recording flies (the live re-fly crew is exempt from reservation, KerbalsModule.cs:2598-2624). Existing gates (recovered / docked / boarded terminal, crew joined a foreign vessel) close most routes there. | INFERRED |

### 3.2 Re-Fly start and session

| # | Case | Status |
|---|---|---|
| S1 | Re-Fly START retires the recovery rewards of any vessel the rewind point brings back that the player recovered later (`RetireResurrectedVesselRecoveryRows`, RewindInvoker.cs:1357-1384, 1535). It runs before the marker exists, bypasses the guard, and stays retired after Discard. Example: 20k, vessel V recovered at UT 1500 for +50k (70k), facility -65k at UT 1800 (5k); a Re-Fly of another split at UT 1000 brings V back and retires the 50k, so UT 1800 reads -45k, even if the player discards. | CONFIRMED |
| S2 | During EVERY Re-Fly session the invoke walk passes `double.MaxValue` as "now" to `ContractsModule.PrePass` (ContractsModule.cs:310), so every active contract with a deadline is treated as expired: a synthetic fail is injected, its penalty is charged in the session pools, and the contract leaves stock's active list until the merge walk restores it. | code path CONFIRMED, live effect INFERRED (needs a red-first unit cell) |
| S3 | During the session the pools include the replaced flight's earnings, which the merge will take away (open todo REFLY-SESSION-CAREER-SHOWS-THE-REPLACED-FLIGHTS-FUTURE). The player sees money that will vanish. | CONFIRMED |

### 3.3 Outside Re-Fly (same invariant, different paths)

| # | Case | Status |
|---|---|---|
| O1 | Quickload into a quicksave taken before a later-committed flight retires that flight's rows but keeps untagged KSC spends made after it (design QL-R2, ParsekScenario.Trim.cs:1211, LoadReconcilePolicy.cs:751). Example: F5 at UT 100, +100k contract at 250, upgrade -120k at 400 from 30k, quickload: -90k. | CONFIRMED |
| O2 | Stock revert to launch after a plain rewind: `Ledger.PruneOrphanActionsAfterUT` (Ledger.cs:321, ParsekScenario.cs:4316) removes every untagged row after the launch UT, including committed-future credits (re-homed discard rows), while later tagged spends (another flight's rollout) survive. | CONFIRMED (code comments call it "accepted exposure") |
| O3 | A recording that fails to load (schema reject, sidecar loss) has its earnings pruned by `Ledger.Reconcile` (Ledger.cs:1005) while later spends survive. | CONFIRMED |
| O4 | Route recovery credits after a rewind are retired and come back later or never (Ledger.cs:436, ReconciliationBundle.cs:302-320). | CONFIRMED |
| O5 | The resumed-tree tombstone rule (InSessionStagedStateHandoff.cs:551) can re-retire credits or un-retire debits. | CONFIRMED path, cases INFERRED |
| O6 | Any flight after a rewind can insert a loss earlier than a committed spend, and nothing checks it: contract fail penalty, a deadline fail of an accepted contract, contract cancel / decline penalties, kerbal death reputation, strategy converter debits, a destruction that makes a later committed repair chargeable. An early in-flight contract fail also zeroes a later committed completion (the X1 cascade the 2026-09-25 stock-UI ruling D7 blocked only for the KSC Cancel button). | CONFIRMED |
| O7 | The funds affordability check `CanAffordFundsSpending` has no production caller; funds rely only on stock's check against the patched counter, and the "keep what you earned" floor can hold that counter above the reservation. | CONFIRMED |

## 4. The core problem in one line

The ledger has one safety property, "the committed timeline pays for every committed spend at
every UT", and Re-Fly is the only operation that removes committed credit on purpose. The old
narrow scope kept the property but double-counted; the broad scope fixed the double count and
broke the property. The fix has to make the two flights never both count AND never take a
credit back.

## 5. Options for Re-Fly

### Option 1: Re-fly only flights with nothing to give back (recommended)

A slot can be re-flown only when its flight, from the split on, holds no row the game cannot
give back the same way. One predicate, evaluated where the retry gate already runs
(`UnfinishedFlightClassifier`, Destroyed / focus / Stash arms) and again at invoke:

- **Seals the slot (value or outcome):** science and lab science, funds earnings, reputation
  earnings, milestones and records, contract accept / complete / fail / cancel, kerbal rescue,
  resource scans, strategy credits, any in-flight spend. This widens today's
  `ScienceEarning`-only gate to the strict predicate that already exists and is tested
  (`IsWorldStateChangingRecordingAction`, used today in strict mode only).
- **May be retired at merge (losses the new flight records again on its own if it repeats
  them):** kerbal deaths and their reputation penalty, vessel-destruction reputation, building
  damage. Each is captured from the flight's own events, not gated by stock state, so the
  replacement re-incurs it if it repeats it.

What it guarantees: a merge can only retire losses, so value only rises at merge. No later
spend can lose funding (R1, R2, R7 closed), no credit moves later (R4), no milestone is lost
(R3: a flight with a milestone is sealed), no contract re-opens or loses its failure (R5, R6),
no orphaned rescued kerbal (R8), and no double credit (there is nothing on the old side to
double). The session pools no longer show money that will vanish (S3), because the old side
holds no credits.

Purpose kept: the booster that crashed in the background earned nothing, so it stays
re-flyable, and landing it adds its recovery value as a new earning.

Cost: a crashed flight that earned a milestone, a contract, lab science, funds or reputation
can no longer be re-flown (today only science stops it). The existing Rewind button's own
disabled state and tooltip carry the reason (stock-control annotation rule, no new UI).

Coop: fits principle 6 and the ownership fence; an importer never receives a retirement that
lowers a peer's balance.

Save migration: none. The predicate reads existing rows. Slots already re-flyable in old saves
whose flight holds value become sealed on load (forward-only, design 2.10 precedent).

### Option 2: Re-fly when the timeline still pays (the PC session's proposal)

Keep the broad retirement. Offer the Re-Fly only when retiring the flight's later value leaves
every later spend paid; at merge, recompute with the new flight and refuse the merge (Discard or
keep flying) if anything later goes unpaid.

- Closes R1, R2, R4 for currency.
- Still takes credits back at merge (pools drop when nothing was spent yet), which V's rule
  forbids.
- Leaves R3, R5, R6, R8 open unless each gets its own fix.
- Detection, not prevention: a player can fly the whole re-fly and then be refused.
- The answer depends on later state, so it can change after the merge (a coop peer's later
  spend, a later commit), and the reservation layer would have to carry the check forward.

### Option 3: Never retire, only add

Keep every credit of the replaced flight; the new flight only adds. No deficit is possible,
but the two flights double count (the reason the scope was broadened in May): uncapped funds,
reputation and lab science pay twice, once-ever credit stays with the dead branch. It opens
farming. Not recommended.

### Option 4: Re-fly only before anything later is committed

Close every Re-Fly slot as soon as anything else commits after the split UT (a KSC spend,
another flight). No later consumer can exist, so retirement cannot underfund anything. It
still takes credits back at merge, leaves R3 / R5 / R6, and the closing rule is invisible to
the player. Not recommended.

## 6. Companion fixes needed with Option 1

- **S1:** a Re-Fly must not bring back a vessel the player recovered later. Strip it at load
  (its committed recording plays it as a ghost until the recovery) and retire nothing. This
  keeps the recovery, removes the double-recovery exploit the retirement was guarding, and
  matches "ghosts are the only paradox prevention".
- **S2:** pass the session's real UT, not `double.MaxValue`, as the deadline "now" in the
  invoke walk (or keep MaxValue for the walk horizon only). Red-first unit cell first.
- **R9:** verify with a unit cell that no route lets a re-fly kill a kerbal a later committed
  recording flies; if one exists, the crew of such a slot is reserved like any other.

### 6.1 Switching to another vessel during a session (traced 2026-10-19, answering V)

- Nothing blocks a switch during a session; ghosts and vessels claimed by a committed future
  (ghost chain) cannot be flown (ParsekFlight.cs:3710, :13011). CONFIRMED.
- Awards are tagged to the LIVE active recording (ParsekFlight.cs:246, GameStateRecorder.cs:147),
  and an empty tag falls back to the provisional (`ReFlyAwardTagging.Resolve`,
  `unbound-empty-tag`). So the re-flown vessel's background gains are filed under the other
  vessel (missed by the seal scan, SupersedeCommit.cs:1959), and the other vessel's gains
  before its recorder starts are filed under the fork (wrongly sealing). CONFIRMED.
- "The attempt" differs by switch path: '[' / ']' attaches the outsider under a `Launch`
  branch point (attempt-owned, dropped on Discard); map Switch-To creates a parentless segment
  (not attempt-owned; its events are left behind). `CreatingSessionId` is stamped only on the
  provisional (RewindInvoker.cs:2690); design 6.11's claim that session-born children carry it
  is doc drift. CONFIRMED.
- Farming loop: the outsider's gains survive the merge but do not seal the slot, so a second
  re-fly can switch over and earn them again. INFERRED from the above.
- Map Switch-To to an unloaded vessel opens the pre-switch Merge / Discard dialog, which takes
  no Re-Fly input (MapFocusObjectOnSelectPatch.cs:1168-1199) and runs `CommitTreeFlight` on the
  live Re-Fly tree: no supersede rows, no tombstones, marker left set. CONFIRMED path, end state
  INFERRED.
- Correction to section 5 / the thread: ledger rows already filed for attempt recordings are
  kept untagged on Discard, with their career effect (MergeDialog.ReFlyDiscard.cs:627-640).
  Option 1 must make Discard drop every row the attempt filed.
- RULED by V 2026-10-19: block switching. During a re-fly the player can fly only the re-flown
  vessel and its own crew on EVA. File each award under the vessel that caused it.
- General gap, not Re-Fly specific: a vessel whose committed future is only a root-lineage or
  switch-continuation flight with no claiming branch point stays real after a rewind and can be
  changed before its committed future. INFERRED.

## 7. The outside-Re-Fly paths (separate decisions, to file now)

O1-O7 break the same property without Re-Fly. They need their own fix, and the same invariant
answers all of them: every operation keeps the committed timeline paying for every committed
spend at every UT, and an operation that cannot is refused or reshaped at its source.
Candidate shapes, for later:

- O1 / O2: a quickload or revert that abandons a funding flight abandons the KSC spends after it
  too, or is refused while those spends stand.
- O3: a recording that fails to load keeps its ledger rows (the rows are the record; the
  sidecar loss is a playback problem, not a ledger event).
- O6: reserve each accepted contract's failure penalty from accept to resolution (Layer 2
  extension), block the in-flight X1 cascade the way D7 blocked the KSC Cancel, and add a
  reputation reservation for committed strategy costs.
- O7: wire the funds affordability check, and stop the "keep what you earned" floor from
  holding the counter above the reservation.

## 8. Proof plan for Option 1

- xUnit, red first on main: R1 (upgrade paid by the replaced flight goes negative), R2 (tech
  never unlocked), R3 (milestone lost), R6 (contract failure erased), S1 (recovery retired at
  start and kept after Discard), S2 (deadline contracts failed in session).
- Green after the fix: each of those slots is sealed with a `recordingAction:<type>` reason;
  a slot with only deaths / destruction stays re-flyable and its merge only raises value; a
  property cell walks random ledgers and asserts no merge lowers any balance at any UT.
- Harness lane: the booster case (upper stage to orbit, booster BG-crashes, re-fly the booster
  to a landing, merge) stays green and earns the recovery; a lane where the replaced flight
  earned a contract reward later spent on a facility upgrade proves the slot is sealed.

## 9. Open questions for V

1. Option 1, 2, 3 or 4 for Re-Fly (recommended: 1).
2. Whether a crew death on the replaced flight should stay retirable (recommended: yes; it is
   the reason to re-fly a crashed crewed booster, and the replacement re-records any death).
3. The outside-Re-Fly paths O1-O7 get their own thread once Re-Fly is settled.
