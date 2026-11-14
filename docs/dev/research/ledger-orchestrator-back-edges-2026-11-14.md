# LedgerOrchestrator back-edges (2026-11-14)

Design note for refactor item B4, part 2. Question: which types in the semantic residue
call back into `LedgerOrchestrator`, what each needs from it, and which inversions
actually lower the residue ratchet. Measured on the B4 branch tip (module set landed),
Debug `Parsek.dll`.

## How the list was produced

The residue is `semantic.measure(g, mods, SEMANTIC_CUT_ORDER)` over the forwarder-resolved
IL graph, the same measure `scripts/arch/test_semantic.py` ratchets. A back-edge is an
edge `X -> LedgerOrchestrator` with `X` in that residue. No repository command prints that
set directly, so it was listed with this snippet over the `scripts/arch` modules (run from
the repo root after a Debug build):

```bash
python3 - Source/Parsek/bin/Debug/Parsek.dll <<'EOF'
import sys; sys.path.insert(0, "scripts/arch")
import archview, ilgraph, semantic
rules, tooling, _f, _a = archview.load_rules(str(archview.DEFAULT_MODULES))
model = archview.build_model(archview.DEFAULT_SOURCE, rules, tooling, measure_sizes=False)
names = {t["name"] for t in model["types"]}; mods = {t["name"]: t["module"] for t in model["types"]}
g = ilgraph.type_graph_from_il(ilgraph.parse_il(ilgraph.load_il(sys.argv[1])), names,
                               resolve_forwarders=True)
m = semantic.measure(g, mods, semantic.SEMANTIC_CUT_ORDER)
back = sorted(s for s in m["residue"] if "LedgerOrchestrator" in g.get(s, ()))
print(m["knot"], m["greedy"], m["replayed"], semantic.ratchet_residue(m), len(back))
for s in back: print(" ", mods[s], s)
EOF
```

Output: knot 391, greedy 140, replayed 144, ratchet 140 (gate 141), 30 back-edges. The
members each source uses come from the same parse (`parsed["uses"]`, forwarders resolved
through `ilgraph.forwarder_table`). Every residue figure below is this measure on a copy
of the graph with the edge removed AND the edges the moved code brings to its new home
added (a moved method's IL closure inside `LedgerOrchestrator`, followed call by call).

B4 itself adds one back-edge: `ClickReservationGate` reads `LedgerOrchestrator.Modules`
to pass the production set to its probe (29 -> 30). It does not move the measure
(391 / 140 / 144 before and after); `SupersedeCommit` and `ReFlyMergeDominance` read the
set too but already had the edge.

## The 30 back-edges, by what the caller needs

### Q. A query: an answer computed from data `LedgerOrchestrator` does not own

| Source (module) | Members used |
|---|---|
| Ledger (GameActions) | FindRecordingById |
| CommittedFutureIndexCache (GameActions) | FindRecordingById |
| CareerSlotSummary (UI) | GetContractSlots, GetStrategySlots |
| ContractSlotReservation (GameActions) | GetContractSlots |
| StrategyReservationPredicates (GameActions) | GetStrategySlots |
| ConversionLegBinding (GameActions) | IsStrategyConversionRow |
| CommittedMilestoneReach (GameActions) | BuildCommittedScienceSubjectCredits |
| KspStatePatcher (GameActions) | IsCurrentUtReadyForCutoff and six patch inputs (contract reinstate cutoffs, tombstoned contract guids, ledger action ids, three pending science credits / debits) |
| PostWalkActionReconciler (GameActions) | FindRecordingById, FormatFixed1 / 3, five science-event helpers, GetResourceTrackingAvailability, plus its log-once state (see S) |

The closures are small and touch no orchestrator state: `FindRecordingById` reads
`RecordingStore`, the slot tables are a pure level switch, `IsStrategyConversionRow` reads
the row. Exceptions: `BuildTreeCommitRowsPreview` (used by `TreeCommitService` and
`ReFlyMergeDominance`) reaches 37 orchestrator methods and `ParsekScenario`,
`GameStateRecorder`, `RewindPoint` and more; it is a query only by signature.

### N. A notification: the caller tells the ledger something happened

| Source | Members used |
|---|---|
| RecordingTreeSplitter (Rewind) | OnTimelineDataChanged (raises) |
| KerbalsWindowUI, TimelineWindowUI (UI) | OnTimelineDataChanged (subscribe) |
| SwitchSegmentAttempts (SwitchIntent) | PreserveIrreversibleLiveGameplayOnDiscard |
| TreeDiscardService (Recording) | the same, plus RemovePendingScienceSubjectsForRecordings |
| CommittedTreeRestoreAttempt (Recording) | PreserveIrreversibleRestoreAttemptTailsOnDiscard |
| TreeCommitService (Recording) | BuildTreeCommitRowsPreview, RemovePendingScienceSubjectsForRecordings |
| MergeDialog (Rewind) | NotifyLedgerTreeCommitted (and a trigger, R) |
| LiveOnlyIncome (GameActions) | OnLiveOnlyIncome |

The two `PreserveIrreversible*` bodies reach 17 and 22 orchestrator methods and write the
module state: they are the ledger's own work and must stay in it.

### R. A recalculation trigger

| Source | Member used |
|---|---|
| RewindInvoker | RecalculateAndPatch |
| CareerLedgerClockCursor | RecalculateAndPatchForClockAdvance (and TryGetNextActionUTAfter, Q) |
| MergeDialog, MergeJournalOrchestrator | RecalculateAndPatchForCurrentTimelineIfFutureActions |
| ParsekKSC, ParsekTrackingStation | RecalculateIfKerbalReservationReleaseDue |
| TimeJumpManager | RecalculateAndPatchForTimeJump |
| SupersedeCommit | RecalculateAndPatchAfterTombstones, TryEnsureKerbalsModuleForTombstoneRosterCleanup |

### S. State another type should own

| Source | Members used |
|---|---|
| CrewReservationManager, TimelineBuilder (and KerbalsModule, KerbalsWindowUI, SupersedeCommit) | get_Kerbals: the live kerbals module instance |
| ClickReservationGate, ReFlyMergeDominance, SupersedeCommit | get_Modules: the production module set (B4) |
| ReFlyMergeDominance | Initialize (to make sure the set exists) |
| KerbalsModule | NeedsCrewEndStatePopulation (a crew question kept on the orchestrator) |
| PostWalkActionReconciler | LogReconcileSkippedOnce / WarnOnce, TryRegisterPostWalkDumpKey: the reconciler's own log-once sets |
| TreeCommitService, TreeDiscardService | RemovePendingScienceSubjectsForRecordings: `GameStateRecorder`'s pending list |

Eight sources need only Q members, five only N, four only S, five only R; eight mix two
groups (`PostWalkActionReconciler`, `KerbalsWindowUI`, `TreeCommitService`,
`TreeDiscardService`, `MergeDialog`, `CareerLedgerClockCursor`, `ReFlyMergeDominance`,
`SupersedeCommit`).

## Cheapest inversion per group

- **Q: move the answer to the data.** `FindRecordingById` to `RecordingStore`; the slot
  tables to a leaf `CareerSlotTable`; `IsStrategyConversionRow` to `ConversionLegBinding`;
  the science credit builder to `CommittedMilestoneReach`; `TryGetNextActionUTAfter` to
  `CareerLedgerClockCursor`; the reconcile helpers to `PostWalkActionReconciler`; the
  patcher inputs computed by the orchestrator and passed to `KspStatePatcher` (it already
  calls the patcher). Do NOT give `BuildTreeCommitRowsPreview` a new home type: measured,
  the new type joins the knot (391 -> 392) and the ratchet rises 140 -> 145. Its callers
  take it as a delegate parameter (below) or keep the edge.
- **N: pass the ledger's handler in, or move the signal to a leaf.** The timeline event
  moves to a leaf `LedgerTimelineSignal` that subscribers, the splitter and the
  orchestrator all name (no install order: subscribers attach to the leaf directly). The
  discard preservations become a delegate parameter of
  `SwitchSegmentAttempts.TryDiscardActiveSwitchSegmentAttempt` and
  `CommittedTreeRestoreAttempt.PurgeCommittedTreeRestoreAttemptEventTails*`, supplied by
  their callers (`MergeDialog`, `TreeDiscardService` already reference the orchestrator;
  `ParsekFlightSwitchContinuation` and `MapFocusObjectOnSelectPatch` gain the edge, which
  measured costs nothing: 126 either way). A delegate installed once in a static holder
  measures the same but adds an install-order risk (a discard before installation would
  skip the preservation silently); the parameter form has none.
- **R: the same delegate holder or parameter.** Recalculation entry points stay on the
  orchestrator; callers reach them through an installed `Action`. Timing is unchanged
  (synchronous), but every trigger site now depends on installation having happened.
- **S: put the instance where it lives.** The live kerbals module as
  `KerbalsModule.Current` (set by the orchestrator, reset on the same save boundary), the
  module set on a leaf holder typed only by `RecalculationModuleSet`, the log-once sets in
  the reconciler, the pending-subject removal on `GameStateRecorder`. Measured trap: a
  holder that owns the eight modules (typed `KerbalsModule` among them) closes a
  `KerbalsModule <-> holder` cycle, joins the knot and raises the ratchet to 145.

## Residue per step

Cumulative, with replacement edges (greedy / replayed / ratchet; knot stays 391):

| Order | After step | Edges gone | Ratchet |
|---|---|---|---|
| Q, S, N, R | Q | 9 / 30 | 140 (greedy 140, replayed 144) |
| | + S | 13 | 140 |
| | + N | 22 | 124 |
| | + R | 30 | 121 |
| N, Q, S, R | N | 5 | 135 (greedy 135, replayed 144) |
| | + Q | 16 | 124 |
| | + S | 22 | 124 |
| | + R | 30 | 121 |

Each group alone: Q 140, S 140, N 135, R 140. So Q and S really gain nothing on their own,
and S gains nothing even after N and Q; R buys the last 3.

The edges are not equal. From the full cut (121), putting one source edge back costs 3
(124) for 26 of them and 9 to 19 for four: `Ledger` 130, `RecordingTreeSplitter` 135,
`CommittedTreeRestoreAttempt` 140, `SwitchSegmentAttempts` 140. Cutting just those, with
their replacement edges:

| Cut (cumulative) | Ratchet |
|---|---|
| Ledger (FindRecordingById to RecordingStore) | 140 |
| + RecordingTreeSplitter (timeline signal to a leaf) | 140 |
| + CommittedTreeRestoreAttempt (restore-tail preservation as a parameter) | 140 |
| + SwitchSegmentAttempts (live-gameplay preservation as a parameter) | 126 |
| + ConversionLegBinding (IsStrategyConversionRow moved) | 125 |
| + PostWalkActionReconciler (helpers and log-once state moved) | 124 |

The first three steps gain nothing; the four only pay together (the two discard edges
alone read 136). Four edges recover 14 of the 19 available; six recover 16. The other 24
edges buy 3.

## What can be verified headless, and what needs flights

- **Q and S moves**: pure relocations. Headless: the full xUnit suite, the B4 walk digest
  (`LedgerStateFuzzerTests.ModuleSet_WalkDigestOverTheFuzzCorpus_IsPinned`) as the
  differential witness that no ledger result moved, `il_compare.py` on the moved bodies,
  the static-lifetime gates for any static that changes owner (`KerbalsModule.Current`
  must be tagged and reset like the orchestrator field it replaces). No flight.
- **N as a parameter, the timeline signal as a leaf**: headless. Subscription and raise
  order are unchanged, the discard path receives the same method. The existing discard
  tests (`SwitchSegment*`, `CommittedTreeRestoreAttempt*`, `TreeDiscardService` cells and
  the in-game `DiscardEconomyPreservationInGameTest`) cover the call; a mutant that drops
  the parameter must red one of them before landing. No flight.
- **N and R through an installed delegate**: the logic is headless, but "installed before
  the first discard / commit / time jump / KSC update in every scene and load kind" is a
  runtime ordering claim. It needs an in-game assertion per scene plus the merge, discard,
  rewind and time-jump harness lanes to fly green with a log line proving the hook ran.
  This is the only part that needs flights.

## Recommendation

Do part of it: the six-edge cut (four load-bearing edges plus the two cheap pure moves),
ratchet 140 -> 124, in one PR, because any three of the four alone gain nothing. Every
step there is a relocation or a delegate parameter, so it is verifiable headless with the
B4 digest as the witness that ledger results did not move; no installed hooks, no flights.
Leave the remaining 24 edges: they buy 3 more (124 -> 121) at the cost of putting ten
recalculation and notification entry points behind installed delegates whose install
order needs runtime proof in every scene.
