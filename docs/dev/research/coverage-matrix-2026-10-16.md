# Parsek coverage matrix: what is proven, what is not (2026-10-16)

Read-only audit of `main` at 412d48e (after PR #2121). Scope: everything except co-op multiplayer,
refactors and Gloops. Sources: `docs/dev/autotest-status.md` (the single authority on which lanes
are live-proven), `docs/dev/autotest-roadmap.md`, `docs/dev/todo-and-known-bugs.md`, the 390 specs
in `harness/scenarios/`, the xUnit and in-game test sources, and the mod source. Builds on
`docs/dev/research/feature-op-coverage-matrix-2026-10-06.md`. The work it calls for is registered in
`docs/dev/autotest-roadmap.md` ("The cross-system gap program (2026-10-16)") and tracked by todo
CROSS-SYSTEM-COVERAGE-GAPS-2026-10-16.

## How to read this

| Mark | Meaning |
|---|---|
| **P** | Proven in flight: a harness lane flown green on V's PC (lane named) |
| **IG** | An in-game test cell, executed inside a flight by a pinned lane (synthetic setup, real KSP) |
| **X** | xUnit only (headless, never in KSP) |
| **U** | Untested at any layer |
| **?** | Unknown: the answer needs a decompile or a flight |
| **(code read)** | Inferred by reading code, not proven by any test |

One caveat applies to nearly every P below: lanes assert Parsek's own log lines and recording
counts. **No lane independently reads a tank, a crew roster or a ghost's mesh.** A writer that
logs "wrote 200" and then loses the write at the next save reads green (this happened: RVR8, see
`docs/dev/done/todo-and-known-bugs-v9.md:2549`).

---

## 1. The grid: system x timeline operation

Columns: Rewind (to launch) / Re-Fly / F9 quickload / stock revert / warp and time jumps / ghost
playback / scene change and save-load.

| System | Rewind | Re-Fly | F9 | Revert | Warp / jump | Ghost playback | Scene / save |
|---|---|---|---|---|---|---|---|
| Recording core | P RR-1, VN-7 | P RF-1, RF-9 | P QL-4/4b/4c, QL-6 | P VN-5 (only revert lane) | P PWR-1..3, CA-1 | n/a | P S0.7, CL-2 |
| Staging / decouple | P GS-9/10/11 | P S4.1, RF-1 | P QL-4 | IG H64 | P PWR-2 | P GS-1..3 | P RF-1 |
| Dock / undock | **U** | P CI-2, CI-4 | P CI-3 (reload at the save instant only) | U | P BDOCK-1 | P CI-4, CI-9 | P SD-1, ST-3 |
| EVA / board (same vessel) | P EVA-6/7/9/10/11, RC-6 | P RF-18/19/20 | P QL-2b | U | U (EVA-5 stale) | U | P EVA-9/10, RF-20 |
| Crash / destruction | P GS-7 | P CL-4, RF-12W, S4.3 | X | U | P RF-12W | P GS-7 | P CL-2 |
| Ghost playback (flight, map, TS, KSC) | P GS-9, GS-12, LF-2 | P RF-8, S4.3 | P QL-4b | U | P V24W, V7W, AP-1 | V-lanes, W1 | P V*T / V*K, SE-1 |
| Loop playback | P LF-2 | **U** | LF-1/2 (reload at the save instant only) | U | P V7W | P LF-1/2 | U (watch mode across a scene change) |
| Real spawn / Real Spawn Control | P EVA-6/7, RR-1, VN-3 | P CI-2, CI-4, S1.5 | P CI-3 (save instant); back-in-time **U** | IG LT-1 | P SS-1; RSC-1 stale | P LF-1, EX-1 | P CI-6/7/10/11 |
| Crew / kerbals | P GS-9, EVA-7 | P CL-4, RF-12S, RF-13, RF-19 | P QL-2, QL-4 | X | P RF-12W | IG H31 | P RF-13R, CL-2, KB-5 |
| Vessel names | P VN-3, VN-7 | P VN-2, VN-9 | P VN-1 | P VN-5 | U | P VN-13 | P VN-6 |
| Missions window | P LF-2, VN-3 | P VN-2 | **U** | P VN-5 | P V4 | P W1 | P GUI census |
| Rewind points | P GS-9 (repeated) | P RF-4, RF-14, RF-15 | **U** (rewind, then a back-in-time F9) | U | P LF-2, EVA-6 | P GS-* | P RF-4 |
| Re-Fly / supersede / merge journal | P RF-14/15 | P RF-2, RF-3, RF-11 | P QL-3, QL-5c/5d, QL-7, S4.4 | P QL-5, VN-9, RF-16/17 | P RF-12W | P RF-8, S4.3 | P RF-1, RF-10 |
| Stock revert | U | P QL-5 | U | U | U | U | P VN-5; Revert to VAB has no live lane |
| Merge / commit / discard | P RF-4 | P S4.1, S4.3, RF-3, RF-17 | P QL-3, QL-6, S4.4 | P QL-5 | P CA-1 | P S4.3 | P S0.7, S0.9/10 |
| CommNet ghosts | X | X | U | U | P CN-3 | P CN-1, CN-2 | P CN-1T (stale) |
| Ghost FX | P GS-6, BAY-1 | **U** | P S1.9 (save instant) | U | P V7W | P H37, S1.9 | P MC-1/2 (stale DLL) |
| Save switching / main menu (GC-1) | - | - | - | - | - | - | **X only** |

Lanes marked stale had a red or invalid last flight, then a spec or code change, and have not
re-flown: RSC-1, ZF-1, VB-1, CN-1, CN-1T, ST-2, LF-1, L5, H58, RF-7T, RR-1, SE-1, EVA-5. Red by
finding: V1, V7T, L3-career-science-recover. Flaky: S4.2 / S4.4 (recorder-start race), CL-2
(award variance), SS-1. The modded-compat lanes (MC) fly whatever DLL that instance last got.

Four in-game test categories are run by no lane at all: ChainTipBlockedGhost, DeployedScienceGhost,
GhostReapplyFrame, GuiMock. The Rewind category runs at most 17 of its 39 cells on any host.

---

## 2. Career ledger: contracts, milestones, tech, facilities, strategies

Columns: Rewind / Re-Fly / F9 / revert / repeated rewinds.

| Item | Rewind | Re-Fly | F9 | Revert | Repeated |
|---|---|---|---|---|---|
| Contracts | log tokens only (HC-1/2); KB-3/4 use a pre-rewound fixture | **U** (in-game cell skips on every host, `RF-12W.toml:107`) | P QL-1 accept (ledger oracle gating); completion X | U | U |
| Milestones | HC-2 log tokens | X (open display bug TIMELINE-PHANTOM-MILESTONE) | P QL-2, QL-2b | U | U |
| Facility upgrade / repair | KB-2/3/4 pre-rewound fixture | U | P QL-1, KB-1 | U | U |
| Facility downgrade | X only (fix never flown) | U | U | U | U |
| Tech | KB-3 pre-rewound fixture | X | P QL-1 | U | U |
| Kerbal hire | KB-3 (block only) | U | P QL-1 | U | U |
| Kerbal death + rep tombstone | U | P CL-4 (save parse, gating) | P QL-3 | U | U |
| Recovery funds | HC-2 log tokens | P RB-1/RB-2 | P QL-2 | X | U |
| **Strategies (all)** | **X / U** | **X / U** | **U** | **U** | **U** |

Why the rewind column is weak: the ledger oracle refuses to run next to `InvokeRewind`,
`InvokeRewindToLaunch` or `AnswerMergeDialog` (`harness/lib/hlib.py:7334-7360`). Only 12 lanes
gate the oracle, and only QL-1 crosses a timeline operation. The ledger ground-truth harness runs
on L2, L4, HC-1, HC-2 and ZF-1, but **never after a rewind, Re-Fly or F9** (it runs before the
rewind, and its cell skips with a live recorder or pending tree). Repeated rewinds are flown only
on sandbox hosts (GS-9, RF-2, RF-3, RF-11), so **career x repeated rewinds is untested**.

xUnit idempotence exists and is decent: `LedgerStateFuzzerTests` (500 seeds, all nine modules,
double recalculate, equal-UT permutation, seed rows at every cutoff), plus
`CrossTierIntegrationTests`, `FullCareerTimelineTests`, `RewindUtCutoffTests.TwoPass_*`. But
"twice" there means the same cutoff run twice. Nothing models N rewinds that each append
supersede rows or tombstones. The fuzzer's own header notes a blind spot (deleting
`ResetDerivedFields` reds nothing).

### Strategies in detail

Capture is proven live, but only at the Space Center with no time operation (lanes
L3-strategy-currency-conversion and L3-strategy-exchanger-floor, in-game category
`StrategyLifecycle`, ledger oracle gating):

| Strategy | Live capture cell | Across any timeline operation |
|---|---|---|
| Patents Licensing | IG `CurrencyConverterStrategy_LedgerMatchesNetCredit` | U |
| Outsourced R&D | IG `ConverterStrategy_ScienceYield_CapturesCredit` | U |
| Open Source Tech Program | IG `ConverterStrategy_ReputationLeg_CapturesEarning` | U |
| Fundraising Campaign | IG `ConverterStrategy_ReputationDebitLeg_CapturesPenalty` | U |
| Appreciation Campaign | IG `ConverterStrategy_FundsDebitLeg_CapturesSpending` | U |
| Leadership Initiative | IG `OperationStrategy_RewardMultiplier_IsCapturedOnNominalReason` | U |
| Research Rights Sell-out | IG `ExchangerStrategy_OneShot_CapturesBothLegs` | U |
| Unpaid Research Program | none (shares mechanisms with the above) | U |
| Bail-Out Grant | none; X converter tests | U |
| Aggressive Negotiations | none; X only | U |
| Recovery Transponder | none (correct by design: recovery funds are the observed delta) | U |

Strategy x timeline operation is xUnit only: `RewindUtCutoffTests.StrategyActivate_CutoffFiltersLater`,
`StrategyReservationTests.Plan_*`, `StrategyExpiryReplayTests` (one rewind only). No lane activates a
strategy and then rewinds, and no lane pins a `PatchStrategies:` log line.

**Likely defect found (code read, not filed, not tested):** strategy conversion rows are always
written untagged (`RecordingId = null`, `LedgerOrchestrator.cs:5260-5439`), on purpose so that a
recording discard does not take back irreversible economy. But the award they divert from is
tagged to the flight. An F9 retires only tagged rows after the quicksave
(`ParsekScenario.Trim.cs:1210-1235`, "Untagged KSC rows are kept"), and a Re-Fly merge tombstones
tagged ContractComplete / MilestoneAchievement / ScienceEarning rows
(`TombstoneEligibility.cs:56-117`). So a contract completed in flight under Appreciation Campaign,
Leadership Initiative or Patents Licensing, followed by F9 or a Re-Fly merge, would keep the
diverted funds / science / rep leg with no parent award. When the clock reaches that UT, the pool
moves anyway (drift) or the guard clamps. A red xUnit test would settle it in minutes.

Other strategy edges nobody tests (code read): a strategy active across a rewind boundary or
re-activated after one (re-activation overwrites the commitment, `StrategiesModule.cs:113-126`);
two rewinds across a strategy expiry; reputation converter rows re-curved at a different running
rep after a rewind changes earlier rep; the known 1e-4 same-UT ordering imprecision
(`LedgerOrchestrator.cs:5150-5158`) over many re-walks near the steep part of the rep curve.

---

## 3. Logistics and real resource transfers

| Behavior | Status | What is actually checked |
|---|---|---|
| Surface route create, dispatch, deliver (loaded) | P RVR-2, RVR-4, RVR-7 | Log tokens `Delivery write ... tankBefore=200 tankAfter=400` plus the save's route counters (gating) |
| Origin debit, pickup, partial / full / funds short, endpoint removed or substituted | P RVR-8..RVR-20, V27M | Log tokens, route counters |
| First Mun delivery (unloaded) | P IR-5 | `written=281.95` pinned; tank before / after not pinned; route facet report-only |
| Real stock dock transfer captured as a route window | P BDOCK-1 | Only the sign of the delta is pinned, not 40 out = 40 in |
| Live tank writers read back through `PartResource.amount` | IG H38 / H39 / H40 / HV-1 | Synthetic routes on a rig. **Red on 2026-10-09 from test drift; fixed but not re-flown, so no green since the 10-07 part-scope and 10-15 escrow changes** |
| Escrow across a scene switch | P H60 (2026-10-16) | In-game cells |
| **Conservation (removed at origin == added at destination)** | **U** | Nothing relates the cost manifest to the delivery manifest; no lane reads tanks independently; the conservation fuzzer (TA-F) does not exist |

| Route x ... | Status |
|---|---|
| Rewind to before a delivery | IG synthetic restore only (H6, H38 cell); the one real lane H58 is re-pinned by prediction, never re-flown |
| Rewind-to-Launch of the route's own tree | U (IR-7 blocked by fixture C4) |
| Re-Fly of the source mission | X only. Superseding the source auto-pauses the route; re-binding to the new attempt is untested |
| F9 across a dispatch | X only (`RouteLoadReconcileTests`, 22 cells). The 2026-10-07 fix (A1) was never flown (IR-1, needs verb C2) |
| Stock revert across a dispatch | X only (IR-2 needs C2 and a `Revert` verb) |
| Many cycles under real warp | U. Only the seam time jump is proven (RVR-2 "firing ONCE") |
| Real Spawn Control / Missions / TS forward jump | U |
| Ticking and delivering at the KSC or in the TS | U live (ticks do run in all four scenes, `ParsekScenario.cs:31,1306-1344`) |
| Transport recovery credit | X (31 cells); RVR-4 pins only the zero-recovery skip |
| Player recovers an endpoint | U |
| Delivery into a docked visitor (A5) | X only (IR-9 not authored) |
| Chain-tip / end spawn carrying unpaid cargo (TA-7) | X only (RC-2 not authored) |
| Orbital endpoint destroyed mid-session | U |

Edges with no test (code read): a debit that finds the origin unresolved still records the full
manifest and the delivery writes it in full, so cargo can appear that nothing paid for (designed as
a one-tick race, `RouteOrchestrator.cs:3100-3150`; nothing asserts the delivery side); delivered
totals can differ between a Parsek forward jump and real high warp over the same span; routes keep
ticking during a live Re-Fly (no session gate in `RouteOrchestrator.Tick`); unloaded writes at the
KSC / TS go to the proto vessel and nothing checks they survive the next save (the RVR8 bug class,
in scenes no lane delivers in).

---

## 4. Resource-producing parts: lab, ISRU, drills, fuel cells, power

How it works today (code read): a ghost is a mesh with no part modules, so it produces nothing (by
design). A spawned vessel gets its end snapshot verbatim: tanks, lab data and stored science, and
each converter's `lastUpdateTime` (`VesselSpawner.cs:5842-5880`). Stock catch-up then runs from that
timestamp when the vessel loads. Repeated rewinds therefore spawn identical amounts.

| Part family | Status |
|---|---|
| Converter running animation on ghosts | P H37 (built from prefabs, not a recorded drill) |
| Drill / ISRU rate model, resource scan ledger | IG HV-2 (prefabs only, no live drill) |
| Fuel cell harvest windows, toggle, warp re-baseline | IG H38 |
| Stock catch-up credited to a harvest window | U in effect (`HarvestCapture_CatchUpOnLoad` skips on every host) |
| Fast-forward converter reset (`FixResourceConverterTimestamps`) | **U at every layer**, no test references it |
| Fast-forward vs Real Spawn Control warp (TA-6, open defect) | U (MINE-3 not authored) |
| Spawned tanks equal the end snapshot | U (no `ReadVesselResources` verb, no resource oracle) |
| **Mobile Processing Lab, any behavior** | **U** (GC-8 open; nothing in the source names the lab beyond the generic converter) |
| **Lab science reaching the ledger** | **?** (see below) |
| RTG, solar EC, batteries | U for resources (solar is covered as visuals only) |
| Mining end to end | U: no fixture has a drill, ISRU, ore tank or scanner; MINE-0..5 all unauthored |

Findings (code read):
- **TA-6 is wider than filed.** `ExecuteForwardJump` resets converter timestamps only on LOADED
  vessels (`TimeJumpManager.cs:541`, `:822-866`). An unloaded miner or lab keeps its old timestamp
  and catches up the whole jump the next time it loads, so "a Parsek jump is not production time"
  holds only inside the physics bubble. Not in the todo.
- **Lab science may never be ledgered.** Science reaches the ledger only through
  `OnScienceRecieved` with a subject (`GameStateEventConverter.cs:326-345`); subject-less
  `ScienceTransmission` changes are dropped on purpose (`GameStateRecorder.cs:1063`, `:1153`). If
  the stock lab transmits without a subject (believed, not decompiled), a committed future's lab
  science is not re-applied after a rewind, while the spawned lab still holds its stored science and
  can transmit it again. Net: lost or re-earned, not double counted. Needs a decompile of
  `ModuleScienceConverter`'s transmit path.
- A spawned converter whose snapshot timestamp is later than the spawn UT gets a negative catch-up
  (stock is believed to self-heal to zero; unverified).
- Re-Fly or F9 with a running ISRU or lab aboard: restored verbatim from the quicksave, untested
  with a converter aboard.

---

## 5. EVA moves, docked crew and fuel transfers, EVA construction (V's follow-up)

Columns: recording / ghost playback / spawned contents / crew reservation / x rewind / x Re-Fly /
x F9.

| Action | Recording | Ghost | Spawned contents | Reservation | Rewind | Re-Fly | F9 |
|---|---|---|---|---|---|---|---|
| A. One kerbal hops across 2-3 vessels (EVA A, board B, EVA, board C) | P only for reboarding the SAME vessel (EVA-1/2/3, CI-1); boarding a different vessel X (tree shape only) | U | U (EVA spawn position IG H20) | X | U | P same-vessel EVA (RF-18/19/20); kerbal leaving for a foreign vessel X | P QL-2b (EVA only); board x F9 U |
| B. Crew transfer while docked, then undock (kerbal A -> B) | **Not recorded, by design** (no `onCrewTransferred` handler outside the Kerbals window; documented limitation, `SwitchSegmentNoOpClassifier.cs:146-161`). Only the undock snapshot carries the result | U (ghosts show no crew) | U (should work from B's end snapshot, code read) | X (undock-child crew path "never exercised", `KerbalEndStateTests.cs:86-93`) | U | U | U |
| C. Fuel transfer while docked, then undock | P BDOCK-1 / IG H55 for the route window delta only; per-part amounts in the post-undock snapshots U | n/a | U (CI-2/CI-3/RF-8 spawn the tips but check pids, not tanks; fixture transfer is inverted) | n/a | U | U | U |
| D. EVA construction: take or attach on a FOREIGN vessel | P EVA-11 (attach + two detaches), RC-6 (take a part) | **U, and not implemented (code read)** | P EVA-11 (`parts=15`), RC-6 (`storedParts`) | n/a | P EVA-11, RC-6, EVA-6/7 (rewind to launch only) | X | U |
| D'. EVA construction on your OWN vessel | Nothing recorded by design; the end snapshot carries it | ghost keeps the start shape until the spawn (code read) | from end snapshot | n/a | U | U | U |

Findings (code read, verified against the source):
- **Construction ghosts never change shape mid-playback.** The design says a PART_ADDED event means
  "the ghost starts rendering this part after this UT", but playback never reads
  `SegmentEventType.PartAdded / PartRemoved` (only `GhostingTriggerClassifier` and the writer
  `ForeignInventoryTransfer` use them). The ghost snapshot is set once at the first transfer. For
  EVA-11 the ghost should show 17 parts until the detaches, then 15; it shows one shape throughout.
- **A claimed foreign vessel can vanish after a rewind.** `VesselGhoster.GhostVessel` (called from
  `ParsekFlight.cs:12989`) despawns the real vessel but creates no ghost (`ghostGO = null`, "deferred
  to 6b-4", `VesselGhoster.cs:155-168`). Until the member recording starts, nothing is shown. EVA-11
  and RC-6 hide this because their transfer happens seconds after the start.
- **Cross-tree crew transfer can strand a reservation.** A kerbal moved from tree 1's station into
  tree 2's vessel while docked leaves tree 1's open hold; recovery closes holds only in the same tree
  (`KerbalsModule.cs:1318-1335`, intended for strandings, but this case is a transfer, not a
  stranding).
- A kerbal who joins a recording mid-way is invisible to reservations: start crew comes from the
  ghost snapshot (`KerbalsModule.cs:2131`), and a start kerbal missing at the end reads Dead
  (`:1815-1820`) when the dock was not recorded as a dock branch.
- Open: EVA-CONSTRUCTION-ON-FOREIGN-VESSEL-UNCLAIMED (a foreign EVA kerbal's transfer is refused; a
  part dropped on the ground becomes a vessel no recording carries), TAIL-TRIM-UNWITNESSED-RESOURCE-CHANGES
  (an in-dock fuel transfer leaves no witness, so a trimmed tail's split arrives early). There is no
  verb for a "put" into a foreign container, and no seam path for a crew transfer.

---

## 6. Cross-system combos no lane exercises

1. Re-Fly while a looped ghost plays.
2. F9 while a merge dialog is pending.
3. Rewind or Re-Fly while a spawn is deferred.
4. Rewind while an EVA kerbal is out or floating (GC-5).
5. Dock with a Parsek-spawned vessel, then rewind (CI-9, VN-4, VN-10 dock but never rewind).
6. Dock or undock, then Rewind-to-Launch.
7. Save switching in one KSP session, and the main-menu round trip (GC-1).
8. Rewind to launch, then a back-in-time F9; repeated back-in-time F9.
9. Stock revert combined with ghosts, crew, spawn or warp; Revert to VAB after a committed flight.
10. Career combined with ghosts, loops, map, spawn, docking or CommNet (QL-1/2/2b assert no ghost).
11. Contracts across a Re-Fly.
12. CommNet ghosts across rewind, Re-Fly or F9; a relay that drops out under require-signal (GC-14).
13. Ghost FX during a Re-Fly.
14. A chain tip crossing its spawn time at the KSC or TS after a rewind (RC-3); recovering a vessel a
    committed future docks to (GC-2).
15. Repeated rewinds combined with spawn or crew, or with a career.
16. Missions window across an F9.
17. A strategy active through any timeline operation (section 2).
18. A lab, ISRU or drill aboard through any timeline operation (section 4).

---

## 7. What is still left to do (excluding co-op, refactors, Gloops)

- **Open product defects:** QL-KSC-ROWS-CROSSED-ACROSS-A-SCENE-CHANGE (medium),
  COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE, QUICKLOAD-REFLY-SCOPE-KEEPS-SESSION-CHILDREN-FUTURE,
  QUICKLOAD-TRIM-STAMPS-CUTOFF-AS-EARLIER-END, QUICKLOAD-RESUME-KEEPS-AN-ENDING-STAMPED-JUST-AFTER-THE-SAVE,
  TIMEJUMP-CONVERTER-POLICY-DIFFERS-BY-JUMP-KIND (TA-6), TAIL-TRIM-UNWITNESSED-RESOURCE-CHANGES,
  EVA-CONSTRUCTION-ON-FOREIGN-VESSEL-UNCLAIMED, CLAIMED-VESSEL-DESPAWN-AT-REWIND-OUTSIDE-FLIGHT (design),
  REPUTATION-SEED pending-tree death row, plus about 15 low / display items (todo).
- **New from this audit (not filed):** strategy conversion rows surviving F9 / Re-Fly (section 2);
  construction ghosts not changing shape and the claimed-vessel visual gap (section 5); TA-6 for
  unloaded vessels; the lab science ledger path (section 4); the cross-tree crew-transfer hold.
- **Untraced gameplay cases:** GC-1..GC-6 and GC-8..GC-16 (GC-7 done).
- **Fixed but never flown:** about 27 product entries and 11 harness entries; the struck todo entries that
  say xUnit only or not flown (route fixes A1, A2, A4, A5, TA-7, TA-8; chain-walk and
  optimizer split fixes; facility downgrade debit; Tracking Station ledger cutoff; EVA-5).
- **Missing harness machinery:** `ReadVesselResources` and `Quickload` / `Revert` verbs (TC-1), a
  resource-conservation oracle (TC-2), crew and identity rules (TC-3), a rewind-aware ledger oracle
  (TC-4 / C1), the route read-back verb (C2), fixtures C3 / C4 / C5 / TC-6 and the miner fixture
  (MINE-1).
- **Never-authored lanes:** RC-1..RC-5, MINE-0..5, IR-1..4, IR-6..9, IR-11, IC-1..4, GP-1..20,
  FZ-1, CS-0 onward.

---

## 8. Ranked: the lanes or fixes that close the riskiest gaps

1. **Strategy rows across F9 and Re-Fly.** Write the red xUnit test (contract completed in flight
   under Appreciation Campaign, then F9 and then a Re-Fly merge); fix if red. Then one F9 lane on the
   QL-1 host with a strategy active (the ledger oracle is allowed next to `LoadGame`). Cheap, and it
   may be real funds drift.
2. **`ReadVesselResources` verb plus a resource-conservation oracle (TC-1 / TC-2).** Then RC-1
   (fuel transfer while docked, undock, rewind, check both spawned tanks) and conservation gates on
   the existing RVR / IR lanes. Without this nothing proves any amount of any resource.
3. **Re-fly the stale lanes, no build needed:** H38, H39, H40, HV-1 (logistics in-game cells, no
   green since the 10-07 / 10-15 changes), H58 (real route rewind), EVA-5.
4. **TC-4 rewind-aware ledger oracle, then GP-1:** a career with a strategy, a contract and a
   recovery, rewound and re-flown several times, with the ledger ground truth checked after each
   operation. This is the only way to answer "correct no matter how many rewinds".
5. **EVA construction ghosts:** an EVA-11 variant that rewinds and watches the ghost through the
   attach and detach, to confirm the shape and the claimed-vessel gap; then the fix.
6. **Lab and converters:** decompile the lab transmit path; then the MINE-1 fixture with a lab,
   MINE-3 (TA-6, including an unloaded vessel) and a lab rewind / spawn lane (GC-8).
7. **Contracts across a Re-Fly:** a host where `ContractTombstonesAcrossSupersede` does not skip.
8. **Route read-back verb (C2), then IR-1 and IR-2** (F9 and revert across a dispatch).
9. **EVA hop across 2-3 vessels with a rewind** (the `EvaBoard targetPid` arg already exists, no
   spec uses it), plus a crew-transfer verb so case B can be flown at all.
10. **GC-1..GC-4 headless traces** (save switching, recovering a vessel a future docks to, old
    recordings dropped silently, a contract completed by a spawned vessel).
