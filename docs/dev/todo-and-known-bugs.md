# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.
- `done/todo-and-known-bugs-v5.md` — the v0.9.1 / v0.9.2 cycle: Re-Fly Phase D wrap-up, debris-rendering PR stack through PR 3c and the always-shadow follow-up, Phase 11.5 storage and observability follow-ons, the multi-debris explosion-audio fix, and the carrying-over numbered items #570-#640. Archived 2026-05-10.
- `done/todo-and-known-bugs-v6.md` - the v0.9.2 / v0.9.3 bug-closure wave and the first half of the v0.10.0 cycle: Re-Fly supersede / anchor-propagation / co-bubble-retirement closures, the watch-mode W-cycle + chain-seam fixes, the schema generation-3 reset, the Missions window (tab + looping + periodicity + zero-drift reschedule), re-aim interplanetary transfers, the Map/TS render-tracer MVP (PR #1005), and the debris-rendering / switch-fly auto-record closures. Archived 2026-06-05.
- `done/todo-and-known-bugs-v7.md` - the v0.10.1 / v0.10.2 / v0.10.3 finish-up: logistics milestones M1-M6 (non-KSC origin, mod resources / harvest, pickup, multi-stop / multi-origin / round-trip, inter-body, legibility) + the claw producer; missions M-MIS-1..6 / 8 / 9 and the re-aim / periodicity / phasing solver stack; the Map/TS render rewrite cutover; the career-economy bug wave (BUG-A..H, the records-milestone recalc storm, contract-discard desync); and the ledger ground-truth audit closures. Archived 2026-07-09.
- `done/todo-and-known-bugs-v8.md` - the v0.10.4+ automated-testing buildout and the 2026-08 finding wave: the M-A5/M-A6/M-B1/M-B2/M-C1/M-C2 module records and the whole B1-B30 / EVA / BDOCK / CL / GS / V-lane flight-forensics ledger, the M-A7 render-composition Phases 1-2 closures, the interplanetary-program measurement records (Eve/Dres/Moho/Jool/Eeloo/moon-to-moon/B29 Kerbin return), the watch-mode entry-refusal family, the ledger/career capture-fix wave, the 2026-08-29 hygiene pass closures (#426 rejected-by-design, #428 won't-build), and every other struck entry through 2026-08-29. Archived 2026-08-29.
- `done/todo-and-known-bugs-v9.md` - the 0.10.5 cycle through 2026-10-06: the stock-screen reservation layer (R&D, Mission Control, Administration, Astronaut Complex, VAB/SPH crew dialog, facility menus) and the UI refresh (Real Spawn Control, Logistics + Route History, Missions, Mission Log, Kerbals, the Timeline Career view, Recordings, Settings; the Career window and recording deletion removed), the KSP-settings audit fixes, the Re-Fly / rewind / recording correctness closures, the GUI census / mirror / state-gallery buildout, and the harness flight-efficiency and post-flight dialog work. Archived 2026-10-06. It is a full snapshot: every entry still open at archival was carried into this file, together with 16 struck entries whose body still names an open residue.
- `done/todo-and-known-bugs-v10.md` - the 0.10.6 cycle through 2026-10-24: load restores the save (F9 and the Load menu put Parsek back exactly as the save was), the Re-Fly ledger safety rules and the committed-value rule (reserve, floor and debt for funds, science and reputation; contract outcomes held), logistics without money, the rewind / Re-Fly / Discard vessel holds and foreign board / seat / EVA claims, GC-7 vessel names, the recorded-velocity seed and orbit fixes, the mining / lab / ISRU fixes, and the GUI P-item wording fixes. Archived 2026-10-24. It is a full snapshot: every entry still open at archival was carried into this file, together with 52 struck entries whose body still names an open residue (an owed lane, an unfixed follow-up or a pending ruling).
- `done/todo-and-known-bugs-v11.md` - the 0.10.7 / 1.0.0 cycle through 2026-11-06: the stable 1.0 save format and its freeze, the ledger event identity (nothing paid twice across rewinds) and the rewind ledger campaign RW-1..RW-24, the scripted campaign to the Mun (CS-0..CS-7), the replay-theory audit and career-settings fixes, Re-Fly / revert / load correctness, the flag timeline, the Real Spawn Control time-jump move, the GUI census tooling closure, the pre-Parsek backup hold and the harness warp and coast speed-ups. Archived 2026-11-06, after the 1.0.0 public beta. It is a full snapshot: every entry still open at archival was carried into this file, together with 32 struck entries whose body still names an open residue (an unbuilt lane, an unfixed follow-up or a deliberately left-open edge). Struck entries whose only residue was a flight of an existing lane were discharged by the 2026-11-06 full run (all 537 lanes on main).
- 2026-11-06 (owner: this file keeps only work still to do): 59 entries with nothing left to do were moved out; their text is in `done/todo-and-known-bugs-v11.md`. They were report-only findings, accepted limitations, answered questions, recorded rulings, fixed entries whose remaining notes are not work, shipped features, and parked or unscheduled plans (PLAYER-LOOPING-REMOVAL on hold, GLOOPS-EXTRACTION not scheduled, CHILD-BRANCH-SINGLE-SLOT-OVERWRITE left until a player-visible defect shows). A struck header still in this file is fixed but its body names remaining work.
- 2026-11-07 (1.0.1 preparation): 13 more entries finished since the rotation (the three supply route fixes and the harvest edge frame from the MINE lanes, the CS-7 lander, the Re-Fly recovery stamp, the Missions header cell and the six GUI audit rulings GUI-D5 / D6 / D8, GUI-EXPOSURE-4 / 46 and GUI-I) were moved to the end of `done/todo-and-known-bugs-v11.md`, under "Moved out 2026-11-07".

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## RESIDUALS-OF-ENTRIES-ARCHIVED-2026-11-07: small follow-ups left inside 20 finished entries moved to the archive [FILED 2026-11-07 (owner: mark the finished entries done). OPEN; each line names its archived entry in `done/todo-and-known-bugs-v11.md` ("Moved out 2026-11-07 (finished entries still listed)"); none is a known player-hit bug]

- AUTOMERGE-ON-BY-DEFAULT: four residuals at the end of that entry (frequency of the resume miss, step 1 still injected, entrance A undriven, the dangling limbo dispatch).
- REWIND-READBACK-GUARD-HAS-NO-LIVE-WITNESS-LANE: the A7 strategy / mod-grant follow-up.
- KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL: the spawned-vessel recovery arm is unit-proven only (no lane flies it), and a legacy recording with no launch guid is never an owner.
- ROUTE-ORIGIN-PROOF-TRANSPORT-MANIFESTS-INCLUDE-THE-DEPOT: `RouteRunCargoManifest` is still scoped to the whole merged vessel; re-scope it in its own pass with the delta measured.
- ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE: the read-side cell `RouteOriginProof_StartedDockedToNonKsc_ProducerLandsProof` self-skips until a committed recording carries a proof; un-skipping moves RVR-1's pin.
- GHOSTCOMMNET-CHAIN-GHOST-NODE-GAP: in the Tracking Station, a vessel a flight despawned relays nothing before its first claim (deliberately dark).
- ATTEMPT-EVENTS-DROPPED-WHEN-RESUMED-IN-PLACE: `SavePendingTreeIfAny` still skips a dirty pending-tree member that shares a committed id (lane QL-13).
- DOCK-CHAIN-ABSORBED-PARTNER-PID-ONLY: a dock into a resumed Parsek-spawned vessel reads as a different launch (identity links are not consulted by `VesselLaunchIdentity`).
- CONTRACT-CROSSED-OUTCOME-MISSING-FROM-STOCK-ARCHIVE: a contract either gate leaves unfiled is not filed later when the clock reaches its outcome.
- D5-PROMOTED-DEBRIS-MAXDIST-RELATIVE-FRAME: the capture's distance is measured from the promotion point, and a capture on the vessel-switch stop path with a Relative section still open takes the flat route.
- RECORDER-PHYSICS-SOI-NO-SECTION-SPLIT: lane owed, a formation flight through an SOI edge with a background wingman.
- VB-1-KSP-HANG-AT-MAIN-MENU: a hang after the MAINMENU line is still caught only by the long timeout.
- FAST-POLL-SETTLES-STILL-RACE: CI-6 still waits on a timer; a named-condition wait would replace it.

## TEST-GAP-AUDIT-2026-10-31-RESIDUE: what the 2026-10-31 test gap audit still leaves open [FILED 2026-11-01 from a read-only re-check of the audit (`roadmap/test-gap-audit-2026-10-31.md` in the project files) against main 2622fd5. OPEN; test program, no known defect]

Closed since the audit (each with its own entry or PR): A1 RECORDINGS-TAB-LOOP-NEVER-REPLAYS-ON-MAP
(#2342, SLM-1 / SLM-2 / SLM-1T read the map ghost by POSITION), A2 ROUTE-DELIVERY-IGNORES-DESCENT-TRIGGER
(xUnit only), C2 the Re-Fly value fuzzer (#2347), C3 SAVE-ROUND-TRIP-GATE (#2349), the route
Re-Fly / multi-cycle warp lanes IR-3 and IR-6 plus `RouteTimelineFuzzerTests` (#2345, part of C1),
and REV-1 / SWS-1 / CN-4 / CN-5 for Revert to VAB, save switching and CommNet ghosts across a rewind
and a load (#2348, #2352, #2354). Still open, each found by grep on main 2622fd5:

- [ ] C4: `BackgroundRecorder.OnBackgroundVesselSOIChanged` and `OnBackgroundVesselGoOffRails` are
  named by no xUnit file (only the SOI seam producer wiring is gated).
- [ ] C8: no test runs `RunOptimizationPass` over a store holding a committed route, so a load-time
  rewrite that flips a route to SourceChanged would go unseen.
- [ ] C9: `GhostCommNetManager` is named by no xUnit file (see GHOSTCOMMNET-PASS3-DEDUPE-AND-SCAN-COST).
- [ ] C6: `RouteHarvestAnalysis.ComputeHarvestedTotals`, `ComputeFullRunGains` and
  `FindGainAnchorIndex` have no test; `HarvestCapture_CatchUpOnLoad` still skips on every host.
- [ ] C5: `EffectiveState.ResolveRewindPointSlotIndexForRecording` still takes the first match
  silently when two slots resolve to one recording.
- [ ] C1 remainder: no per-resource conservation check (origin debit = delivered + by-design loss)
  in an in-game category or the harness (HARNESS-RESOURCE-ORACLE-AND-INVARIANT-RULES).
  Progress 2026-11-06 (branch `routecons`): lane `RCN-1-route-conservation-across-loads` (LIVE-PROVEN
  `2026-11-07_0010`) reads both ends of the two-stop relay route on `rover-relay-c-recorded` and gates
  origin + destination against the equation from the route's own windows (debit 154.4 + transport
  launch-cargo drawdown 45.6 = credit 200, no by-design loss there) across two F9s and a go-back
  rewind. Still open: no in-game category, and no host with a nonzero loss term (the relay on
  `rover-relay-recorded` would show the 73.2 residual, but its endpoints are post-relay, B empty).
- [ ] B3 / item 3: route render lanes still pin counts and log lines, not where the route ghost or
  its line is (only the SLM lanes read a loop ghost's position).
- [ ] B7: loops never meet a Re-Fly, a career save or a scene change in watch mode.
- [ ] B8: ghost FX across a Re-Fly or a revert has no lane.
- [ ] B11: in-game categories no lane runs (ChainTipBlockedGhost, DeployedScienceGhost and the rest
  of the audit's list).
- [ ] Fixes proven by xUnit only, never seen in game: ROUTE-DELIVERY-IGNORES-DESCENT-TRIGGER,
  the three 2026-10-31 low ledger fixes
  (STOCK-POOL-REFERENCE-CARRIED-ROWS-COUNTED-TWICE, OPTIMIZER-MERGE-RETAGS-LEDGER-ROWS-ONLY,
  SUB-100-ROLLOUT-AND-BAILOUT-NOT-LEDGERED), RESCUE-PLACED-MARKER-SESSION-ONLY,
  RESCUE-PLACED-MARKER-NEVER-RETIRES, the drop path of
  TRIMMED-CHECKPOINT-SECTION-ENDS-BEFORE-IT-STARTS (B13 passed without reaching it), the RW-9R reading
  variant owed by REWIND-LEAD-WINDOW-KSC-ACTIONS-LAND-IN-THE-FUTURE, and the 2026-10-06 / 10-07
  chain-walk, optimizer, facility and route-cargo fixes whose headers say "not flown".

## TIMELINE-OP-COVERAGE-PROGRAM: back-in-time loads, revert, mining and resource conservation are barely tested [FILED 2026-10-06 from the coverage-extension research, branch `ccr-77f23eb2-dbqh6i`. OPEN; test program]

Research: `docs/dev/research/coverage-extension-plan-2026-10-06.md` and
`docs/dev/research/feature-op-coverage-matrix-2026-10-06.md`. No lane loads a save back in time
(all 17 green in-place reload lanes reload the instant they saved), no lane drives a stock revert
(there is no verb), no fixture contains a drill, ISRU or ore, every career rewind check is
log-token level, career lanes check no playback, and contracts x Re-Fly never executes on any
host. The defects below were found in that space.

Fix: the register in `docs/dev/autotest-roadmap.md` ("The timeline-operation coverage program"):
red xUnit tests with each fix, the load-path x reconciler wiring gate, headless timeline fuzzers,
the new verbs / oracles / fixtures, then the QL / MINE / RC / GP lanes and the FZ-1 fuzzer lane.

---

## CROSS-SYSTEM-COVERAGE-GAPS-2026-10-16: strategies, the lab, EVA construction ghosts, docked crew and fuel moves, and resource amounts are not proven across timeline operations [FILED 2026-10-16 from a read-only coverage audit at the owner's request, branch `claude/project-thread-t7o1xa`. OPEN; test program plus suspected defects]

Research: `docs/dev/research/coverage-matrix-2026-10-16.md` (system x rewind / Re-Fly / F9 /
revert / warp / ghost playback / scene change, with what each lane actually asserts). Common
thread: lanes assert Parsek's own log lines, and no lane independently reads a tank, a crew roster
or a ghost's shape. The items below are code reads, not reproductions; each becomes its own entry
with a red test once confirmed.

- [x] ~~STRATEGY-CONVERSION-ROWS-SURVIVE-F9-AND-REFLY (suspected defect): conversion legs are written untagged (`LedgerOrchestrator.cs:5260-5439`) while the award they divert is flight-tagged, and F9 / a Re-Fly merge retire only tagged rows (`ParsekScenario.Trim.cs:1210-1235`, `TombstoneEligibility.cs:56-117`), so the leg would outlive its award. Roadmap XS-1.~~ Same defect as STRATEGY-CONVERSION-ROWS-OUTLIVE-THEIR-AWARD, fixed 2026-10-16 in PR #2123 (the door tags its rows like the award, `GameStateRecorder.ResolveStrategyConversionRecordingTag`; live-proven by QL-2c `2026-10-16_2239`). Re-checked 2026-10-24 on main f3a22ce: the F9 half is moot since save-file loads are cold (PR #2155; the save's own ledger never held the share, QL-2c reshaped), and the Re-Fly half holds (`TombstoneEligibility.IsSupersedeTombstoneEligible` takes every tagged conversion row type; `StrategyConversionTimelineTests.ReFlyMerge_TombstonesTheConvertedShareOfTheSupersededFlight`).
- [x] Strategies across any timeline operation: no lane activates a strategy and then rewinds, re-flies, F9s or reverts; Bail-Out Grant, Aggressive Negotiations and Recovery Transponder never ran in game. Roadmap XS-1, GP-1. Traced 2026-10-24 (branch `claude/project-thread-dkcvpn`): rewind / F9 / revert hold through the expiry gate and the patch's future guards; three gaps found and fixed, STRATEGY-ACTIVATION-CROSSED-IN-FLIGHT-NOT-APPLIED, STRATEGY-PATCH-CUTOFF-LESS-WALK-READS-THE-LATER-ACTIVATION, STRATEGY-EXPIRY-LOST-WITH-A-DISCARDED-FLIGHT. Lanes authored 2026-10-25 (branch `claude/project-thread-eo3rd8`): STR-1 (Bail-Out Grant + Appreciation Campaign across a Rewind-to-Launch and an F9), STR-2 (an activation crossed inside a Re-Fly), STR-3 (Leadership Initiative's shares taken back by a stock revert); QL-2c already covers the share across an F9, and LedgerGroundTruth now compares the strategy list. Still open: expiry across a timeline operation (stock 1.12.5 durations read 0 without KSPCF, so no stock-only lane can reach an expiry). Aggressive Negotiations ran in game since (RW-9 repair, RW-23 part purchase, RW-24, RW-10, all LIVE-PROVEN 2026-10-28 / 10-31). Traced 2026-11-06 (branch `str4`, no defect): a facility upgrade under it books the observed net debit with no funds leg and its reputation leg as its own uncaused row, both applied once at the crossing; Recovery Transponder Fitting has no currency effect (stock folds it into the recovery factor, factor 1 on the pad or runway either way), so the recovery is the observed payout. Lane STR-4 (LIVE-PROVEN `2026-11-07_0408`, its first flight red only on two over-broad spec forbids) runs both across a Rewind-to-Launch, with a committed deactivation crossed by time, the nearest stock-reachable stand-in for an expiry. DONE for stock: every stock strategy has now run across a timeline operation; an expiry stays unreachable without KSPCF.
- [x] Resource amounts: nothing checks origin-out == destination-in for routes, fuel moved while docked, or a spawned vessel's tanks. Roadmap TC-1 / TC-2, RC-1. Progress 2026-10-16: the `ReadVesselResources` verb and `resourceq.py` landed, and RC-1 reads both spawned tanks after a docked transfer and two rewinds; routes are still unchecked. Progress 2026-11-06: routes have a lane, `RCN-1-route-conservation-across-loads` (LIVE-PROVEN `2026-11-07_0010`; origin + destination summed at ten points across two F9s and a go-back rewind).
- [x] ~~Career x repeated rewinds, and LedgerGroundTruth after a rewind, Re-Fly or F9: never run.~~ Roadmap TC-4, GP-1. DONE 2026-10-25: L8 covers repeated rewinds; the harness walked past the clock (LEDGER-GROUND-TRUTH-WALKS-PAST-THE-CLOCK, fixed), and L13 runs it after a Re-Fly merge and an F9, live-proven `2026-10-25_0228` after its second flight found EDITOR-LOAD-WALKS-PAST-THE-CLOCK.
- [x] ~~Stale lanes: H38 / H39 / H40 / HV-1 not green since the 10-07 and 10-15 logistics changes; H58 and EVA-5 re-pinned, never re-flown. Roadmap XS-2.~~ DONE: all six re-flown green on main 412d48e (`2026-10-16_2155` .. `_2208`), and green again in the 2026-11-06 full run.
- [x] ~~EVA-CONSTRUCTION-GHOST-SHAPE-NEVER-CHANGES (suspected defect): playback never reads `SegmentEventType.PartAdded / PartRemoved` (only `GhostingTriggerClassifier` and `ForeignInventoryTransfer` name them), though the flight-recorder design says the ghost renders the part from that UT. Roadmap XS-3.~~ Same defect as CONSTRUCTION-GHOST-NEVER-CHANGES-SHAPE, fixed 2026-10-16 (live-proven by EVA-11 `2026-10-16_2322`); re-confirmed by code reading 2026-10-23.
- [x] ~~CLAIMED-VESSEL-HAS-NO-GHOST-BEFORE-ITS-MEMBER (suspected defect): `VesselGhoster.GhostVessel` despawns the claimed vessel and creates no ghost (`VesselGhoster.cs:155-168`), so it is missing after a rewind until its member recording starts. Roadmap XS-3.~~ Same defect as CLAIMED-VESSEL-VANISHES-BEFORE-ITS-CLAIM, fixed 2026-10-16 (`ParsekFlight.UpdateClaimedVesselHoldGhosts`; live-proven by EVA-11 `2026-10-16_2322`). Re-checked 2026-10-24 on main f3a22ce against PR #2181: the plain rewind's holds feed the same hold ghost on every flight load (`RestoreRewindVesselHolds`; EVA-11 / RC-6 `2026-10-22_1431` / `_1432`), and outside flight a held vessel is hidden by owner ruling 2026-10-22. The two cases still uncovered are filed on their own: REWIND-HOLD-MISSES-CROSS-TREE-ADOPTED-VESSEL and DISCARD-REFLY-LANDS-WITHOUT-REWIND-HOLDS.
- [x] ~~Lab science and the ledger: science reaches the ledger only with a subject (`GameStateEventConverter.cs:326-345`, `GameStateRecorder.cs:1063`, `:1153`); whether stock's lab transmit carries one needs a decompile. Roadmap XS-4, XS-5 (GC-8).~~ Decompiled 2026-10-17: it carries none; filed and fixed as LAB-SCIENCE-NEVER-LEDGERED (xUnit; lane LAB-1 live-proven 2026-10-17).
- [x] ~~TA-6 outside the physics bubble: see TIMEJUMP-CONVERTER-POLICY-DIFFERS-BY-JUMP-KIND. Roadmap MINE-3.~~ Fixed 2026-10-17 (xUnit; lane LAB-1 live-proven 2026-10-17).
- [x] ~~Contracts x Re-Fly never executes (the in-game cell skips on every host). Roadmap XS-6.~~ 2026-10-24: the state it looked for is unreachable by design (a contract row seals its slot, design 2.12) and it drove the legacy `CommitSupersede`; the cell is rewritten to assert today's rule in a live session (no contract row in the retire set, nothing accepted after the clock Active). The trace found REFLY-SESSION-SHOWS-CONTRACT-ACCEPTED-LATER and REFLY-DEADLINE-EXPIRY-REFUSES-THE-MERGE (both fixed); lane RF-C1.
- [x] ~~EVA hop across 2-3 vessels and a crew transfer while docked then undock: never flown (no spec passes `EvaBoard targetPid`, no crew-transfer verb); a crew transfer is not recorded by design (`SwitchSegmentNoOpClassifier.cs:146-161`), and a kerbal moved into another tree's vessel keeps the first tree's hold (`KerbalsModule.cs:1318-1335`). Roadmap XS-7.~~ DONE: the `CrewTransfer` verb and lanes CX-1 (docked crew transfer, then an EVA hop through `EvaBoard targetPid`), CX-2 .. CX-6 (crew swapped at a dock, the foreign-tree hold, idle visits) are live-proven and green in the 2026-11-06 full run.

Fix: the register in `docs/dev/autotest-roadmap.md` ("The cross-system gap program (2026-10-16)"),
worked in its rank order.

---

## MINING-ISRU-UNTESTED-END-TO-END: no fixture or lane mines ore or runs an ISRU [FILED 2026-10-06 from the coverage-extension research, branch `ccr-77f23eb2-dbqh6i`. MOSTLY COVERED since 2026-10-17 by the `miner-rover-*` fixtures and 11 committed MINE lanes (see Progress below); MINE-4 LIVE-PROVEN 2026-11-07 as `MINE-4-rover-docked-origin-route-rewind` on the scripted `miner-rover-relay` (`2026-11-07_1209`, PASS attempt 1; its flights found and fixed ROUTE-MIDTREE-ORIGIN-EDGE-ONE-FRAME-LATE, ROUTE-SOURCE-EPOCH-PENDING-AT-CREATE and ROUTE-TRANSPORT-RESPAWN-NOT-EXCLUDED); the lab lane XS-5 is covered by LAB-7 / LAB-9a / LAB-9b / LAB-10 / LAB-10b (live-proven 2026-10-19); MINE-0 (S1.9 `2026-11-07_0023`), MINE-5a (`2026-11-07_0022`) and MINE-5b (`2026-11-07_0448`) LIVE-PROVEN; harness / fixture]

Converters are exercised only through a FuelCell (H38, GS-6); drill and ISRU ghosts are built
from prefabs (H37) and rendered without a running loop (S1.9); harvest-route analysis runs on a
synthetic tree (HV-1); `LogisticsHarvestRuntimeTests.HarvestCapture_CatchUpOnLoad` skips
everywhere (needs a drill landed on ore); `FixResourceConverterTimestamps` has no test at any
layer; no fixture contains a drill, an ISRU, an ore tank, a scanner or Ore; the coverage registry
has no converter value. Mission actions cannot deploy drills: `set_converters` starts / stops converter processes but
only flips a harvester's `active` flag, and `set_deployables` skips harvesters.

Fix: MINE-0..MINE-5 in the roadmap register - converter events on the drill / ISRU showcases;
the `minmus-miner-landed` forge (part definitions harvested from a live VAB session) and a
record / mine / ISRU / commit / Rewind-to-Launch / spawn lane with a saveParse check of the
spawned vessel's Ore against the recording's end snapshot; the jump-policy lane; a mining-base
docked-origin route across a rewind; a live-drill harvest-origin route; GS-6 revision 3 with an
ISRU aboard. The two questions it raised are ruled (2026-10-06): cap harvest-origin routes
(HARVEST-ROUTE-PLAUSIBILITY-CAP) and ledger resource-scan unlocks (RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED).

Progress 2026-10-17 (branch `claude/lab-isru-drill-coverage-5oq37h`): the fixture now exists.
V built and flew a mining rover by hand ("Mining Rover RC", Kerbin, 2 km from the pad), and
`harness/tools/build_miner_rover_landed.py` derives `miner-rover-landed` from its VESSEL node
(tanks emptied, ReStock modules stripped, plus an orbiting `Observer Probe` as a FLIGHT host
after a rewind). The seam can now drive the parts: `PartAction` gained `deploy`, `retract`,
`startconverter` / `stopconverter` (with `converter=`), `experiment`, `labprocess`,
`labtransmit`, `inspect` and `all=true`. `MINE-2-rover-mine-rewind-warp-jump` (MINE-2 and
MINE-3 in one flight, on Kerbin instead of Minmus) records an hour of mining, commits, rewinds
to launch and checks the spawned rover's tanks against its end snapshot after a warp and after
a FLIGHT time jump. First flight 2026-10-17 (`2026-10-17_1207`, both attempts) INVALID, no
Parsek defect: deploy, startconverter and inspect all worked, but the spec missed RC-6's
StopRecording after CommitTree (CommitTree re-takes the committed tree for the still-active
rover, so the rewind was refused `recording-active`), and with the rover's radiators off every
1200 K part (four RTGs, four wheels, two batteries) exploded about 28 minutes into the rails
warp while packed, inferred overheat (stock `ModuleCoreHeat.MoveCoreEnergyToRadiators` counts
only radiators with `IsCooling` set). Fixed in the spec and the fixture (radiators on); LIVE-PROVEN
on the second flight `2026-10-17_1242` (PASS attempt 1, both spawns `snapshotMatch=true`). Still open: MINE-0, MINE-4, MINE-5, GS-6r3, a lab lane (XS-5), and
`HarvestCapture_CatchUpOnLoad` on a host with a drill on ore.

Program 2026-10-17 (owner ask: cover every drill and ISRU idea): the lane plan is
`docs/dev/research/mining-isru-test-program-2026-10-17.md` (MINE-6..MINE-15 plus MINE-4 / MINE-5,
built from V's own manual flight). First of them: `MINE-9-rover-real-spawn-and-loaded-jump`
(Real Spawn Control, a TimeJump with the spawned miner loaded, and a spawn after a jump past
the end) on the new fixture `miner-rover-watched`: LIVE-PROVEN `2026-10-17_1343` on its third
flight; its first found RSC-REWIND-TARGET-NEVER-LISTED and SPAWN-AFTER-JUMP-CATCHES-UP-THE-JUMP.
`MINE-6-rover-manual-flight-replay` (V's flight step for step: mid-warp ISRU and radiator
toggles, Space Center exit, the watched replay to the spawn): first flight `2026-10-17_1348`
INVALID, found IDLE-ON-PAD-DISCARDS-A-STATIONARY-MINER, and answered the open question: an ISRU
toggled during rails warp on the landed rover is recorded at the toggle UT (every edge about
10 ms after its packed press), so an on-and-off inside one warp is not lost.

Progress 2026-11-06 (branch `mine5`, nothing flown yet):
- MINE-0: the `Drill Junior`, `Drill-O-Matic` and `ISRU` part showcases nest a
  ConverterActivated / ConverterDeactivated pair in every deploy span
  (`BuildPartShowcaseRecording` `innerOnEvent`; xUnit `AssertNestedConverterCycles`), and S1.9
  pins `apply family=ConverterActivated|ConverterDeactivated surface=converter-loop ...
  applied=[1-9]` and `Converter loops driven` on one of the three. S1.9 must re-fly.
- GS-6 revision 3 is CLOSED by MINE-6 rather than by a sweep-craft revision: its live-proven
  flights (`2026-10-17_1431_a2`, re-pinned `2026-10-24_1006`) gate `apply family=ConverterActivated
  surface=converter-loop ... applied=[1-9]` and `Converter loops driven` on the miner's ghost,
  the "converter loop live on a flight ghost" GS6-CONVERTER-LOOP-NEEDS-AN-ANIMATION-GROUP-PART-ABOARD
  asked for.
- `HarvestCapture_CatchUpOnLoad`'s body now runs on a drill on ore: it moved into a shared core
  (`RunCatchUpOnLoad`, the Logistics declaration unchanged so `total=47` holds) that the new
  isolated `MiningHarvest` category calls on lane `MINE-5a-rover-live-drill-harvest-cap`
  (`miner-rover-landed`, drills deployed by the lane). Two test-side fixes on the way: the cell
  picks the `ModuleResourceHarvester` before any `BaseDrill` (each stock drill part also carries a
  `ModuleAsteroidDrill`, which activates and mines nothing with no asteroid aboard), and its
  provocation warp goes through the settle + HIGH-mode helper (a bare `SetRate` in LOW mode
  never packs). The same lane's second cell is the plausibility-cap measurement
  (HARVEST-ROUTE-PLAUSIBILITY-CAP).
- MINE-5's ROUTE half (and MINE-4) is blocked on hardware, not on Parsek: a route's delivery is a
  connection window, produced only by a docking-port or claw couple (`RouteProofCapture`).
  The miner rover has neither, no fixture has a landed Ore depot it could dock to, and no seam
  verb or mission docks two landed vessels (`DriveVessel` throttles straight with no steering;
  the kRPC dock missions are orbital). Needed: a craft pair V builds as he built the rover (the
  miner with a forward docking port or a claw, and a landed depot with a matching port and an Ore
  tank, parked in line within a straight drive), then a lane that mines, drives in, docks,
  transfers, undocks, commits, creates the route (`RouteCommand create`) and dispatches it.

Progress 2026-11-07 (branch `mine4`; MINE-5b LIVE-PROVEN `2026-11-07_0448`): the craft pair is SCRIPTED instead of
built by hand, and MINE-5's route half is lane `MINE-5b-rover-dock-depot-harvest-route`.
- Fixture `miner-rover-depot` (`harness/tools/build_miner_rover_depot.py`, gated by
  `harness/lib/test_miner_rover_depot.py`): `miner-rover-landed` with a `dockingPort3` stacked on
  the miner's free front adapter node (+y, the driving direction; port state Ready, derived
  ids), plus an `Ore Depot` at index 2: the ported miner without its four drills, turned 180
  degrees about its ground normal and placed 1.5 m face to face ahead (root lat / lon / alt and
  `srfRelRotation` derived from the decompiled ProtoVessel placement, committed as literals). The
  miner is heavier with the same vessel type, so it is the dominant side of the dock.
- Seam: `DriveVessel untilDocked=true maxSpeed=<m/s>` drives until stock couples one of the
  vessel's Ready ports (`onPartCouple`), the throttle cut above maxSpeed; no couple in `seconds`
  is ERROR `drivevessel-did-not-dock` (a fast driver INVALID). `PartAction button=transfer
  part= resource= [partPid=]` moves a resource from every other part of the vessel into one
  named part (the stock docked-vessel transfer; total unchanged); `partPid=` picks one of several
  same-named parts on any button.
- The lane: half an hour of mining at 100x, the drive and dock, the transfer, the undock (Ore
  conserved at each step through ReadVesselResources sums), commit, `SealSlot`, `RouteCommand
  create` (gated `eligible=True`, `harvest origin`, `harvestOrigin=1`), `send-once`, a TimeJump,
  and the depot's Ore read above its pre-route amount. First-flight watch items are in the spec
  header (dock geometry, wheel drive direction, the mined amount on 3 EC/s of RTGs, the cap line).
- First flight 2026-11-07 (two attempts, head e1d07eb): drive, dock, transfer and undock all
  worked; the route create refused the tree on the product defect HARVEST-WINDOW-EDGE-FRAME (fixed,
  branch `harvest-gain-gap`). The drills did not slow on 3 EC/s (3.06 Ore is the full rate over the
  ~1740 s window). Re-flight owed on a DLL carrying the fix.

Progress 2026-11-07 (branch `claude/project-thread-1ckuhv`; MINE-4 LIVE-PROVEN `2026-11-07_1209`):
- Fixture `miner-rover-relay` (`harness/tools/build_miner_rover_relay.py`, gated by
  `harness/lib/test_miner_rover_relay.py`): `miner-rover-depot` with a second `dockingPort3` on
  the bottom node of the miner's fuel tank (facing back), the depot's Ore full (150), the
  miner's fuel tank full as ballast (it stays the dominant vessel of every dock), and an empty
  `Ore Silo` (the depot re-identified, same heading as the miner) 4 m behind, ports facing.
- Lane `MINE-4-rover-docked-origin-route-rewind`: the miner docks the depot, loads 75 Ore,
  undocks, backs into the silo, unloads and undocks; the route created from that run is the
  first live mid-tree docked origin (`midTreeOrigin=1`). The same flight carries IR-9 (the
  miner docked back to the silo as a visitor while a cycle delivers) and IR-7 (a
  Rewind-to-Launch of the route's own tree); see INTEGRATION-COVERAGE-LOGISTICS-REWIND-LEDGER.

---

## HARNESS-VERBS-FOR-TIMELINE-OPS: no verb drives F9 while recording, a stock revert, the Re-Fly revert dialog, warp outside flight, or reads a vessel's resources [FILED 2026-10-06 from the coverage-extension research, branch `ccr-77f23eb2-dbqh6i`. OPEN, harness]

- `Quickload` (or `LoadGame allowLiveRecorder=quickload`): LoadGame refuses a live recorder today
  except for Re-Fly (`TestCommandDispatcher.cs:619-643`).
- ~~`Revert target=launch|vab`: no stock-revert verb exists.~~ DONE: `RevertToLaunch` (VN-5)
  drives Revert to Launch, and since 2026-10-31 `RevertToLaunch target=prelaunch` drives Revert to
  VAB / SPH (lane `REV-1-revert-to-vab-in-rewound-timeline`, todo REVERT-EDITOR-SAVE-SWITCH-GAPS).
- ~~`ReFlyRevert choice=... target=launch|prelaunch`: the Esc > Revert dialog during a Re-Fly.~~
  DONE 2026-10-07 (branch `refly-revert-verb`): `ReFlyRevert choice=discard|retry|cancel
  target=launch|prelaunch` presses the stock pause menu's Revert Flight and revert option, then
  the chosen button of Parsek's Re-Fly revert dialog; hlib mirror `ReFlyRevertSourceSyncTests`;
  consumer lane `QL-5-discard-refly-keeps-unfinished-flight` (never flown). A resumed flight
  never offers Revert to VAB / SPH, so `target=prelaunch` answers
  `reflyrevert-option-unavailable` during a separation Re-Fly.
- `RunInvariantReport`: reserved, not implemented (`TestCommandVerbs.cs:343-361`); as a per-step
  read it lifts the one-`RunTests`-per-lane limit for invariant checks.
- ~~`ReadVesselResources pid=|name= [expect=...]`: per-vessel resource totals and a part-uid digest
  in FLIGHT, KSC and TS.~~ DONE 2026-10-16 (branch `claude/project-thread-81f9wc`):
  `ReadVesselResources pid=|rec= [resources=] [expect=] [tolerance=] [expectSnapshot=]` in
  FLIGHT, KSC and TS; `rec=` reads the vessel a committed recording spawned, sums the targets
  against `expect=` (conservation across vessels), compares each with its recording's end
  snapshot, and counts part uids also held by another live vessel. A mismatch is an OK answer
  with `match=false` (gated by `forbidden`). hlib mirror `ReadVesselResourcesSourceSyncTests`;
  consumer lane `RC-1-docked-transfer-rewind-tanks`.
- ~~Scene-agnostic `WarpToUT` (FLIGHT only today, `ParsekTestCommandAddon.WarpToUT.cs:50`).~~
  DONE 2026-10-07 (branch `scene-wait`): `RequiresWarpScene` (FLIGHT, Space Center, Tracking
  Station), the post-load physics hold a bounded defer; todo
  HARNESS-FAST-POLLS-SHRINK-INERT-STEP-WAITS.

Fix: one verb per PR with its hlib source-sync cell, in the order the roadmap register needs them.

---

## HARNESS-RESOURCE-ORACLE-AND-INVARIANT-RULES: no check anywhere for resource conservation, crew conservation or vessel identity [FILED 2026-10-06 from the coverage-extension research, branch `ccr-77f23eb2-dbqh6i`. OPEN, harness]

The analyzer's INV1-INV12 include no crew, vessel-identity or resource-conservation rule;
`saveparse` reads vessel name / type / pid only; `EndResources` is not a reliable source (not
refreshed when the commit re-snapshots; absent on background leaves), so an oracle must decode the
snapshot sidecars (`PSN0` + 25-byte header + raw deflate).

Fix: `harness/lib/resourceq.py` + an `[expectations.world.vessels]` block (V1 no part uid on two
live vessels; V2 a chain-tip vessel equals its snapshot plus the route rows addressed to it after
the spawn; V3 dock-window conservation with a declared direction; V4 per-vessel windows); an
in-game `ResourceConservation` category for V1 / V2; analyzer INV13 crew conservation and INV14
vessel identity, baselined over every fixture first, then gated.

Progress 2026-10-16 (branch `claude/project-thread-81f9wc`): `harness/lib/resourceq.py` exists
(snapshot sidecar decode, `ROUTE_CONNECTION_WINDOWS` reader, per-window conservation, cells in
`test_resourceq.py` over `bdock-recorded`), and V1 / V2 / V3 have a LIVE form through the
`ReadVesselResources` verb (`uidCollisions`, `expectSnapshot`, a summed `expect=`), first used by
RC-1. Still open: the `[expectations.world.vessels]` saveParse block, the in-game category, V2's
route-row term, V4, and INV13 / INV14.

---

## HARNESS-TIMELINE-FUZZERS: no test applies random sequences of timeline operations [FILED 2026-10-06 from the coverage-extension research, branch `ccr-77f23eb2-dbqh6i`. OPEN, harness / tests]

Fix: ~~(1) a load-path x reconciler wiring gate in xUnit (every load path against every state it
must reconcile, so a new path or state category cannot be added without a decision)~~ DONE
2026-10-06 (PR #2021, roadmap TA-W): `LoadReconcilePolicy` (load kinds, state
categories, today's decision per cell with the filed defects as known gaps) gated by
`LoadReconcilePolicyTests` and `LoadReconcileWiringGateTests`; (2) extend
`LedgerStateFuzzerTests` / `EffectiveStateGraphFuzzerTests` with commit, supersede, cutoff,
F9-backward, revert-prune and discard, and add `RouteTimelineConservationFuzzerTests` (the route
half DONE 2026-10-31 as `RouteTimelineFuzzerTests`, branch `claude/project-thread-q5tjfx`: seeded
warp steps, saves, Re-Fly starts and Discard Re-fly loads against an independent loop-clock model;
counters, cursor and tank match the model, cargo equals the start fill plus one manifest per
delivered row, no cycle id twice; reds all 12 seeds when the rewind loop-position restore is
removed; the ledger half stays open); (3) the
FZ-1 lane: a seeded generator of declarative step lists over the operation alphabet, invariants
after each step, oracles at the end, seed and step index printed for exact replay.

Item 2, ledger half: DONE 2026-10-31 (branch `claude/project-thread-3mnti5`, test-gap audit
2026-10-31 C2). `ReFlyValueFuzzerTests` builds seeded random careers one operation at a time
(forward and rewound flights, contracts with and without deadlines, purchases at a rewound clock,
repairs, strategies, records, Re-Fly with gate, live session, Discard or Merge through
`ReFlyMergeDominance.Evaluate` and the real `CommitTombstones`, chained re-flies) and checks the
rewind design section 2.12 rules after every step: an open slot holds no counted non-loss row
and retiring it lowers nothing; a passed merge lowers no balance at any UT, keeps every
affordable purchase affordable, retires only losses or not-counted rows and keeps every credit
(milestone, contract, record threshold, science subject) at the same or an earlier UT; Discard
restores the ledger exactly; the earliest milestone / contract outcome wins; a forward flight
takes no credit back. It also checks that the live cached gate and the merge's retire set agree
with the pure parts, and that the isolated walk ends where the live recalculation does. A
5,000-career sweep (6,661 merges, 2,527 discards, 161 refusals) found no violation; planted bugs
(a milestone that does not seal, a dominance check that always passes, Variant B written as A,
a milestone credited to its latest row) each red it. Still open: the effective-state alphabet,
route conservation, and the FZ-1 lane.

---

## INTEGRATION-COVERAGE-LOGISTICS-REWIND-LEDGER: supply routes are tested almost only in isolation [FILED 2026-10-06 from the integration-coverage research, branch `ccr-77f23eb2-dbqh6i`. OPEN; test program]

Research: `docs/dev/research/integration-coverage-gaps-2026-10-06.md`. Of the 54 specs that
drive `RouteCommand`, none also drives a real Re-Fly, real warp, Real Spawn Control,
`MissionConfig` or a KSC action; every live dispatch is a Kerbin surface rover after a seam
`TimeJump`; no inter-body or moon route has dispatched live; the ledger oracle is never run on
a route or a rewind lane. The code read behind the research found the five defects filed
below, all in that untested space.

Fix: the phased program in the research doc - Phase A headless red tests for the confirmed
defects, then the harness capabilities (route-aware ledger oracle that survives a rewind, a
mid-run route read-back, career route fixtures with a rewind handle, a dispatchable moon
route), then the IR-1..IR-11 pair lanes and the IC-1..IC-4 campaign lanes.

Progress: Phase A rows A2 (ROUTE-ENDPOINT-CHAIN-GHOST-PROXIMITY-REBIND) and A5
(ROUTE-DELIVERY-INTO-DOCKED-VISITOR) landed with their fixes on `fix-route-endpoint-writes`.

Checked 2026-11-06 (branch `routecons`): `H58-route-rewind-to-launch` is NOT IR-7. It rewinds a
tree the lane records itself (`InvokeRewindToLaunch tree=latest`), never the route's backing tree
`73e50f1e`, which has no rewind save (C4, ROUTE-REWIND-TO-LAUNCH-UNREACHABLE-ON-COMMITTED-FIXTURES).
IR-9 is not reachable either: no seam verb docks two vessels, and no committed route host has a
visitor docked at a route endpoint. [Superseded 2026-11-07: `DriveVessel untilDocked=true` docks
two landed vessels, and both lanes are authored inside `MINE-4-rover-docked-origin-route-rewind`
(branch `claude/project-thread-1ckuhv`, LIVE-PROVEN `2026-11-07_1209`) on the scripted `miner-rover-relay`: the lane
records the route's tree itself, so IR-7 rewinds to its own launch quicksave, and IR-9's visitor is
the miner docked back to the route's silo. The harvest described next is no longer needed.] It needs a harvested fixture: a route host (sealed, route
candidate) whose destination has a SECOND vessel, not a route transport, hard-docked to it at
save time, with known fill on both, so a lane can create the route, deliver, and read the visitor
unchanged and the endpoint's own parts credited. Docking a visitor rover onto `rover-route-recorded`'s
`rover fuel 0` (its one `dockingPort2`, `Ready` at save time) by hand and harvesting is the shortest path.

---

## HARNESS-LEDGER-ORACLE-ROUTES-AND-REWIND: the ledger oracle knows no route actions and refuses every rewind lane [FILED 2026-10-06 for the logistics integration program (C1), branch `ccr-77f23eb2-dbqh6i`. OPEN, harness]

`harness/lib/oracle.py` models no route action types, and `hlib.validate_spec` refuses an
`[expectations.ledger]` block on any lane that rewinds (`InvokeRewind`, `InvokeRewindToLaunch`;
the L4 deferral, `hlib.py` around the ledger-expectation validator). Route funds are therefore
pinned only by log tokens (RVR-4 `DispatchDebit ... cost=7410`), and no lane can assert that a
rewind retired exactly the route rows after its cutoff.

Fix: add the route action types (dispatch debit, recovery credit, delivery, pickup) to the
oracle's expected model, and a rewind-cutoff model (rows strictly after the cutoff retired,
dormant routes, re-delivery exactly once) so route lanes can carry a gating ledger block.
Roadmap: "The logistics integration program", row C1.

Rewind half DONE 2026-10-17 (branch `claude/career-rewind-ledger-tlmab9`, TC-4): a ledger block
that declares `timeline = "rewind-aware"` may pair with `InvokeRewind`, `InvokeRewindToLaunch`
and `AnswerMergeDialog`. `oracle.compute_expected_rewind_aware` places every entry against the
produced save's clock (careerSave `universalTime`, analyzer version 5) and expects the design 3.9
AVAILABLE funds and science (running balance floored by every projected future balance, earnings
before spendings at one UT) and the running reputation; `attempt` + `retiredAttempts` drop rows
a Re-Fly tombstone retired, `onceKey` credits a milestone once. A missing clock is
INVALID(tooling), a window straddling the clock a hard `timeline-unplaceable` divergence. Cells:
`test_oracle.RewindAwareTimelineTests`, `test_ledger_timeline.py`, `LedgerGroundTruthParserTests`,
`ReportWriterTests`. Lane `L8-career-repeated-rewinds` (three Rewind-to-Launch on a career with
KSC spending, two recovered flights, LedgerGroundTruth after the third rewind) is authored; its
first reading run (`2026-10-17_0632`) was INVALID on driver gaps, now fixed: `InvokeRewindToLaunch`
runs from the Space Center as well as FLIGHT (`VerbSceneRequirement.RequiresRewindScene`, deferring
`not-in-rewind-scene` elsewhere), a `KscAction upgrade-facility` that stock did not apply reads
`upgrade-not-applied` and only a refusal by Parsek's guard reads `blocked-committed`
(`FacilityUpgradePatch.BlockedUpgradeCount`), and the lane commits each pad flight before
recovering it. The second flight (`2026-10-17_0700`) found HIRE-RECALC-BEFORE-STOCK-DEBIT; the
third, `2026-10-17_0930`, PASSED attempt 1 with the produced pools equal to the oracle's
(funds 216769, science 90, rep 0 at end clock 199.87). Still open: the route action types.
Update 2026-10-21 (LOGISTICS-WITHOUT-MONEY phase 2, branch `logistics-no-money-p2`): route events
are no longer ledger rows and routes move no funds, so the route half has nothing left to model in
the ledger oracle; a route-cutoff check would read the route event log (`ROUTE_EVENT_LOG`) instead.
Left open for the operator to close or re-scope.

---

## FIXTURE-CAREER-ROUTE-WITH-REWIND-POINTS: no career save carries both a route and Rewind Points [FILED 2026-10-06 for the logistics integration program (C3), branch `ccr-77f23eb2-dbqh6i`. OPEN, fixture]

`bdock-recorded` is the only committed host with a route window and Rewind Points (station tree
788554a9 with `rp_72eb...`; interceptor tree 8c677bba with the dock window and 2 RPs), but it is
SANDBOX, so funds branches are vacuous on it. IR-3 / IR-4 / IC-2 need the same shape on a career
save.

Fix: a career twin built the way `harness/tools/build_rover_route_career.py` builds
`rover-route-career`, with the route created and sealed (SealSlot 8c677bba, RouteCommand
create) and committed as a fixture.

---

## FIXTURE-MOON-AND-INTERBODY-ROUTE-DISPATCH: no moon or inter-body route has ever dispatched live [FILED 2026-10-06 for the logistics integration program (C5), branch `ccr-77f23eb2-dbqh6i`. OPEN, fixture / lanes]

Every live dispatch is a Kerbin surface rover. `interbody-route-recorded` (sandbox, UT 87.6M)
carries a Paused KSC -> Mun route (transit 85,354 s) and an Active KSC -> Duna re-aim route
(transit 8.53 Ms) but they are only rendered (B32, V26M, V26T) or photographed (GUI-3, GUI-25).
No fixture has a route at Minmus, Ike or a Jool moon, and there is no career inter-body route.

Fix: lane IR-5 first, AUTHORED 2026-10-16 as `IR-5-mun-route-dispatch-delivery` (branch
`claude/lanes-rc6-ql6-ir5-nh7w10`), LIVE-PROVEN `2026-10-16_0652` PASS attempt 1: the Mun station
took LiquidFuel 281.959 and Oxidizer 344.617 on the unloaded path and the route paused
`delivered-then-paused`. Reading before the flight: send-once does not
rebase the loop clock, so the first tick after it owes a long-past dock crossing and fires cycle-0
at once (no day-long jump is needed); the Mun station has less room than the manifest, so the lane
lowers its two tanks with the new `[[fixture.liveState]] partResources` form (the per-vessel form
refuses a multi-tank vessel by design). Then harvest a Minmus depot route and a Duna or Ike station route, and a career twin of
the inter-body fixture, for IC-1 / IC-3.

---

## LANE-PROMOTION-FOLLOWUPS-2026-10-06: what the four promoted lanes and GUI-2 still owe [FILED 2026-10-06 from the spec-logic review that promoted RR-1, EVA-9, EVA-10 and GUI-1, branch `ccr-77f23eb2-dbqh6i`. OPEN, harness]

- [ ] GUI-2-census-flight: one re-fly of the committed spec (its newest flight
  `2026-09-22_1844` predates `screenResolution = "1920x1080"` and two window-table changes);
  then promote it.
- [ ] EVA-9: a confirmation flight on plain `main` (it passed only on the integration DLL, and
  its last spec commit postdates that pass).
- [ ] EVA-10: one flight of the armed spec (the 17..17 count and the three twin-lander forbids
  were armed offline off `2026-09-29_2116`).
- [ ] GUI-1: pin `changed=1` on the `expand key=row:nearmiss:group:MissingRouteProof` step after
  checking it against the `2026-10-04_1403` KSP.log.

---

## TODO-DOC-ANOMALY-TABLE-IN-FROZEN-ARCHIVE: a harness guard now reads a table inside an archived todo volume [FILED 2026-10-06 from the PR #2018 review, branch `ccr-77f23eb2-dbqh6i`. OPEN, low]

`test_hlib.test_todo_doc_table_lists_every_raised_reason` pins that every raised anomaly reason
has a row in the ground-truth table of the struck entry "The harness anomaly token set has
drifted"; the v9 rotation moved that entry to `done/todo-and-known-bugs-v9.md`, so `TODO_DOC`
now points at the archive. A future reason must be added to a FROZEN file to keep the cell
green, and the next rotation must remember to carry it.

Fix: move the table into a living doc (the harness-core design or `autotest-status.md`) and
point the cell there.

---

## MUTATION-CHECK-RESIDUE: what the mutation checker still leaves to the operator [FILED 2026-10-01 when MUTATION-CHECK-PHASE-2 closed, branch `mutation-phase2-pr3`. OPEN; harness, spec rulings]

- The phase 1 triage survivors (moved from MUTATION-CHECK-PHASE-2): the 723 triage
  survivors (96 lanes) listed by group in known-gate 17 (spec tightening; each group is a
  spec change or a recorded ruling). FIRST PASS DONE 2026-09-24 (branch
  `tighten-survivors`): 26 specs tightened, 723 -> 700, of which 391 are recorded as
  intended and 309 remain (known-gate 17 lists them). Still owed from the pass: the
  teardown `Recording stopped` fix on the 25 lanes it could not verify offline - B4 now
  that #1806 (`b4-chute`) has landed, and the 24 whose archives are missing or no longer
  replay green (re-run `mutation_check.py` after their next tier, then apply the same
  per-lane anchor).
- ~~One VACUOUS mission gate needs a ruling~~ RULED AND APPLIED (operator ruling
  2026-10-01, branch `ca1-chute-telemetry`): `CA-1-commit-abort-booster-live`'s
  `craftChuteNeverArmed` (mission `gs1_auto_chute_booster`, abort profile) was met by the
  blind replay because it read only the machine's own command latch. It now also reads the
  upper stage's REAL parachute state (the shell's existing `read_chute=True`
  `craft_chute_state`) on every DESCENT frame attributable to the upper stage (vessel name
  read and not `siblingVesselName`, so the booster's open chutes after the impact handoff
  are excluded), latches any state but Stowed, and needs at least one such read (unread
  fails closed): `mlib.GS1_UPPER_CHUTE_MIN_READS` / `_gs1_observe_upper_chute`.
  `mutation_check.py --mission-only` over archive `2026-09-26_0143`: 19 PROVEN, 0 VACUOUS
  (was 18 / 1). Re-read flight `2026-10-02_1906` PASS: the row met with value `Stowed`,
  `upperStageChuteReads=25`, `observedFired=false` and `unattributedChuteReads=1` (the
  handoff frame).
- Ledger lanes with no seed-bearing archive on this machine
  (`L2-ledger-groundtruth-career`, `L3-strategy-currency-conversion`,
  `L3-strategy-exchanger-floor`): re-run `mutation_check.py --ledger-only` after their
  next tier.
- A threshold pushed across its bound INSIDE a mission evaluator is not reached: the
  telemetry frames the evaluators read are not archived (the mission stdout is
  rate-limited), so the blind replay proves an assertion reads telemetry, not which side
  of its bound it compares. Reaching it needs the mission to archive its frames (or the
  per-assertion evidence the compare reads).

## HARNESS-POST-FLIGHT-DIALOG-STALLS: harness runs sit behind KSP's post-flight screens until a timeout or the wall budget ends [FILED 2026-10-03, branch `todo-stuck-dialogs`. CONFIRMED in the collected logs; observability done on branch `post-flight-dialog-logging`; the paused-clock watchdog, the seam active-vessel-loss fail-fast and the CommitTree fast reject FIXED 2026-10-03, branch `harness-dialog-stalls`; the frozen-telemetry item FIXED 2026-10-04, branch `frozen-detector-fix`; the RB-1 chute-read item FIXED 2026-10-06, branch `harness-overhead`]

Operator observation: auto tests sometimes sit on the post-flight "Mission Summary" screen.
A scan of all 894 collected `_shots/KSP.log` files found two stock dialogs, neither of which
Parsek, `run.py` or the seam detects or dismisses (the old FlightResults patch is gone;
`UiAction op=dismiss` reaches only Parsek popups; `mission_runner.py` `_warp_watchdog`
clears a pause only while a warp is running). In every case something else held the run.

- **Crash screen (`FlightResultsDialog`, "Outcome: Catastrophic Failure!") pauses the game.**
  Stock `FlightLogger` opens it about 60 s after the active vessel's root part is destroyed.
  It calls `FlightDriver.SetPause(true)`, so `timeScale` is 0. KSP.log marker:
  `Game State Saved to saves/<x>/persistent` followed within about 10 ms by `Game Paused!`.
  The seam's `StockScreen` VAB open makes the same pair but unpauses within 0.1 s; exclude it.
  - RB-1 `2026-09-27_1353` (PARSEK-FAIL batch-crashed): the Flea crashed on descent, which
    the lane never intends. `Game Paused!` at 16:56:51, `Game Unpaused!` at 17:15:41: about
    1,130 s on the crash screen, 1,337 s wall against 410-422 s for passing runs. Nothing
    tripped earlier because every phase budget is game time and `mlib` `advances_frozen`
    deliberately ignores a stopped clock, so the mission polled frozen telemetry until its
    wall budget ran out.
  - EVA-8 x6 (2026-09-29, INVALID): Jeb died just after a seam `evagroundscience step move`.
    The pending two-phase step waited out its 120 s `step-timeout`, about half of it with the
    crash screen up. This was a lane-development defect; the lane passes since `_1819`.
- **KSC recovery screen (`MissionRecoveryDialog`, "Mission Summary for <vessel>").**
  The marker is the stock `[VesselRecovery]: ... recovered` line. 22 runs in 9 lanes, median
  1.7 s to the next scene. In 7 of them (L3 x5, L5, L6 x2) it stayed about 62 s, while the
  post-mission `CommitTree` was deferred `not-in-flight` until its 60 s timeout. That timeout
  is measured and non-gating by design in `L3-career-science-recover.toml`; the dialog is a
  bystander.

Lost: about 26 min with a dialog up, about 25 min of it avoidable (RB-1 is about 19 min).

- [x] Mission shell: an always-on paused-clock watchdog. If game time has not advanced for
  about 15 wall s while `krpc.paused` is true, with no seam step in flight, end the mission
  as vessel lost rather than waiting out the wall budget. This would have saved about
  1,100 s on RB-1.
  Fix: pure `mlib.paused_clock_step` (one debounced wall window: an unbroken run of polls
  with UT unchanged AND `KRPC.Paused` true lasting `PAUSED_CLOCK_WALL_SECONDS = 15`), fed
  every poll by `mission_runner._fly_loop_body`. A trip ends the mission through
  `mlib.paused_clock_terminal`: `MISSION-ASSERT-FAIL` with a `vessel-lost (paused-clock:
  ...)` reason, the class every machine's own vessel-lost terminal uses (INVALID(mission),
  retried once). A state with no `loss_reason` field (M3 only) gets a named `MISSION-FLAKE`
  so its frozen telemetry is never graded. Exempt, re-arming the watch: a running native
  warp (`_warp_watchdog` owns pauses then), the poll after any seam action (perform()
  blocks for the whole step), a machine state declaring `game_pause_owned` (no mission
  pauses the game; grep found no pause writer but `_warp_watchdog`'s unpause), an unread
  pause state, a non-finite UT. Not scoped to airborne phases: the seam exemption already
  keeps scene-straddling verbs out, and a pause this long is a stall in any phase. The pause
  RPC is issued only on a poll whose UT did not move. Log: `gate paused-clock armed` /
  `cleared` at Info, `gate paused-clock TRIP | frozenUt= pausedFor= limit=` at Warn. On
  RB-1 it would have tripped about 15.5 s after `Game Paused!` (16:57:07 instead of the
  wall reaper at about 17:15). Tests: `test_mlib.PausedClockWatchdogTests`,
  `test_shells.PausedClockWatchdogShellTests` / `KrpcPausedClockSeamTests`.
- [x] RB-1 also shows the frozen-telemetry detector NOT firing: from the crash (UT 98.72)
  to the pause (UT 157.86) UT advanced at 1x for about 60 s while the logged altitude,
  vertical speed, apoapsis and periapsis stayed identical to 3 decimals, yet B1's
  10-sample limit (inside `sbr_decide`) never tripped. Replaying the logged 3-decimal
  values trips at sample 11 and the sibling RB-2 `2026-09-27_1417` (same code) tripped
  live, so a sub-millimetre difference in one of the four fields kept resetting the count.
  Fix: three changes to the shared `mlib._advance_frozen_count`. (1) A poll whose UT did
  not strictly advance HOLDS the count, as a warp frame already did: a repeated or
  non-finite UT keeps the signature, a lower UT (load, rewind) re-baselines it. A stopped
  clock still never increments, so the paused-clock watchdog keeps sole ownership of a
  pause. (2) The airborne hop machines compare the four fields within a tolerance: new
  missionParams key `frozenToleranceAbs`, defaulting to `HOP_FROZEN_TOLERANCE_ABS = 1e-3`
  in the B1 (and so SBR), EVA-4, GS-1 and B4 params builders, NaN never matching; B4
  applies it only in REENTRY / SPLASHDOWN (`B4_FROZEN_TOLERANCE_PHASES`). Every orbit
  machine (B2 / R1, B5 / V1, B-DOCK, FORGE-LKO, KXRW) keeps the exact compare: near an
  apsis of a near-circular orbit a live craft can sit inside 1e-3 per poll. (3)
  Observability: the machine line and status dict now read the counter off a delegated
  sub-machine (`frozenCount=` was `-` on SBR, R1, V1 and S/R/T-DOCK), and the fly loop logs
  one `frozen-telemetry reset count=N->0 field=<f> <f>=<prev repr>-><curr repr> dUt= warp=
  tol=` line when a run of 1 or more breaks, plus a `frozen-telemetry within-tolerance`
  line when a tolerant machine counts a poll that was not bit-identical (capped at
  `FROZEN_NOTE_LIMIT = 20` per flight; silent on a healthy flight). The jittering field is
  still UNPROVEN: the logs print 3 decimals. The next crash names it in those lines.
  Tests: `test_frozen_detector.py`.
- [x] ~~RB-1's chute channel read nothing useful: of the 1,261 telemetry lines, 1,254 print
  `chute=-` and 7 `chute=Stowed`, and the Flea hit at about 230 m/s.~~ Cause: the fixture made
  kRPC read a clone's parts (fixed by 5ab868f0a); the arm logged `on 0 parachute(s)` and the
  chute never armed. Fixed 2026-10-06, branch `harness-overhead`: B1 / SBR / EVA-4 end the
  mission before the arm when the chute read returns nothing on the DESCENT-entry poll and
  the one before it (`mlib.chute_unobservable_at_descent`, `MISSION-ASSERT-FAIL` reason
  `chute unobservable (part identity?)`, INVALID(mission)), and `deploy_chute` logs
  `deployed N parachute(s)`. Over the 26 collected flights of these lanes it trips on RB-1
  and both RB-2 attempts (same defect) and on no other.
- [x] Seam: a two-phase pending step fails fast when the active vessel is destroyed or
  `FlightResultsDialog.isDisplaying`, instead of waiting its full timeout
  (`ParsekTestCommandAddon.EvaGroundScience.cs` step-move path first). Also log the
  dialog's display and the active-vessel loss so a scan can find the next case.
  Fix: one shared guard on the two-phase completion poll
  (`ParsekTestCommandAddon.ActiveVesselLoss.cs`, pure decision
  `TestCommandActiveVesselLoss`). A watched verb (`EvaGroundScience`, `PlantFlag`) captures
  the active vessel when it goes PENDING; each completion poll checks it before the verb's
  own completion and ends the step `ERROR active-vessel-lost` with one Warn line
  (`active-vessel lost id= cmd= vessel= pid= state=dead|destroyed elapsed=`). Lost means
  Unity-null OR `Vessel.State.DEAD`: decompiled `Vessel.Die` leaves the dead ACTIVE vessel in
  place, which is why the old `kerbal == null` check never fired and each EVA-8 run sat out
  the full 120 s. The kerbal's death precedes the crash dialog by about 60 s, so no dialog
  check is needed to fail fast; logging the dialog belongs to the observability item below
  and to game-side PR #1992. EvaChuteDeploy keeps its own debounced `eva-chute-kerbal-lost`;
  EvaBoard / EvaExit / scene-leaving verbs / WarpToUT are not watched. Saves about 120 s on
  each of the six EVA-8 runs. Tests: `TestCommandActiveVesselLossTests`.
- [x] Recovery-screen class: `CommitTree` returns a fast `REJECTED not-in-flight` in
  SPACECENTER instead of deferring 60 s, or L3 / L5 / L6 drop that step. L3 keeps the step
  verbatim as a tripwire, so its `expect` must change with it. Saves about 60 s on each of
  those runs (SE-1 hits the same deferral).
  Fix: option (a). `TestCommandDispatcher.RejectOutsideFlightVerbs` = {`CommitTree`}: in a
  settled non-FLIGHT scene it answers `REJECTED not-in-flight` at once; every other FLIGHT
  verb keeps its defer. A pending scene load still defers `not-safe-point` above the scene
  check (the transition flag is set synchronously by any load request, kRPC included), and
  the FIFO head blocks every other seam verb, so no deferral could bridge into FLIGHT. Caller
  audit: 38 specs drive CommitTree as a step (previous step StopRecording x20, the mission
  phase x15, WarpToUT x3) plus in-mission seam calls from the mission shells; across all 217
  collected runs that sent it, 213 dispatched straight to OK with no defer and all 10 that
  deferred `not-in-flight` ended TIMEOUT (L3 x5, L5, L6 x2, SE-1 x2), none executing after a
  defer. L3 / L5 / both L6 lanes now expect `REJECTED`. SE-1 needs no change: its two
  deferrals came after an upstream `LaunchFromEditor` failure left the run outside FLIGHT,
  which now rejects fast too. Tests: `TestCommandDispatchTests.CommitTree_*`.
- [x] Seam budgets that only ever paid out on failure: `EvaGroundScience` 120 s -> 60 s and
  `AnswerMergeDialog` 120 s -> 60 s (2026-10-06, branch `seam-timeout-budgets`). Measured
  over 859 collected runs (2026-09-10 to 2026-10-05): EvaGroundScience OK n=412, p99 5.5 s,
  max 5.5 s, its 37 timeouts all at the full 120 s (4,440 s); AnswerMergeDialog OK n=65,
  p99 6.6 s, max 7.2 s, its 6 non-OK outcomes all at 120 s. Rule: max(4 x OK max, 30 s),
  except EvaGroundScience at 60 s because its place ladder counts frames (about 1,000
  worst case, inside 60 s down to ~17 fps); AnswerMergeDialog's floor is twice its 30 s re-fly resume-settle fallback. The hlib
  `DISPATCH_DEFERRAL_BUDGET_SECONDS` mirror moved with them. Other verbs whose failures
  all sat at the budget while OK max was under a quarter of it: `StopRecording` (2 at the
  60 s default, OK max 0.1 s) and `TrackingStationRecover` (2 at 120 s, OK max 3.6 s, only 5
  OK samples); left unchanged (the default bounds a scene-settle defer for every verb, and
  5 samples are too few). `CommitTree` (10 at 60 s) is already fixed above. Tests:
  `TestCommandDeferralBudgetTests.MeasuredBudgets_ArePinned`.
- [x] ~~Observability: subscribe to `onGUIRecoveryDialogSpawn` / `Despawn` and log the crash
  dialog. Neither dialog logs its own close today, so a stall longer than about 62 s on the
  recovery screen cannot be confirmed from logs.~~ Fix: `PostFlightDialogLog` (pure state and
  formatting) writes four Info lines under the `[PostFlightDialog]` subsystem, each with the
  scene and the vessel name in quotes (`"?"` when unknown):
  `FlightResultsDialog shown: scene=FLIGHT vessel="..." paused=true exitControls=true outcome="..."`,
  `FlightResultsDialog dismissed: scene=... vessel="..." via=Close|Destroyed onScreenWallSeconds=N.NN paused=...`,
  `MissionRecoveryDialog shown: scene=SPACECENTER vessel="..."` and
  `MissionRecoveryDialog dismissed: scene=... vessel="..." onScreenWallSeconds=N.NN` (`unknown`
  for a dialog that opened before the subscription). Wall seconds come from
  `Time.realtimeSinceStartup` and format culture-invariant. `exitControls=true` is the crash
  screen; `false` is the F3 flight status screen, which is the same stock dialog. The flight
  results feed is three Harmony postfixes (`Patches/FlightResultsDialogLogPatches.cs` on
  `Display(string)`, `Close()` and the private `OnDestroy()`, since that dialog has no
  GameEvent); the recovery feed is `PostFlightDialogLogHost`, a process-lifetime addon on
  stock `onGUIRecoveryDialogSpawn` / `onGUIRecoveryDialogDespawn` plus
  `onVesselRecoveryProcessing` for the vessel name (stock fires the spawn inside the dialog's
  Awake, before the name is set). No harness cell reads these lines yet.

## HARNESS-FLIGHT-WALL-TIME: auto-flights spend ~40% of their mission wall time idle at 1x [FILED 2026-10-01, branch `flight-efficiency`. MEASURED; the warp-policy fix is a separate session]

MEASURED 2026-10-12/13 (daily + operator tiers on main `7cbe594c0`, 181 lanes, 189 attempt runs):
12.1 h of wall. Longest: B20 4,655 s, B21 3,045 s, B19 2,950 s, V24W 2,868 s, B22 2,247 s, B30
1,763 s, B16 1,536 s, BDOCK-2 1,299 s. Daily lanes are boot-bound (kspBoot ~24 s mean, KSPCF
FastLoader and the ModuleManager cache already active; preLaunch ~6 s, verifiers ~8 s). Operator
mission wall 34,927 s: idle at 1x 9,895 s, recoverable 4,640 s (low-warp 2,144 s, of which B20's
coast 1,315 s; waiting-for-node 1,963 s, of which the B19 / B20 / B21 park round-out ~1,773 s,
tried and dropped below), by design 6,064 s.

`harness/tools/flight_efficiency.py` (pure core `harness/lib/flighteff.py`, contract in its
docstring and `harness/README.md` "Flight efficiency") now measures where a run's wall time
goes. Over the 2026-10-01 nightly runs 39% of mission wall was 1x with a static orbit outside
the atmosphere; over all 165 mission runs in every worktree's results, 38%. The largest
recoverable causes: MechJeb NodeExecutor node waits (B11 CAPTURE-BURN ~600 s, TRANSFER-BURN
30-100 s on most B lanes, BDOCK RENDEZVOUS; with autowarp on, the executor warps only to
600 s before ignition and then holds 1x until the craft is within 1 deg AND turning slower
than 0.001 rad/s, decompiled MechJeb2 2.15.1 `StateWarpAlign` / `AlignedAndSettled`), the
1x PARK dwells (`parkDwellSeconds` of game time, ~170 s each, 1x by design so the recording
carries parked coverage), B4's DEORBIT retrograde slew at 1x
(~340 s per run; `apErr` is the autopilot pointing error, so rails warp cannot help it), and
the m3 HOLD-* observation windows (deliberate). The mission warp policy itself is being
changed in a separate session; re-run the tool over the next nightly to measure that fix.

- [x] Re-measure after the warp-policy change lands (`--since <date> --json-out`). Measured
  2026-10-03 with the policy-aware analyzer (schema 2: `physicsWarp` bucket, `byDesign`
  totals, 4x targets for PARK / kx COAST). #1958 (capture holds, PARK / COAST 4x physics
  warp): B11 mission wall 1,275 s (`2026-10-01_1231`) -> 668 s (`2026-10-01_1926`);
  recoverable 618 s -> 14 s, with PARK now ~42 s of physics warp and 0 recoverable and the
  capture wait's last ~133 s before the node by design. #1964 (BDOCK rendezvous holds):
  BDOCK-1 mission wall 2,115 s (`2026-09-30_1732`) -> 1,487 s (`2026-10-02_2011`),
  RENDEZVOUS recoverable 1,178 s -> 329 s; BDOCK-2 1,475 s (`2026-09-23_1704`) -> 1,022 s
  (`2026-10-02_2037`). Over every worktree's results (845 runs, 197 with a mission log) the
  old analyzer reported 30,196 s recoverable; the policy-aware one reports 18,712 s
  recoverable plus 9,978 s by design (DEORBIT slew 2,266 s, the HOLD-* dwells 2,611 s,
  node waits 5,100 s). The largest recoverable rows are now pre-#1964 BDOCK RENDEZVOUS
  waits, B22's outcome-sensitive CIRCULARIZE node wait (~590 s per run, policy undecided)
  and pre-#1958 CAPTURE-BURN waits.
- [x] B22's CIRCULARIZE node wait: owner ruling 2026-10-03 keeps it recoverable and
  outcome-sensitive in the analyzer. Backlog target: a verification flight before any warp
  there (~590 s per run). Now a hold behind the default-off b5 key `circularizeNodeWaitWarp`
  (branch `b5-circ-node-wait`, stacked on `coast-native-lead`): the CAPTURE node-wait hold
  (`_b5_node_wait_begin` / `_b5_node_wait_step`) extended to the park round-out hand-off,
  declined when the executor is not observed idle, the park is rails-illegal, or the warp
  budget is spent; transfer nodes stay un-held. Only B22 opts in. The analyzer counts the
  wait inside the lead (120 s + 5 s + half burn) as by design only in a visit whose own
  `node-wait:` line printed its half burn; the rest stays recoverable and outcome-sensitive.
  Re-fly proof 2026-10-03_1157 (with the coast lead): the hold warped to ut 2564.7 (node
  2687.2, half burn 2.5 s) and released on arrival; trim ap 769,667 m (unheld runs 769,661 /
  769,650); CIRCULARIZE wall 626 s -> 108 s; mission wall 2,767 s (2026-10-01_2235) ->
  2,156 s; same 20-phase list, corrections 124.10 / 73.25 m/s (unheld 124.10 / 72.73-72.81),
  Jool park ap 584,389 km (unheld 584,339 / 584,320 km, about 0.01%).
- [x] BDOCK decisions (owner ruling 2026-10-03, resolved in the analyzer). STATION-ASCENT /
  INT-ASCENT node waits are by design (the same MechJeb ascent as MJ-ASCENT; BDOCK engages
  `ACTION_MJ_ENGAGE_ASCENT` on both legs). The RENDEZVOUS wait is by design from the hold's
  warp target minus the 15 s early cancel (`RV_WARP_ARRIVAL_TOLERANCE_SECONDS`), the target
  read from the hold's own `warp_to_ut` line for that node (a ca-clamped target is already
  earlier); a declined hold uses its `node-wait:` text, a pre-hold run the constants
  (nodeUt - 135 s). BDOCK-1 `2026-10-02_2011`: RENDEZVOUS recoverable 329 s -> 0 (329 s by
  design), STATION-ASCENT 140 s and INT-ASCENT 87 s -> by design, run recoverable 595 s ->
  37 s (DOCK coasts). Pre-#1964 `2026-09-30_1732`: RENDEZVOUS 1,178 s -> 874 s recoverable
  (284 s by design: the last 135 s before each of four nodes). Every worktree's results:
  recoverable 18,712 s -> 14,450 s, by design 9,996 s -> 14,141 s; the top recoverable rows
  are now the pre-#1964 BDOCK-1 / BDOCK-2 RENDEZVOUS waits (3,499 s / 1,338 s), B22
  CIRCULARIZE (1,183 s) and pre-#1958 CAPTURE-BURN waits.
- [x] Coast native lead (branch `coast-native-lead`): two default-off b5 `missionParams`.
  `soiNativeLeadSeconds` moves the coast's native warp stop (and the approach clamp's lead)
  closer to the SOI boundary than `soiLeadSeconds`, floored at 120 s + 4 x
  `RAILS_WARP_RATES[approachMaxWarpFactor]` (rejected at spec load below it);
  `triggerNativeLeadSeconds` keeps the time-mode correction-trigger native warp armed to
  that many seconds before the trigger instead of `soiLeadSeconds`. Opted in on B26
  (400 / 30) and B22 (10,000 / 30); every other lane is unchanged. Re-fly proof: B26
  2026-10-03_1055 vs _0956 mission wall 707 s -> 596 s, same 20-phase list, corrections
  63.72 / 11.63 m/s (was 63.72 / 11.67), Vall park 170,812 x 167,552 m (was 170,547 x
  167,279; the two pre-change runs differ by ~980 m). B22 2026-10-03_1106 vs 2026-10-01_2235
  coast wall 368 s -> 315 s, corrections 124.10 / 72.73 (was 124.10 / 72.81), Jool park
  within 19 km of 584,339 km.
- [ ] Opt B30 (armed 2026-10-14, see the coast-lead-b5 item below; B29: dropped, next item) into the coast native lead (and `circularizeNodeWaitWarp` where the
  lane has a park round-out node), then re-fly both and compare against their last runs:
  phase list, correction dv, arrival periapsis, capture and park. Last-run recoverable per
  the analyzer: B30 `2026-10-01_1009` ~740 s (capture 468 s and PARK 125 s are already fixed
  by #1958; coast low-warp ~117 s is the new target), B29 `2026-10-01_0933` ~345 s (coast
  low-warp ~199 s). Size each lead per lane: soiNativeLeadSeconds >= 120 s + 4 x
  `RAILS_WARP_RATES[approachMaxWarpFactor]`. About 1.5 h of machine time; coordinate the
  machine lock with the other sessions before provisioning. Queued 2026-10-03 (operator: no
  more flights for now).
- [x] ~~B29 coast native lead~~ DROPPED 2026-10-12 (operator ruling: measure variance first,
  drop if main repeats). Opted in with B22's values (`soiNativeLeadSeconds` 10,000,
  `triggerNativeLeadSeconds` 30), B29 `2026-10-11_1701` saved 207 s of mission wall
  (2,023 -> 1,816 s) with identical corrections (1.97 / 21.00 m/s) but a Kerbin arrival
  periapsis of 119.7 km and park pe 118.4 km. Plain main flew 102.1 km / 100.8 km
  (`2026-10-11_1239`) and again 101.6 km / 100.3 km (`2026-10-12_0504`), so main varies by
  ~0.5 km and the opt-in shifted the outcome by ~17 km. Not explained by the arrival-quality
  round's timing alone: main's two runs planned it 60 s apart (ut 60,363,980 / 60,363,920)
  and landed 0.5 km apart, the opt-in planned it at 60,364,000. Kept from the branch: the
  b29 schema max for both keys raised from 3,600 to 1,000,000 (B29's floor is 4,120, so the
  old max made every legal value unloadable). Open: find what the opt-in changes on B29's
  final approach before retrying, and `_b5_native_warp` (`harness/missions/lib/mlib.py`
  ~13469) re-issuing on +-200 s / +2,000 s SOI-estimate flips (~26 s of re-ramp on B29's
  Jool exit).
- [x] ~~B19 / B20 / B21 park round-out hold~~ DROPPED 2026-10-13 (the B29 rule). The 2026-10-12
  operator tier measured ~590 s of 1x CIRCULARIZE node wait on each (the largest recoverable rows
  after B20's coast). Opted into `circularizeNodeWaitWarp` on branch `repin-duna-rocket`, the
  confirm re-flights saved ~520 s each (B19 2,950 -> 2,410 s, B20 4,655 -> 4,135 s, B21 3,045
  -> 2,535 s) and matched main to the metre through the park trim and ORBIT (B19 trim ap
  769,645 vs 769,648 m), but every arrival then diverged: the transfer node moved 3-33 s and
  the parks read Dres ap 850,661 m (main `2026-10-12_1149` 1,051,483 m; 1,110 km in 2026-08),
  Moho 396,037 m (main `_1259` 428,433 m), Eeloo 10,482,265 m (main `_1431` 10,621,342 m).
  Main's own run-to-run spread on these lanes is not measured, so the outcome is not shown
  unchanged and the key stays on B22 only. Open: two more plain-main flights per lane would
  say whether those parks are inside main's spread (about 3 h of machine time).
- [x] Coast native lead + latched-approach native warp, measured against plain-main variance
  (2026-10-13/14, branch `coast-native-lead-2`, owner gate: keep an opt-in only where the arrival
  stays inside main's own spread). New default-off b5 key `approachLatchNativeWarp`: on the
  latched target-SOI approach with a blind clock (`tts=nan`, which B19-B21 read for most of the
  approach), native-warp to the remembered entry UT minus the native lead instead of holding the
  rails cap for the whole window. Plain-main parks (park apoapsis): B19 1,051,483 / 1,051,512 m
  (`2026-10-12_1149`, `2026-10-13_1624`); B20 428,433 / 444,354 / 496,511 m (`_1259`,
  `2026-10-13_1713`, `_2137`); B21 10,621,342 / 9,699,388 / 12,791,382 m (`_1431`, `_1533`,
  `_2046`). Opt-in flights: B21 (coast lead 4,200 s + `circularizeNodeWaitWarp`, the owner's B21-only
  park-skip ruling; the latch key was armed but never fired, so it is left unarmed) `2026-10-14_0039` 11,593,287 m, corrections 166.79 / 5.79
  m/s against main's 166.80 / 5.52 and 166.79 / 5.43, wall 3,045 -> 2,448 s: KEPT. B19 (coast
  lead) `2026-10-13_2351` 942,969 m (second correction 15.97 vs 14.03 / 14.26), wall -110 s:
  DROPPED. B20 (coast lead 600 s + latch) `2026-10-13_2255` 381,492 m and `2026-10-14_0154`
  543,562 m, wall ~4,650 -> ~3,355 s: DROPPED (both outside main's spread; the mean matches, the
  spread widens, so an owner may revisit). `approachLatchNativeWarp` never fired in ANY flight
  (0 `approach:native-held` lines on B19 / B20 / B21): the native lead already covers the
  blind frames and `blind_soi_hold` keeps an in-flight warp, so the key ships default-off and
  unarmed, unit-tested only. B19's 29 m main spread shows the faster coast moves
  WHEN the late correction fires, so the arrival genuinely changes, not only its noise.
- [x] ~~Coast native lead armed on B20, B28 and B30~~ (owner ruling 2026-10-14, branch
  `coast-lead-b5`; new bar "tests pass and we still catch bugs" replaces the outcome-unchanged
  gate). `soiNativeLeadSeconds` (value / mlib floor / soiLeadSeconds): B20 600 / 520 / 100,000,
  B28 400 / 320 / 3,000, B30 400 / 320 / 3,000. The confirm flight B25 `2026-10-14_1726` (lead
  400) went MISSION-ASSERT-FAIL, Laythe pe -152 km: the native warp ran from tts 473,306 to
  tts 400, straight past the arrival re-correction window (600-3,600 s), where the unarmed
  `2026-10-12_1620` re-corrected at tts ~2,517 to pe 71 km. Fix: `mlib.coast_soi_arrival_lead`
  lifts an armed lead to min(soiLeadSeconds, 3,480) while that round can still fire (rounds
  done, corrections on, an extra left, SOI clock not naming another body); non-target
  crossings keep the short lead. That leaves B23 / B24 / B25 / B33 / B34 / B35 (start in the
  target parent's orbit, so the target approach is their only crossing) nothing to save, so
  they are NOT armed. B20's two greens at 600 skipped the window too (likely its 381,492 /
  543,562 m parks against main's 428-497 km); its Moho approach now stops at 3,480. B26
  (400) changes on its Vall approach (3,000 while an extra is left). B21 / B22 unchanged.
  Not armed anywhere: `approachLatchNativeWarp`, `circularizeNodeWaitWarp` (B21-only),
  `triggerNativeLeadSeconds`. Confirmed green 2026-10-14 on the arrival-window rule: B20
  `2026-10-14_1851` (3,381 s vs plain main 4,135-4,678 s), B28 `_1834` (338 s vs 390 s), B30
  `_1805` (1,719 s vs 1,763 s), B26 `_1839` (679 s vs 635 s; its Vall arrival re-correction
  window is now reachable).
- [x] Optional refresh re-flies of the b5 lanes last flown before #1958: B24, B33, B34, B35,
  B25, B28, B23 and B12. Done by the 2026-10-11 nightly and 2026-10-12 operator tiers: each
  MISSION-OK, analyzer recoverable now 51-108 s per lane (B24 108, B34 57, B35 / B25 56, B33
  54, B28 51); B33 needed the count window above, everything else green.
- [x] The run corpus survives worktree removal (HARNESS-RESULTS-ARCHIVE, branch
  `harness-results-archive`). The ~860-run corpus measured above lived in the worktrees'
  gitignored `harness/results/` and was lost when those worktrees were removed. `run.py` now
  copies each attempt's result / status / mission JSONs, mission stdout, its scenario's
  block of the harness log (gzipped, so a nightly does not store N growing log copies) and
  gzipped KSP.log to `<umbrella>/automation/results-archive/<runId>/` with a `manifest.json`
  (worktree, git HEAD, deployed dll sha256, sizes), verdict-neutral, opt-out
  `PARSEK_HARNESS_NO_ARCHIVE=1`; `flight_efficiency.py` reads the archive by default (gzip
  KSP.log), and `tools/archive_results.py --from <results-dir>` back-fills surviving
  snapshots. About 0.4 MB per run (10 flown runs measured), no pruning. Contract:
  `harness/README.md` "The machine-wide results archive".
- [x] WarpToUT 1x tail (branch `warptout-tail-trim`). Audit
  2026-10-11: the selector kept the bottom rung only while `rate * 2.5` game-s remained, so
  every capped rails warp ended on 12.2-12.3 s of 1x (AP-1 x2, CN-2, CN-3) and a physics warp
  on ~5 s (PWR 4.8 s); downshifts to a non-zero rung used stock's eased ramp (CN-3's 5x
  stretch read 9.9x). Fix: the bottom rung alone (rails 5x, physics 2x) uses a 0.5 s floor
  (`TestCommandWarpToUT.BottomRungMinRealSeconds`; tail 2.5 / 1.0 game-s) and every
  downshift is instant (`ShouldSetRateInstantly`); `maxRate=1` waits never leave 1x. CN-3 and
  OC-1 go from `maxRate=10` to 50 (GhostHide hides ghosts only above 50x). Expected from the
  ladder arithmetic: about 8 s per capped rails warp (15 s -> 7 s from 25 game-s out) and 2 s
  per physics warp; the nightly has 7 such rails warps and 14 physics ones, so about 85 s per
  nightly, plus about 38 s on CN-3 (warp ~64 s -> ~27 s) and 9 s on OC-1 (~28 s -> ~19 s).
  Flight proof on the branch (build sha 5d34c86d): AP-1 `2026-10-11_1657` PASS, its two
  WarpToUT steps 20.2 -> 12.7 s and 32.2 -> 26.7 s against the same day's main run, attitude
  counts unchanged (resolvedLines 971 / floor 400, surface 254 / 100); CN-3 and OC-1 PASS at
  maxRate 50. The per-tier total is still to be read off the next nightly.
- [x] Three analyzer over-counts (branch `flighteff-accuracy`; a 2026-10-11 archive read
  found them inflating "recoverable"). (1) Native warp_to ramp: inside a native `warp_to`
  (telemetry `warpTo=` ahead of ut) the target is capped at the best stock rails rate at
  most seconds left / 2 (`NATIVE_WARP_RAMP_SECONDS`), so KSP's own step-down near the
  target is `warped`, not low warp; machine rails with no `warpTo=` is unchanged. (2)
  MechJeb executor settle: an un-held waiting-for-node stretch (no `node-wait:` line in the
  visit) that ends in the executor's own rails warp, with no machine warp command, is by
  design. (3) BDOCK DOCK phase: MechJeb's docking autopilot at `dockSpeedMetersPerSec`, by
  design on the bdock / sdock / rdock machines. Archive 2026-10-11, recoverable before ->
  after: B7 45.0 -> 1.1 s, B15 93.5 -> 5.1 s (CIRCULARIZE 40.9 s now by design), BDOCK-1
  69.5 -> 0.0 s (69.4 s now by design), B29 242.9 -> 205.5 s (the machine x1000 coast
  clamp keeps its 194.5 s; only native-only runs dropped, 31.6 s). Every 2026-10-11 run:
  596.5 -> 272.8 s recoverable, 2,069 -> 2,180 s by design. `ci9_tip_dock` (tdock) delegates
  the same DOCK phase but stays outside the scope, like the rendezvous lead.

- [x] Nightly tier measured 2026-10-11/12 on main (88ee0a32e, with #2017 / #2075 / #2069; worktree
  `Parsek-flight-efficiency`, paused twice at lane boundaries for another session): all 190
  lanes flown, 186 PASS, 1 KILLED (H69 boot hang, fixed by #2078's watchdog), 3 PARSEK-FAIL
  (HC-2-ROLLOUT-CHARGE-NOT-RECORDED, L2 teardown NRE, S4.2 recorder-start race; see those
  entries). Tier wall 9.2 h over the 189 lanes with an analyzed log: missions 5.3 h with 471 s
  recoverable left; outside missions 3.9 h = KSP boot 1.2 h (24.4 s per lane), seam steps 1.5 h
  (real work: RunTests, WarpToUT, LoadGame), post-quit verifiers 0.6 h (12.0 s), pre-launch
  0.25 h, seam gaps 4 min (1.3 s per lane, was 13.7 s before #2017). The 15 longest lanes are
  45% of the tier (B29 2,039 s, V1-map-dwell 1,440 s, B14 1,431 s, BDOCK-1 1,393 s, B13
  1,235 s, B4 1,112 s, B15 1,014 s). Further cuts are structural and need operator rulings:
  shorten the dwell lanes, share flights between overlapping lanes, or move long lanes out of
  nightly.
- [x] ~~Pipelined post-quit verification~~ (owner ruling 2026-10-14, branch `pipelined-verify`). After KSP exits, a lane spent ~10.4 s mean (postQuitTail)
  in the verifiers, collect-logs, snapshot, artifacts and result writes before the next lane's
  ~25 s boot began. `run.py` now copies the lane's post-quit inputs (KSP.log, results file,
  render manifest, ModuleManager logs, the produced save, this run's screenshots, Player.log)
  into `results/.verify-private/<runId>/` and runs all of that on a worker over the copy while
  the next lane stages and boots; one lane in post-quit work at a time, lock / stage / launch
  serial. Same verdicts and records (fake-KSP smoke flies one selection both ways and diffs
  every result); a retry now flies one lane later. Review follow-up: the next lane's zombie
  preflight waits up to 180 s for a killed predecessor's KSP instead of refusing it busy; a
  raising worker is an INVALID(post-quit-error) row; Ctrl-C / an exception closes every open
  scenario; the copy-set test is an AST walk of the post-quit call graph (13 reviewer mutations, 12 red; a
  conditional-expression alias of the instance root is the one known miss). Opt out:
  `PARSEK_HARNESS_SERIAL_VERIFY=1` or `--serial-verify`. Contract: `design-autotest-harness-core.md` "Pipelined post-quit
  verification". MEASURED 2026-10-14, the daily tier (27 lanes) flown back to back from this
  branch: serial (`--serial-verify`) 1,425 s, pipelined 1,242 s, 183 s (13%, ~6.8 s per lane)
  saved, all 27 lanes PASS both ways. At that rate a ~200-lane nightly saves ~20 min.
- [x] Shared flights, owner ruling B 2026-10-14 on
  `docs/dev/research/harness-dwell-and-shared-flight-audit-2026-10-14.md` (the four SAFE
  rows), branch `shared-flight-merges`: GS-4 into GS-9, B2 into PWR-2, CL-1 into CL-2 (nightly)
  and CL-3 into CL-4 (operator). Each absorbed lane flew a prefix of its host's flight; its
  gating expectations and `[dimensionsCovered]` claims moved onto the host and the spec was
  deleted (384 -> 380 scenarios). Every moved gate was evaluated offline against the host's
  latest archived run and holds (GS-9 `2026-10-11_1542`, PWR-2 `2026-10-12_0741`, CL-2
  `2026-10-11_1443`, CL-4 `2026-10-12_1813`). Saves about 750 s per nightly tier run and
  64 s per operator pass. Cost accepted by the ruling: coarser failure attribution (a red in
  the host's own extension also reds the absorbed claims).
- [x] ~~Confirm-fly the four merged hosts on their merged specs~~: all PASS on 2026-10-14 -
  GS-9 `2026-10-14_1744` (492 s), PWR-2 `2026-10-14_1737` (299 s), CL-2 `2026-10-14_1742`
  (168 s), CL-4 `2026-10-14_1753` (67 s); host walls unchanged from their prior runs.

## GROUND-SCIENCE-CLUSTER-FIVE-PART-HOST: no committed fixture can host a cluster with three or more experiments inside stock inventory limits [FILED 2026-09-28, branch `deployables-lanes`]

**Why.** A stock EVA kerbal carries 2 slots / 40 L / 0.065 t
(`GameData/Squad/Parts/Prebuilt/kerbalEVA.cfg`) and every Breaking Ground part is 25-35 L
(`SquadExpansion/Serenity/Parts/DeployedScience/*.cfg`), so one kerbal carries ONE part at a
time; a cluster is carried in containers and taken out one by one (the operator's run did
exactly this from a rover). EVA-8 / EVA-9 / EVA-10 use the only container their hosts have,
the landed Kerbal X's Mk1-3 pod (3 slots / 200 L), plus the kerbal's own slot: 4 parts,
Central Station + RTG + TWO experiments. Surveyed 2026-09-28: every committed fixture's
landed crewed vessel off the pad is a one-kerbal Kerbal X capsule
(`kerbin-splashdown-recorded`, `mun-landing-recorded`); `duna-one-recorded` has two crew and
an EVA kerbal but is on Duna; nothing else lands crew off the pad.

**The Mun geometry (EVA-10).** Its first flights (`2026-09-28_2048`, `_2053_a2`) left Jeb
standing ON the lander after the ladder release, where every placement preview hit its hull;
the lane now runs `EvaGroundScience action=step distance=4.5` off the lander first. The pod
sits about 4 m above the ground (VESSEL `hgt = 4.18`), so from 4.5 m out it is ~4.4 m away,
inside stock's `EVA_INVENTORY_RANGE = 5` but not by much. If a reading refuses `take ...
reason=out-of-range`, the Mun lane needs the same fixture as below.

**Costed plan.** One forge flight builds a recorded host with a ground-level container: a
small crewed rover (Mk1 lander can or Mk2 pod on wheels) carrying two stock inventory
containers (`ConformalStorageUnit`, 3 slots / 300 L each) is launched from the runway with
`LaunchFromEditor`, driven by a new kRPC mission shell (`wheel_throttle` / `wheel_steering`,
about 150 lines plus its schema TOML and mlib tests) 300 m off the runway end so the KSC
exclusion zone cannot retire what it leaves, stopped, and harvested `--keep-parsek` as
`kerbin-rover-containers-recorded` with a builder under `harness/tools/` and a
`RECORDED_FIXTURES` pin. Its containers then take `[[fixture.partInventory]]` (6 slots:
CS + RTG + solar + seismic + Go-ob + ion/weather), and EVA-8/9's recipe runs unchanged with
two more takes. Estimate: one flight for the shell, one harvest, about half a session of
authoring. A Mun variant needs the same rover landed on the Mun (a B13-style delivery), which
is a second, longer forge flight; defer it until the Kerbin one has flown.

**The operator's own save is not the host (decided 2026-09-28).** `logs/2026-09-28_2231_
deployables` is a post-replay state: ten of its eleven members were refused by the defect
(PLACED-PART-CLUSTER-SPAWN-BLOCKED-BY-SIBLING), the rover and Bob were abandoned, and no clean
post-commit / pre-rewind save survives (the quicksave `1.sfs` is post-replay too). A committed
fixture must be a reproducible harvest with a builder and pins (the `refly-a-recorded`
precedent requires a clean seed session), and an operator-local fixture may only assert that
a window drew, which none of these lanes needs. Nothing from that save is committed or
staged.

## DEPLOYED-SCIENCE-FLOW-LANE-NEEDS-A-HOST: no committed science or career fixture can place a powered Breaking Ground cluster [FILED 2026-09-28, branch `deployables-lanes`]

**What is wanted.** A lane that places a powered cluster (Central Station + RTG or solar +
experiments), warps while it transmits, and checks the ledger rows: every deployed subject
is an UNTAGGED row even with a live recorder (todo DEPLOYED-SCIENCE-IS-ALWAYS-UNTAGGED,
`GameStateRecorder.IsDeployedScienceSubjectId`, `TryRecordKscScienceSubject`), survives a
Re-Fly (never tombstoned), and is dropped by a Revert with the other untagged post-launch rows.
Sandbox (every EVA-* host) awards no science, so it cannot host it.

**Survey (2026-09-28).** SCIENCE / CAREER fixtures: `fresh-science` (SCIENCE_SANDBOX, no
vessels), `career-*-pad` (one PRELAUNCH vessel on the pad), `career-earned-*`,
`rover-route-career` (probe-only rovers), `strategy-career`, `stock-screen-census*`. In every
one the R&D tree holds only `start` (no `spaceExploration`, `electrics`, `electronics`,
`miniaturization`, `experimentalElectrics`, the nodes the eight deployables require), so
stock drops the parts from a crew inventory CSV silently, and none has a crewed vessel off
the pad. A cluster placed ON the pad is fine for science (nothing spawns), but it still needs
the tech.

**Costed plan.** (1) A builder `harness/tools/build_science_bg_pad.py` derives
`science-bg-pad` from `career-science-pad` offline: R&D `Tech` nodes for the five nodes
above (stock's own `state = Available` + `part = ...` entries, read from a stock
tech-tree save rather than hand-written), plus `[[fixture.crewInventory]]` /
`[[fixture.partInventory]]` for the parts (the pad pod must carry an inventory; a Mk1-3 pod
does). About 200 lines with its test and README row. (2) Lane: boot, EvaExit, place the four
parts beside the pod, board, `WarpToUT` about 10 game minutes (ten 60 s transmissions),
`SaveGame`, then the ledger: `[expectations.ledger]` / oracle rows for the deployed subject
ids with an empty recording tag, and the log line of the untagged route. Estimate: one
reading flight after the builder; no product change expected.

## C2-DERIVED-FIXTURES-HOLD-JEB-OPEN-ENDED: every career fixture built from `C2CareerPostFix` shows Jeb held with no end date although he was recovered [FILED 2026-09-26 from the GUI-28 stock-screen census (run `2026-09-25_2055`, finding F8); OPEN, fixture work, not an overlay defect]

**Evidence.** The Astronaut Complex shows Jebediah as `Reserved` with the hover text `Flies
'Jumping Flea' on your committed timeline. ... Free once 'Jumping Flea' is recovered.` (KSP.log
`record label=stk-ac-ksc ... item=Jebediah Kerman kind=KerbalOnFlight ... why="... Free once 'Jumping
Flea' is recovered."`), and the VAB crew dialog greys him for the same reason, yet the base career
recovered him at UT 348.08. The base ledger
(`Source/Parsek.Tests/Fixtures/C2CareerPostFix/Parsek/GameState/ledger.pgld`) has, for the Jumping Flea
recording `5436a7e8840b4c5885afcbaedc9dc037`, a `KerbalAssignment` row with `endState = 0` (Aboard,
`endUT = 347.92`) and a `KerbalExperience` row whose career entries end in `Recover`, but NO
`KerbalRecovered` (type 34) row: the career was harvested before crewed recoveries wrote one, so the
reservation walk sees an Aboard end with no closing recovery and holds him open-ended. Every fixture
derived from it inherits the row set: `career-earned-pad` and `career-earned-ksc` (both carry that
recording id) and GUI-28's `stock-screen-census`.

**Options.** (1) Re-harvest the base career from a current build (fly the Jumping Flea recovery again
so the capture writes the `KerbalRecovered` row), then re-run the derived-fixture builders
(`harness/tools/build_career_earned_pad.py` and siblings; `test_career_earned_pad.py` asserts
byte-identity with the base, so they must move together) and re-pin whatever reads Jeb's state. (2)
Leave the fixtures and document that Jeb reads held on them. No migration or load-time repair of old
ledgers: the recording schema policy (CLAUDE.md, "Recording schema") is one current contract, and a
missing capture row is not something load code should synthesize.

## ARCH-STOCK-UI-RESERVATION-CYCLES-2026-09-25: the stock-UI reservation layer added eight types to the kernel knot and a new 7-type knot [FILED 2026-09-25 when `scripts/arch/modules.toml` classified the layer; OPEN, low; architecture debt, no behavior defect. POSTPONED 2026-10-25 (owner): architecture and refactor work comes after the full test rerun]

**What the map shows** (`python scripts/arch/archview.py --check`, canaries in
`scripts/arch/test_archview.py`):
- The kernel knot went 293 -> 301 once the new root files were classified. Joined:
  `CommittedFutureIndex`, `CommittedFutureIndexCache`, `ReservationExplanation`,
  `StrategyReservationPredicates`, `StrategyStatePatcher`, `LedgerStrategySnapshot` (this
  program), plus `FacilityRepairCapture` and `RefusedResumedCopyTail` (earlier work, also
  unclassified until now). The 2026-09-22 pin of 286 was already stale on main at 293.
- A new 7-type knot: `StockUiOverlayController`, `StockUiRnDDecoration`,
  `StockUiAstronautDecoration`, `StockUiFacilityDecoration`, `StockUiLiveSnapshot`,
  `KerbalDismissalPatch`, `FacilityMenuUpgradeBlockPatch` (UI 5, Patches 2).
- The greedy cut sequence now starts with `GameAction` (its one in-knot reference, `Ledger`,
  splits off 28 types), then `RecordingStore`.

**2026-11-01:** the 7-type knot no longer exists on its own: once the 50 unplaced root files
were placed, it merged into the kernel knot (584). The fix direction below is unchanged.

**Why it happened (likely, to confirm with `--check`'s hub list).** The index and the
explanation builder read `LedgerOrchestrator` / `EffectiveState` / `KerbalsModule`, and
`LedgerOrchestrator` references the index cache and the strategy state patcher back (cache
invalidation, the patch step), which closes the loop. The 7-type knot is the controller
calling the per-screen decoration helpers while those helpers (and two patches) call the
controller's refresh / snapshot back.

**Fix direction (design, not urgent).** Invert the back-references: have
`LedgerOrchestrator` raise its existing `OnTimelineDataChanged` event and let the index cache
and the strategy patch subscribe instead of being called by name; pass the snapshot into the
decoration helpers instead of letting them reach the controller. Re-pin the canaries in the
same commit as whatever moves them.

---

## SEAM-HIDDEN-ADMINISTRATION-SCREEN-RESIDUE: automation-only robustness of the `activate-strategy` seam [FILED 2026-09-24 from the PR #1803 review; OPEN, low]

1. The hidden Administration screen is released only on a KscAction execute or a scene change, not when the command times out or is rejected (`ParsekTestCommandAddon.KscStrategy.cs` ~:41 returns early while a canvas is held). If the singleton never appears, every later `activate-strategy` in that scene times out on `administration-not-ready`, and a real Administration screen opened in that scene would hit stock's "Instance already exists" check. Fix: release in `HandleDefer`'s TIMEOUT branch and on Reject.
2. The readiness check tests `Administration.Instance != null`, set in `Awake`, while the slot limit and commitment ceiling are read in `Start`; safe today only because `Pump` runs once per frame. Gate on a field `Start` sets, or compare the slot limit with `GameVariables`.
3. `StrategyDisplayNames`' production cache is not exercised by the unit tests (the test hook bypasses it).

## LISTHANDLES-CHAINS-DIGEST-SCOPE: the chains digest hashes each chain's links only as a COUNT, and covers only the kept (future, non-terminated) chains [FILED 2026-09-22 from the #1761 review. OPEN; a follow-up, deliberately not fixed in that PR]

`TestCommandListHandles.ChainsDigest` hashes `pid|links|tip|spawnUT(R)|terminated;` per
chain, where `links` is `chain.Links.Count`. Two chains with the same pid, tip and spawn UT
but a DIFFERENT link set of the same length (another claiming recording, another branch
point, another interaction type) digest identically, so CI-3's readback equality is
blind to a link-level change. And the family reads `ParsekFlight.ActiveGhostChains`, which
`FilterAndGhostChains` has already reduced to chains whose spawn UT is still in the future
and which are not terminated: a terminated or past-spawn chain is outside both captures, so
the readback says nothing about how those are re-derived (on bdock-recorded at rp1, 12 of
the 13 evaluated chains are outside it).

2026-09-23 (PR-D): each chain now also reports its links' distinct tree ids (`chain<i>trees` /
`chain<i>treeIds` and a per-chain log line), deliberately OUTSIDE the digest so CI-3's pin holds;
the digest's link-level blindness is unchanged.

Fix direction: add each link's `recordingId` (and branch point id) to the canonical line,
and consider a second, pre-filter view (`ComputeAllGhostChains`' full output, or a
`scope=all` arg) so terminated chains are read back too. Either change moves CI-3's pinned
`digest=6ad6ec1c` (a fixture-derived literal in two required tokens), so it needs CI-3
re-flown (reading, armed, control) in the same PR, plus the xUnit digest pins
(`bbd83d3b`, `8952919c`) recomputed.
## ARCH-FINDINGS-REPORT: the architecture findings report and how to regenerate its numbers [FILED 2026-09-15. A POINTER, not a defect. OPEN as the entry point for the refactoring work that follows. POSTPONED 2026-10-25 (owner): architecture and refactor work comes after the full test rerun]

**What is true.**
- The findings report IS the regenerated atlas: run `python scripts/arch/archview.py --check`,
  then open `docs/dev/arch/atlas.html`. It carries the headline tiles, the "Main findings"
  list, module layers, the cycle and its cut sequence, hotspots, co-change, the largest files
  and types, and "Opportunities, ranked" with PR status. The findings and the opportunity
  rows are the only hand-written conclusions; they live in `scripts/arch/atlas.toml`
  (`[findings]`, `[[opportunities]]`), point at the generated sections instead of restating
  their numbers, and `--check` reports their review date's age (a line past 30 days) and any
  opportunity type no longer in the model.
- The hand-written snapshot `docs/dev/research/architecture-findings-2026-09-14.html` was
  retired on 2026-09-22 because its numbers went stale; it lives in git history (last in
  commit d9dca69e6). The published link https://claude.ai/artifact/9Qzf1YvzDFsTSLF6X5We9b
  stays and now carries the atlas.
- Every number on it regenerates from the source tree and git history in about 20 s:
  `python scripts/arch/archview.py --check --place` writes the live views under
  `docs/dev/arch/` (gitignored: `atlas.html`, `explore.html`, `matrix.html`,
  `ladder.html`, `modules.svg`, `edges.json`, `types.json`, `history.json`,
  `sizes.json`, `core-placement.md`) and prints the check report. Hand-authored inputs are
  `scripts/arch/modules.toml` (file to module, forbidden edges, the runtime-coupled module
  list) and `scripts/arch/atlas.toml` (prose). Contract and reading guide:
  `docs/dev/arch/README.md`.
- **Why the original missed GhostMapPresence.** The generator
  had no notion of size when the report was written, which is why it says almost nothing
  about `GhostMapPresence` (13.5k lines, a partial class whose members are all static). It
  now has one: `sizes.json` plus the SIZE section rank the largest files and types with
  partials merged, their long methods, their static state (reassignable slots plus
  `static readonly` collections whose contents change) and an ESTIMATE of their pure
  static pool, and print rule-based split candidates with a tier (README, "Size view").
  Read the pure pool as an estimate to verify, never as a list to lift. The same pass fixed the partial-class file attribution behind the
  omission - a partial type was credited to whichever file the walk met first, so every
  large partial class (`ParsekFlight`, `GhostMapPresence`, `RecordingStore`,
  `FlightRecorder`, `GhostPlaybackEngine`) was scored on a tiny part's commit count and
  dropped out of the hotspot table. Re-read the regenerated HISTORY and SIZE sections
  before ranking anything from the 2026-09-14 hotspot list.
  The hand study of the motivating case, with a cluster table, ranked extraction
  candidates and a do-not-extract list tied to the render contracts, is
  `docs/dev/research/ghostmappresence-extraction-research-2026-09-22.md`.
- The 2026-09-14 ranked list behind the opportunity rows is `docs/dev/research/architecture-opportunities-2026-09-14.md`;
  the VesselSpawner member-level plan is `docs/dev/research/vesselspawner-split-plan-2026-09-14.md`.
- Landed so far (2026-09-14): #1682 tooling, #1683 ParsekLog leaf, #1684 Recording data-only,
  #1685 Missions -> Logistics boundary, #1686 VesselSpawner step 1. Cycle 391 -> 282.

**Refreshed 2026-11-01** (main 2622fd5e6, full history; `archview.py --check --place`, `count-code.py`):
- The checker had drifted with nothing to notice it: 50 production root files were unplaced
  (skipped from every figure) and six real-tree cells in `scripts/arch/test_archview.py` were
  red on main. The 50 now have `R0` rules in `scripts/arch/modules.toml` (one fix:
  `GravityHackDetector.cs` holds a `FlightRecorder` partial, so it is Recording) and the
  canaries are re-pinned.
- The kernel knot is 584 types (301 on 2026-09-25); the stock-UI 7-type knot merged into it.
  The greedy cuts leave 328 (no longer half). The first cut is `Recording` again: the data
  type calls `LaunchSiteOnlyGhost.IsHidden` and `SidecarVersions.CopyHashes`, which ties 50
  types to the knot (a regression of #1684). Second is `GameAction` (five references).
- 109 production files at or above 1,000 lines, 16 at or above 5,000 (83 / 14 on 2026-09-22).
  Largest types: `ParsekFlight` 35,099 lines over 14 files, `GhostMapPresence` 15,314,
  `FlightRecorder` 14,935, `GhostPlaybackLogic` 14,653, `ParsekScenario` 12,959,
  `LedgerOrchestrator` 11,222 (one file), `RecordingStore` 10,142.
- Top file pairs: `FlightRecorder.cs` / `ParsekFlight.cs` and `ParsekScenario.cs` /
  `RecordingStore.cs`, 160 commits each.
- Boundaries: `Missions -> Logistics` was a name collision (a nested `VesselIdentity` struct in
  `RouteEndpointTransfer.cs`), not a crossing. `Ghost -> GameActions` is crossed by one real
  call (`GhostPlaybackLogic` -> `RecoveredRecordingEvidence.IsRecoveredByLedgerRowLive`).
- Atlas findings and opportunities re-read (`atlas.toml`, `reviewed = "2026-11-01"`). The
  regenerated page is published at https://claude.ai/artifact/6uSBEKzHEtBHaK5t7bFxjm; the
  link above keeps the 2026-09-22 page. Without graphviz `dot` on PATH the page has no map.
- On the refactor branch claude/project-thread-bkwmrr only (not on main, they change
  compiled code): `Recording` is plain data again (knot 584 -> 535; `GhostVisible` reads an
  installed `Recording.GhostHiddenRule`, `DeepClone` copies the sidecar hashes inline), and
  `GameAction`'s two real knot calls moved out (the `cseq` key and parse to a leaf
  `CaptureSequenceCodec`, the per-load tallies from `Ledger` to a nested
  `GameAction.LoadTally`). The golden `GAME_ACTION` codec test that pins the bytes
  (`SaveRoundTrip/GameActionCodecGoldenTests.cs`) tests main's codec and is on main too.
- The scanner now skips member names (ARCH-TOOLING-ROSLYN-AND-CI). The member rule leaves
  object-initializer lines and a property named like its own type (`Treatment Treatment
  { get; }`) as references (review fix). The new 3-type knot is the stock-UI grey trio
  `ButtonEntry` / `GreyLedger` / `StockUiGreyedButton`. On main's production code the knots
  are 542, 4 and 3, and the first greedy cuts are `Recording` (542 -> 494: `LaunchSiteOnlyGhost`,
  `SidecarVersions`) and `GameAction` (455: `CaptureSequence`, `Ledger`); the cut residue is
  301. On the refactor branch, with both re-cut, the knots were 454, 4 and 3, the first cuts
  `Route` then `RecordingStore`, the residue 273. `GameAction`'s remaining knot edges there
  (`CaptureSequence`, `ConversionCause`, `FundsEarningSource`, `ReputationSource`) were
  scanner false positives: same-named fields, and the enum member `Milestone` read as a type.
- `KspStatePatcher` (6,939 lines, one file) is now a partial class over 11 files (S3, pure
  move by `scripts/refactor/split_kspstatepatcher.py`, which checks the line multiset): the
  main `KspStatePatcher.cs` (330: `PatchAll`, `PatchPools`, the resets and EVERY static field,
  since partial-file field initializers run in an undefined order) plus `.Science` (721),
  `.Tech` (955), `.Unlocks` (788), `.FundsReputation` (324), `.PoolGuards` (622),
  `.RewindReadback` (252), `.Milestones` (895), `.Contracts` (783), `.ContractParameters`
  (609) and `.ContractRemoval` (790).
- `LedgerOrchestrator` split into partial-class files (rule S3, pure move by
  `scripts/refactor/split_ledgerorchestrator.py`; every static field stays in the main file):
  `LedgerOrchestrator.cs` 600 lines, `.Commit.cs` 1279, `.CommitRows.cs` 974,
  `.EarningsReconcile.cs` 1188, `.Recalculate.cs` 1511, `.Seeds.cs` 1286, `.Persistence.cs`
  927, `.LiveEvents.cs` 1514, `.Recovery.cs` 1271, `.Affordability.cs` 781.
- `FlightRecorder.cs` split into partial files (S3, pure move by
  `scripts/refactor/split_flightrecorder.py`; fields, constants, initialized properties and
  constructors stay in the main file): `FlightRecorder.cs` 2,270 lines, `.PartModuleStates`
  1,909, `.PartEvents` 1,866, `.PointCommit` 1,343, `.RelativeAnchors` 1,148,
  `.StopAndFinalize` 1,082, `.StartRecording` 977, `.EnvironmentAndTrackSections` 808,
  `.Sampling` 807, `.EngineAndRcs` 626.
- `GhostMapPresence.cs` split into partial files by `scripts/refactor/split_ghostmappresence.py`
  (pure move; every field, const and initializer stays in the main file): main 1,762 lines,
  `.ProtoVesselLifecycle` 2,106, `.StateVectors` 2,093, `.SourceResolution` 1,961,
  `.TrackingStationLifecycle` 1,656, `.FlightMapLifecycle` 1,274, `.TargetingAndSelection`
  1,022, `.OverlapInstances` 975, `.ReFlySuppression` 791, `.OrbitLines` 791.
- `GhostPlaybackLogic.cs` (9,489 lines) is split further (S3, pure move by
  `scripts/refactor/split_ghostplaybacklogic.py`, same line-multiset check; region pairs move
  whole): the main file (907: every field, the resets, the external-vessel ghost policy and
  the zone / soft-cap fidelity / auto loop range policy) plus `.GhostInfo` (698),
  `.Visibility` (679), `.PartEvents` (1,404), `.EngineRcsFx` (1,036), `.Audio` (926),
  `.Robotics` (641), `.SynthesizedMotion` (1,243), `.HeatDeployablesLights` (977) and
  `.SpawnDecision` (1,077), beside the existing `.SpanClock` / `.StandaloneLoopClock` /
  `.WarpLoopPolicy` / `.WatchMode`.
- `GhostPlaybackEngine` (9,327-line main file) split further into partial-class files (rule
  S3, pure move by `scripts/refactor/split_ghostplaybackengine.py`; every field, event,
  property, constant and the constructor stay in the main file): `GhostPlaybackEngine.cs`
  1494 lines, `.InRangeRender.cs` 748, `.LoopUnits.cs` 1357, `.LoopPlayback.cs` 1603,
  `.Completion.cs` 611, `.SectionPositioning.cs` 700, `.ReentryFx.cs` 481, `.GhostVisuals.cs`
  635, `.SpawnDestroy.cs` 933, `.Diagnostics.cs` 882.
- `BackgroundRecorder.cs` (8,231 lines) split further into partial-class files (S3, pure move
  by `scripts/refactor/split_backgroundrecorder.py`; every field, constant, property and the
  constructor stay in the main file): `BackgroundRecorder.cs` 1,507 lines, `.MemberReconcile`
  516, `.GameEventHandlers` 310, `.SplitDetection` 1,388, `.Finalization` 885,
  `.StateInitialization` 862, `.Sampling` 809, `.RelativeAnchors` 1,118, `.TrackSections` 932,
  beside the existing `.PartEventPolling` / `.State` / `.Testing` / `.TimeJump`.
- `GhostVisualBuilder.cs` (7,754 lines) split further into partial-class files (S3, pure move
  by `scripts/refactor/split_ghostvisualbuilder.py`; every field, constant and property stays
  in the main file): `GhostVisualBuilder.cs` 1,841 lines, `.FxSetup` 763, `.AnimationSampling`
  842, `.HeatAndReentryFx` 984, `.AudioFx` 209, `.RcsFx` 357, `.Fairings` 272,
  `.SynthesizedMotion` 793, `.PartInfoBuilders` 1,058, `.TransientFx` 742, beside the existing
  `.Parsing` / `.SnapshotBaseline` / `.Variants`.
- `UI/RecordingsTableUI.cs` (6,965 lines, an instance class) is now a partial class over 10
  files (S3, pure move by `scripts/refactor/split_recordingstableui.py`; every field, event,
  property, constant and the constructor stay in the main file): `RecordingsTableUI.cs` 1,231
  lines, `.TableRows` 1,125, `.GroupTree` 900, `.RewindColumns` 765, `.UnfinishedFlights` 625,
  `.Window` 592, `.LoopPeriod` 572, `.SortAndStatus` 533, `.GroupAggregates` 498,
  `.WatchButton` 250.
- `Display/GhostTrajectoryPolylineRenderer.cs` (6,938 lines) is now a partial class over 7
  files (S3, pure move by `scripts/refactor/split_ghosttrajectorypolylinerenderer.py`; every
  field, constant and property stays in the main file): `GhostTrajectoryPolylineRenderer.cs`
  4,166 lines, `.Builder` 621, `.Anchoring` 575, `.LegDraw` 531, `.SeamBridges` 459,
  `.Diagnostics` 387, `.ForwardArcs` 265. The main file stays large on purpose: the nested
  `Driver` (2,764 lines, the fenced single draw host of design-map-ts-render-architecture.md
  Appendix A) and the ownership / paint publication stay whole in the file the contract names.
- Test-gap pass over the 10 split classes (test-only): 52 previously untested `internal`
  helpers (guards, classifiers, formatters, clamps) now have direct characterization tests in
  10 new `Source/Parsek.Tests/<Class>CharacterizationTests.cs` files (148 cases, 3 mutation
  checks red as expected). A name-based scan still finds 85 untested `internal` logic methods
  in those classes (338 counting `private`), most of them Unity-coupled (particle systems,
  audio, transforms) or reachable only through private helpers.
- On the refactor branch claude/project-thread-bkwmrr only (not on main, it changes
  compiled code): the dead Re-Fly frame-offset chain is removed (no behavior or log-text
  change). Its offset resolver always refuses (`display-alignment-removed`), so
  `TryCanonicalizeReFlyRecordingPoint` / `TryCanonicalizeReFlyRecordingOrbitSegment` never change a point or a segment and every
  caller ignored the result. The 15 point call sites go; the 8 orbit-segment call sites keep
  the one line the old path could write (`... skipped: ... reason=orbital-velocity-non-finite`)
  through `FlightRecorder.LogReFlyOrbitSegmentVelocityNonFinite`. The two characterization
  cases of the removed `TryApplyReFlyRecordingFrameOffsetToWorld` go with it there (on main
  all 148 stay).
- On the refactor branch only: `BackgroundRecorder.ShouldRunBackgroundStateDriftCheck`'s
  time-went-backwards branch `nextLastCheckUT = double.MinValue` is a dead store (the
  fall-through always overwrites it with `currentUT`); removed there, behavior identical.
- Source-gate tightening (test-only): `ReFlyAnchorBypassWiringTests`'s background
  no-bypass gate now scans `UpdateBackgroundAnchorDetection` from its declaration in
  `BackgroundRecorder.RelativeAnchors.cs` (it scanned from the call site in
  `BackgroundRecorder.cs`, so a bypass call planted in the method stayed green). The three
  readers that name a split class's files (`BackgroundRecorderSourceFiles`,
  `GhostTrajectoryPolylineRendererSourceFiles`, `RecordingsTableUISourceFiles`, through the new
  `PartialClassSourceFiles`) and hlib's `_bg_recorder_source` now list `<Class>.*.cs` on disk
  and fail naming any partial file their lists do not declare; the other readers
  (`GhostPlaybackEngine`, `KspStatePatcher`, `LedgerOrchestrator`, `GhostMapPresence`,
  `FlightRecorder`, ledgeraudit `_src_paths`) already read the whole glob.
- Merge of main a73abc1af (2026-11-04): main edited four split classes, so each was re-split
  from main's single file by its script (line multiset checked by the script and again
  independently). New members placed: `ReadLiveAllowNegativeCurrency` -> `.PoolGuards`;
  `ResolveCrewRowName` -> `.CommitRows`, `SlotOwnerOrSelf` / `CrewSlotOwnerName` ->
  `.Persistence`; the flag pick-up members (`ApplyFlagPickUps`, `RemoveStandingFlagsLive`,
  `ResolveBodyRadius`, `ResolveBodyRadiusLive` and two testing setters) ->
  `GhostPlaybackLogic.PartEvents`. Main's new `GhostMapPresence.OverlapHighWarp.cs` stays as
  main wrote it (now a declared sibling in `split_ghostmappresence.py`). Line counts after:
  `KspStatePatcher.PoolGuards` 632, `LedgerOrchestrator.CommitRows` 1,013 and `.Persistence` 977,
  `GhostMapPresence.cs` 1,764, `.ProtoVesselLifecycle` 2,107 and `.OverlapInstances` 1,002,
  `GhostPlaybackLogic.cs` 913 and `.PartEvents` 1,573. Arch map: main's three new root files got
  R0 rules (`FlagPickUp` / `RemovedReplayFlags` -> Ghost, `ParkedSplitSaveOverlay` -> Recording);
  on the refactor branch the knots became 456, 4 and 3 (`ParkedSplitSaveOverlay` and
  `NoSaveSceneExit` joined the kernel), the first cuts `Route` (456 -> 438) and `RecordingStore`
  (421), and the cut residue rose 273 -> 277, all main's own code. `CareerSettingsLock.Action`
  is a new scanner false positive (every `System.Action` reads as it) and has a glossary line
  saying so.
- Release branch `refactor-release-splits` (2026-11-04): everything above except the
  compiled-code changes marked refactor-branch-only, so its `Parsek.dll` is method for method
  identical to main's (ikdasm compare). It keeps the partial-file splits (re-split from main's
  single files by the scripts), the test-only changes and the architecture tooling; there the
  knots are 542, 4 and 3 and the cut-residue ratchet is 306 (301 before main's save-format
  freeze #2380 removed `GameAction`'s `Ledger` call; the first two cuts now leave 496 and 456).

**Fix.** None; this entry is where the next refactoring session starts. Before ranking
again, regenerate and read the SIZE section (largest files and types, Tier 1 / Tier 2,
split rules; the later break-up pass of large files and classes into smaller ones works
from it, cheapest rule first: S1 extract-method, S2 pure helper, S3 partial files, S4
state map plus facade), the KNOTS greedy cuts and the upward-edge count; the remaining
items (ARCH-RECORDINGSTORE-GOD-OBJECT, ARCH-PARSEKFLIGHT-CHANGE-HUB, VesselSpawner steps 2-5,
ARCH-TOOLING-ROSLYN-AND-CI) are design work planned one PR at a time.

## ARCH-PARSEKFLIGHT-CHANGE-HUB: ParsekFlight.cs is 27,591 lines (30,126 on 2026-11-01; the type is 35,099 over 14 files) and on one side of every top cross-module co-change pair [FILED 2026-09-14 off the architecture program (`docs/dev/research/architecture-opportunities-2026-09-14.md` item 1). A STRUCTURAL debt, not a defect. OPEN; the largest item on the list and the last to start. POSTPONED 2026-10-25 (owner): architecture and refactor work comes after the full test rerun]

**What is true.**
- Touched in 1,125 of the 5,127 commits since 2025-03-01 (22 percent of every commit to
  `Source/Parsek`); the next file is `ParsekScenario.cs` at 420.
- The top cross-module co-change pairs are all this file with another: `FlightRecorder.cs`
  (147 commits together), `RecordingStore.cs` (128), `GhostPlaybackEngine.cs` (106),
  `BackgroundRecorder.cs` (90), `ParsekUI.cs` (87), `GhostPlaybackLogic.cs` (79),
  `GhostVisualBuilder.cs` (75), `VesselSpawner.cs` (63), `Recording.cs` (60).
- Regenerate the evidence with `python scripts/arch/archview.py --check --place`
  (HISTORY section; the views are gitignored).

**Fix.** Extract one seam per PR, each behind a harness tier, in co-change order: the recorder
hookup (`FlightRecorder` / `BackgroundRecorder`), playback hosting, spawning, the merge flow.
`WatchModeController`, `ParsekPlaybackPolicy` and `ChainSegmentManager` are prior extractions
of the same shape. Do this AFTER ARCH-KNOT-CHEAP-CUTS and ARCH-RECORDINGSTORE-GOD-OBJECT, which
make the types it touches easier to reason about.

## ARCH-RECORDINGSTORE-GOD-OBJECT: RecordingStore is a list plus eight services in one type [FILED 2026-09-14 off the architecture program (item 4). OPEN; the first item with real design content, after the cheap cuts. POSTPONED 2026-10-25 (owner): architecture and refactor work comes after the full test rerun]

**What is true.**
- 6,970 lines plus partials, fan-in 91 files, touched in 408 commits (third most churned file);
  co-changes with `ParsekScenario.cs` 131 times and `ParsekFlight.cs` 128. On 2026-11-01:
  10,142 lines over 13 files, fan-in 101, 497 commits, 42 reassignable statics; co-changes
  with `ParsekScenario.cs` 160 times and `ParsekFlight.cs` 150.
- Inside the knot it references 37 other members: stores, codecs, the scenario, the flight
  controller, the ledger orchestrator, the crew manager. It is the third greedy cut and the
  first that names a design problem rather than a stray reference.
- `CommittedListNotifications` documents one contract (the index-keyed live state); the sidecar
  I/O, optimizer, orphan cleanup, tree discard, group hierarchy, ledger trigger and
  switch-segment classification it also drives have no stated contract.

**Fix.** The store as the committed list with its notifications; the operations that use it
(optimizer, purge, sidecar commit, session merge) as services on top that the scenario module
calls directly. One service per PR; the co-change with `ParsekScenario.cs` is the measure.

## ARCH-KERNEL-AND-TRAJECTORY-PLACEMENT: three files are filed where the map says they do not belong [FILED 2026-09-14 off the architecture program (items 6, 7 and 9). PARTLY DONE 2026-09-14: the Missions -> Logistics crossing closed in #1685 (merged); the Trajectory half closed as a placement error (five recorder files matched the Trajectory prefixes; `modules.toml` now places them, Trajectory reads at instability 0.06); VesselSpawner step 1 of 5 merged as #1686 (`VesselSnapshotOps`), plan in `docs/dev/research/vesselspawner-split-plan-2026-09-14.md`. OPEN for steps 2-5. POSTPONED 2026-10-25 (owner): architecture and refactor work comes after the full test rerun]

**What is true.**
- `VesselSpawner.cs` (6,897 lines, 163 commits, co-changes with `ParsekFlight.cs` 63 times)
  stays in the eight-file Core kernel because no module owns a majority of its references, and
  it is the only reason Core references Recording (21 references, the kernel's one upward edge).
- Trajectory references Recording 51 times through `BallisticExtrapolator`, `OrbitReseed` and
  the incomplete-ballistic scene-exit finalizer, so the one module the docs call pure math is
  not.
- `MissionRouteStructureList.cs` references `Logistics.Route` and `RouteStop`: the one declared
  boundary (`[forbidden]` in `scripts/arch/modules.toml`) the checker finds crossed. (Closed
  in #1685. On 2026-11-01 the crossed boundary is `Ghost -> GameActions`: one call,
  `GhostPlaybackLogic` -> `RecoveredRecordingEvidence.IsRecoveredByLedgerRowLive`.)

**Fix.** Split `VesselSpawner` into the snapshot/manifest half (kernel) and the live spawn and
recover half (Controllers); move the Recording-aware half of the extrapolator into Recording;
move the route structure list into Logistics or read it through an interface Missions owns.
Update `modules.toml` in the same commits so the placement report stays true.

## ARCH-TOOLING-ROSLYN-AND-CI: the architecture checker is a text scan that gates nothing [FILED 2026-09-14 off the architecture program (phases 5 and 6). OPEN; tooling, no product change until the namespace tidy. POSTPONED 2026-10-25 (owner): architecture and refactor work comes after the full test rerun]

**What is true.**
- `scripts/arch/archview.py` matches identifiers against declared type names after stripping
  comments, strings and directives. It cannot see reflection, string-keyed lookups or generic
  inference, and names declared in two modules are dropped. Good enough to place files and rank
  couplings; not sound enough to fail a build.
- The three declared boundaries in `modules.toml` `[forbidden]` are report-only, and the
  placement contract (no unplaced root file, every module has a summary) runs only when someone
  runs the script. Nothing in CI or the xUnit suite executes it.
- Namespaces do not match the module map: 370 of 749 files sit in the flat `namespace Parsek`,
  so no off-the-shelf tool (ArchUnitNET, NsDepCop, NDepend) can express the boundaries either.
- 2026-11-01 (refactor branch): the scan now skips member names. A field, property, method or
  enum member spelled like a type (`public long CaptureSequence;`, the enum member `Milestone`)
  is not read as a reference to that type, unless it is type-qualified (`Ledger.X`,
  `Parsek.CaptureSequence.Key`). On the refactor branch, where `GameAction`'s two real knot
  calls are moved out, that removed the false positives that still held it in the knot
  (535 -> 454); on main's code its real calls keep it in (knot 542). Still missed: object-initializer members of another type (`Held = ...`
  on `Ghost -> GameActions`), which only the Roslyn extractor below can tell apart.

**Fix.**
1. A Roslyn extractor in `Parsek.Tests` that walks the semantic model and emits the same
   `edges.json` / `types.json` shape, so every view and every number stays comparable.
2. An xUnit test that fails on the placement contract only (unplaced root file, missing atlas
   summary) while dependency findings stay report-only; add the regeneration command to the
   per-commit docs checklist in `.claude/CLAUDE.md`. **DONE 2026-11-01 (refactor branch) in a
   different shape:** CI's `tests` job now runs `scripts/arch`'s own suite, whose real-tree
   cells include the new `test_every_production_file_is_placed` (placement contract), the
   atlas-summary cells and the knot canaries; `.claude/CLAUDE.md`'s CI paragraph names it.
3. Then the mechanical namespace rename to match `modules.toml`, and arm the three forbidden
   edges as failing tests.

## REGISTRY-GROWTH-DECISIONS-2026-09-11: two registry values the 2026-09-10 wave measured but was not allowed to register, D4 `persistence-graze-suppression` and D9 `rewind-to-launch-repeat` [FILED 2026-09-11 off the wave-0910 decision memo (`docs/dev/research/wave-0910-open-decisions-2026-09-11.md` sections 1 and 2). An OPERATOR DECISION, not a defect. RULED YES and APPLIED 2026-09-15 (register B1 / B2); only D9's LIVE control stays owed]

**RULED YES and APPLIED 2026-09-15:** D4 `persistence-graze-suppression` added (registry
comment names step 7 `IsGrazePattern` vs step 5 `IsSurfaceGrazePattern`) and claimed on LT-2
off the `Split summary ... grazeForward=2 grazeBackward=2 ...` token pinned from run
`2026-09-10_1957`; armed re-flight and control (`grazeForward=2` -> `=3`) flown this session
(run ids recorded in `autotest-status.md`). D9 `rewind-to-launch-repeat` added and claimed on
GS-9 off its two existing backreference tokens; the token control was discharged OFFLINE over
`2026-09-11_0109` via `hlib.evaluate_expectations` (intact PASS/0; each mutated backreference
FAIL/1), the live control owed on the next GS-9 flight.

**Why it is a decision.** The wave's ruling G5 forbade registry growth and sent both
candidates to the operator. A new value is a registry change (`harness/coverage/registry.toml`
plus its catalog line), so it moves the cell total as well as the covered count.

**D4 `persistence-graze-suppression` (memo section 1).**
- Optimizer step 5 and step 7 are different predicates with different counters.
  - Step 5: `IsSurfaceGrazePattern` (`RecordingOptimizer.cs:517`) feeds
    `surfaceGrazeForward` / `surfaceGrazeBackward`. RF-1 gates those as D4
    `surface-graze-suppression`.
  - Step 7: `IsGrazePattern` (`:461`, the `BriefSectionMaxSeconds = 120` collapse-walk)
    feeds `grazeForward` / `grazeBackward` (`:680` / `:683`).
- RF-1's required `Split summary` token pins the step-7 pair at `grazeForward=0
  grazeBackward=0`, so no lane gates the persistence predicate FIRING.
- LT-2's `EccentricGrazing_StaysOneSegment_InGame`
  (`InGameTests/PersistenceSplitOptimizerTest.cs:166`) drives the production
  `RunOptimizationPass`. It prints `Split summary: rec=rec_persistence_smoke_grazing
  evaluated=4 grazeForward=2 grazeBackward=2 surfaceGrazeForward=0 ...`, byte-identical in 9
  of 9 logs that ran the cell (per the memo).
- Recommendation: add the value and claim it on LT-2, with a registry comment stating the
  step-5 / step-7 distinction.
- Cost: one required LT-2 token, an armed re-flight and one negative control
  (`grazeForward=2` -> `=3`), about 50 s each. No C#.

**D9 `rewind-to-launch-repeat` (memo section 2).**
- GS-9 already requires two backreference tokens (`GS-9-kerbalx-repeat-rewind.toml:233-234`):
  the same UT and save id across both `Rewind-to-Launch initiated` lines, and the same-id
  `Rewind: loading save` pair.
- Precedent for a value added for a newly measured property: `world-preservation`,
  `mesh-lifecycle-derender`, `watch-entry-distance-cutoff`.
- GS-9's negative control inverted `destroyLines`, not these tokens. Discharge the token's
  control OFFLINE over `2026-09-11_0109` (mutate the second `\2` to a non-matching literal and
  expect exactly one mismatch), and fly a live control on the next GS-9 flight.
- Cost: 0 flights.

**Fix (DONE 2026-09-15 in the registry PR, roadmap "Priority register (2026-09-11)" items
B1 / B2).** Both values carry their comment in `harness/coverage/registry.toml`, each is
claimed only after its armed run and its control (the wave's G4 discipline, D9's control
offline), and the coverage count is re-derived: 187 of 250 over 263 specs.

# GUI exposure audit, 2026-09-11

One batch, one branch (`gui-fixes-1`). The 2026-09-11 GUI census produced two
inventories - a structural one (105 countable surfaces, 14 IMGUI windows, 21 dialogs)
and a capability one (580 rows classed EXPOSED / AUTOMATIC / HIDDEN / DEAD /
PROMISED-NOT-DELIVERED) - and a supervisor triage split their findings into FIX NOW
(unambiguous defects: text that contradicts its handler, a guard one path skips, code
with no caller) and FILE ONLY (needs a design decision or a live lane). Every `GUI-*`
entry below is one line of that split. Struck entries were fixed in this batch, with the
evidence that found them; open entries carry the proposed fix and, where the triage said
so, a `Decision:` naming what the operator must choose.

## EVAKERBALGHOSTHASVESSELSNAPSHOT-HAS-NO-HOST-THAT-FLIES-LOW: the one `AutoRecord` cell that executes NOWHERE wants a crewed vessel FLYING low over terrain, and no committed fixture is one and no seam verb lofts one [MEASURED across the four `AutoRecord` hosts - H61 `gs1-two-stage-pad`, H68 `gs2-orbital-stack`, H69 `rover-route-recorded`, H70 `eva3-pad-3crew`, whose union executes 8 of 10. Filed 2026-09-08 as the last named unreachable cell on the in-game category axis, which was otherwise CLOSED at 112 of 112 categories; the GUI-tree dump spike opened a 113th row on 2026-09-10 and `GUI-1-census-ksc` claimed it on 2026-09-11 with one `RunTests` step, so the axis reads 113 of 113 by SPEC COVERAGE again and this cell is once more the only named unreachable one]

The cell EVAs a kerbal and asserts the resulting ghost carries a `VesselSnapshot`, then
waits 10 s for the kerbal to settle and reads its terminal. That bounds the host from
both sides: a LANDED or PRELAUNCH craft auto-seals the kerbal recording before the
assertion, an ORBITING one leaves the kerbal floating (H68 measured the guard, widened
in `Source/` to require a crewed vessel FLYING inside an atmosphere), and a
cruise-altitude craft leaves the kerbal airborne past the settle wait and reads
Splashed or nothing. What is wanted is a just-airborne or hovering crewed craft over
the KSC grass.

Needs: a forged or derived in-flight save sized to that bound. Not worth one on its own
- it buys a single cell - so this is filed as the axis's one named residue rather than
as work to schedule. Recorded in the inventory's `AutoRecord` row and its B5 residue
table.

## V26M-GHOST-SPAWN-IN-MAP-WINDOW-IS-EPOCH-DEPENDENT: the V26M lane's map-open dwell sometimes holds a painting ghost and sometimes holds none, so its paint-arm reading is not reproducible from the step list [RAISED 2026-09-07 on branch `g10-leg-drop` from reading run 3. LANE/HARNESS item, NOT a product defect. OPEN, low priority]

`V26M-interbody-route-map-lines` opens the flight map over `interbody-route-recorded` and
dwells 40 ticks. On reading runs 1 and 2 (2026-09-06) a ghost of recording `36c7688b` was
alive and PAINTING inside that window - which is what raised `routeCoDrawViolations=1024`,
then 403, and what let the per-leg paint arm be observed live at `legsDrawn=20
skippedOwned=1`. On reading run 3 (`2026-09-06_2113`, same spec, same fixture, same DLL
family) the run's `ghostLifecycle` census reads `spawned=0 spawnLines=0` (two destroys at
engine teardown, no spawns) and `paintedLegSpans` appears nowhere in the log, so all seven
draw lines read `skippedOwned=0 ownedLegs=0 paintedLegs=0`.

**WHY IT MATTERS AND WHAT IT DOES NOT MEAN.** It does not weaken the run's other readings:
the BUILD side is independent of the ghost population and came back byte-identical for the
third time, which is what the count pins rest on. What it costs is a LIVE exercise of the
paint arm on demand - run 3 exercised it only headlessly - so a future regression in that arm
would be caught by the xUnit cells and the source gates rather than by this lane. The
OWNERSHIP arm has a reproducible live lane (H59, nine `ownedLegs=1` frames on every run).

**WHAT IS NOT YET KNOWN:** which input decides it. The spec pins no clock motion and the
fixture is fixed, so the candidates are the save's own UT against the ghost spawn policy's
window, and frame timing inside the dwell. Diagnosing it means comparing the three runs'
spawn-decision lines rather than guessing, and it is worth doing before anyone tries to arm
a `paintedLegs`-bearing token on this lane. Do NOT widen a pin to absorb it: the two shapes
are distinguishable on the draw line itself (`ownedLegs=` / `paintedLegs=`), and a lane that
accepts both without saying which it saw is the vacuous reading this suite refuses.

---

## L3-CREWREPORT-BIOME-PIN-DEPENDS-ON-LANDING-SITE: `L3-career-science-recover` pins a science subject id whose BIOME the flight rolls for, so a perfectly good run reds on where the capsule stopped [FOUND 2026-09-02 by the L3 confirmation flight `2026-09-02_1824` (PARSEK-FAIL(expectation), one unmet). HARNESS/LANE AUTHORING, not a product defect. OPEN - the spec is deliberately NOT edited by the branch that found it (`l6-dwell-variants`, whose scope is the recover-dwell param); the PARSEK-FAIL stands as filed]

**What was measured.** `L3-career-science-recover` flew a confirmation run for the new
`preRecoverDwellSeconds` default and came back PARSEK-FAIL(expectation) with EXACTLY one
unmet token:

    logContracts.required not matched:
      Science subject captured: crewReport@KerbinSrfLandedLaunchPad.*reason='VesselRecovery'

The log carries the line, at the other biome:

    Science subject captured: crewReport@KerbinSrfLandedShores amount=3.0 total=3.0
        reason='VesselRecovery' ut=345.7 tag='21eb7811...' directLedger=False

`recordings.count=2` as pinned, every other verifier PASS, and the other two subject pins
(`mysteryGoo@KerbinSrfLandedLaunchPad`, `recovery@KerbinFlew`) both fired.

**Root cause: the biome is a landing-site roll, and the craft rolls across a boundary.**
The Jumping Flea's pad hop lands back beside the pad, on the LaunchPad/Shores boundary, and
it is still MOVING after touchdown (`Section started: env=SurfaceMobile ... at UT=342.30`).
A science subject's id is stamped where the experiment is taken, so which biome a given
experiment carries depends on where the capsule happened to be at that instant. THE
DECISIVE EVIDENCE IS INSIDE THE ONE RUN: `crewReport` read `Shores` and `mysteryGoo` read
`LaunchPad` two seconds apart on the same craft. The prior recovery run
(`2026-08-19_2220`) had both at `LaunchPad`, which is why the pin was written that way and
why it has held until now.

**What is NOT wrong with the pin.** The `reason='VesselRecovery'` half - the half it was
written to catch, against CAREER-TRANSMIT-SCIENCE-EMITS-NO-CORROBORATING-EVENT, where the
2026-08-19 reading run read `reason=''` - PASSED. Only the biome half of the subject id is
fragile. Any fix must keep the reason half literal.

**Fix (harness side), two options, neither attempted here:**
1. **Accept the landed biome CLASS.** Widen the pin to
   `crewReport@KerbinSrfLanded[A-Za-z]+.*reason='VesselRecovery'`. Cheap, keeps the
   load-bearing half literal, and still fails a subject captured in flight
   (`KerbinFlying...`) or with the wrong reason. Loses the (never load-bearing) claim about
   which biome.
2. **Assert the biome from the mission's own landing telemetry.** The mission already
   observes the landed situation; having it report the landed biome would let the lane
   pin the subject id against what the flight actually did rather than against a prior
   run's luck. Strictly better, and strictly more work: it needs a new telemetry channel,
   a schema key and its own reading run.

Option 1 is the proportionate one unless a lane appears that genuinely needs the biome.
Whoever takes it re-flies L3 once to confirm, and updates this entry and the L3 status row
in the same commit.

## FIXTURE-DEPOT-ROUTE-RECORDED-LANE-PENDING: the G1/B27 route subject is harvested, repaired and registered, but no scenario has flown against it yet [OPENED 2026-08-26 on branch `route-harvests`. TODO, not a defect]

`harness/fixtures/saves/depot-route-recorded` is committed: the operator's own
free-play sandbox save `orbital supply route DELIVERY test`, harvested
`--expect-situation ORBITING --keep-parsek` and finished by
`harness/tools/build_depot_route_recorded.py`. It is **the first committed
fixture carrying a `ROUTE`** - `5420f805...`, `status = Active`,
`completedCycles = 1`, `isKscOrigin = True`, SameBody Kerbin -> Kerbin
(`dispatchWindowPeriod = 0`), `dispatchInterval` / `transitDuration`
16,058.001895760137 s, `recordedDockUT` 17,478.248634212287, a DockingPort STOP
onto the `Depot` (pid 3620499050) with a LiquidFuel/Oxidizer delivery manifest.
Two whole recording trees, 22 recordings, 86 authoritative sidecars. Pins live in
`test_saveparse.RECORDED_FIXTURES["depot-route-recorded"]` plus the builder's own
`verify_route`; the recipe is guarded by `DepotRouteRecordedFixtureDriftTests`.

It reads GREEN under `analyze-recordings.ps1 -FailOnRed -FreshSaveGate`
(`FAIL=0 WARN=0 INFO=0 STALE=0 BASELINED=0 RED=0`) after a two-section INV2
containment repair on the Transporter's chain segment `a85a7ae0...` - the only
RECORDED fixture in the suite with a zero-WARN reading.

**Why it is a harvest and not a forge**, restated here because it is the thing a
reader will question: route candidacy is gated on `IsTreeFullySealed` and, AT HARVEST
TIME, both verbs that could satisfy it (`SealSlot`, `RouteCommand`) were RESERVED
command-seam verbs (H35 ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH), so no driven run
could create a ROUTE at all. The register amendment is on the G1 entry in
`autotest-roadmap.md`; B27 is a FORGE-CLASS STAMP and the flight variant was deferred
behind those two verbs. **Both verbs shipped 2026-08-30**, so the deferral is lifted as a
CAPABILITY - but this fixture stays a harvest until a driven seal -> create run actually
produces one, and nothing here should be re-read as forged in the meantime.

**Three residuals, all deliberate, all non-gating:**

- **No `Ships/` at all.** Dropped by the builder: the source carried the
  operator's edited `Kerbal X.craft` plus KSP's `Auto-Saved Ship.craft` VAB
  autosave, and this is a render subject that launches nothing (the same choice
  `duna-one-recorded` makes). A launching lane must add a `shared-ships.toml` row
  or re-harvest with `Ships/` kept.
- **`activeVessel` was re-pointed**, from the source's asteroid `Ast. YRJ-552`
  (index 0) to `Depot` (index 9). The harvest's own focusability gate PASSES on
  an asteroid, so nothing upstream would have caught it; the drift test
  re-resolves the index by name + pid.
- **The route is pinned BUILDER-side, not in `saveparse`** - see the improvement
  entry below.

**What is owed**: the V18T / V18M lanes. **V18T can be authored first with no
`EnterMapView` verb** - measured, not assumed: `RouteTrajectoryLineRenderer.DrawAll`
has one production call site (`Display/GhostTrajectoryPolylineRenderer.cs:3894-3906`,
the `Camera.onPreCull` route slot) whose only guards are the planetarium-camera
identity check, `scene is TRACKSTATION or FLIGHT`, and a per-frame de-dupe. No
`MapView.MapIsEnabled` anywhere on that path; the GHOST polyline pass is the
map-gated one (`:4014`). Headline arming facet when the lane exists:
`routeLineBuilds { min = 1 }` (the first non-zero in the suite; emitted by
`RenderCompositionRecorder.NoteRouteLineBuild` on an actual cache rebuild, which
a post-load first draw always is because `OnGameStateLoad` clears the cache) plus
`routeCoDrawViolations { max = 0 }`. Registry D10 `route-map-lines` stays
UNDECLARED until a GATING token earns it. Until a lane flies, nothing reads these
bytes.

**V18T FLEW AND IS ARMED (2026-08-26).** Both headline windows landed as written
above - `routeLineBuilds { min = 1 }` (measured 1 on all three flights, the
suite's first non-zero reading of that census anywhere) and
`routeCoDrawViolations { max = 0 }` - and the negative control
`2026-08-26_2017_a2` red on exactly `PARSEK-FAIL(render-composition)` /
`renderComposition.routeLineBuilds 1 < min 5`. THE GATING TOKEN NOW EXISTS, so
D10 `route-map-lines` IS DECLARED on that lane in the same commit. V18M (the
FLIGHT-map half, which does owe `EnterMapView`) is still owed.

## FIXTURE-DUNA-PARK-RECORDED-LANE-PENDING: the heliocentric-parking-departure subject is harvested, repaired and registered, but no scenario has flown against it yet [OPENED 2026-08-26 on branch `route-harvests`. TODO, not a defect]

`harness/fixtures/saves/duna-park-recorded` is committed: tree `ced78481...`
("Kerbal X #2") out of the SAME operator save `duna-one-recorded` came from
(`logs/2026-08-25_1537_s15-duna-one-manifest-run2`), harvested
`--expect-situation PRELAUNCH --keep-parsek` and stripped to one tree / one
mission / 14 recordings by `harness/tools/build_duna_park_recorded.py`. The two
fixtures are disjoint payloads out of one harvest.

**Why it is a separate subject and not a duplicate of `duna-one-recorded`**, which
is the whole point of the entry: they are two DIFFERENT WAYS OF GETTING TO DUNA.
`duna-one-recorded` is a DIRECT transfer - it parks in KERBIN orbit, ejects, and
its three Sun segments are one conic split by warp (sma 17,604,964,389.77
throughout), so its departure burn happens inside Kerbin's SOI. THIS one is a
HELIOCENTRIC PARKING DEPARTURE, the operator's own "orbits the star until
alignment is good": its transfer `aa48920e...` (856 points, the largest recording
in the source save) escapes Kerbin almost immediately and then coasts on ONE Sun
orbit across three consecutive segments at sma 14,072,049,898.09 / ecc 0.0326934
(agreeing to ten significant figures) for 13,502,219.94 s - about 156 Kerbin days
at 3.5% outside Kerbin's own heliocentric sma, i.e. a PHASING orbit. The
departure burn is then an element STEP at UT 2,561,070,900.03 to sma
17,908,765,008.46 / ecc 0.19216 (+27% sma, +488% ecc). Duna SOI entry at
2,570,454,935.62, hyperbolic (ecc 3.6025), capturing into an ellipse at
2,570,492,255.34.

That property lives in the transfer's `ORBIT_SEGMENT` list, which NO saveparse
facet reads, so `RECORDED_FIXTURES` alone cannot tell the two subjects apart -
which is why `DunaParkSignatureTests` reads it directly and asserts the park run,
the departure step, the Duna arrival, AND (from the other side) that the direct
sibling still has no such park.

It reads GREEN under `analyze-recordings.ps1 -FailOnRed -FreshSaveGate`
(`FAIL=0 WARN=16 RED=0`) after a four-section INV2 containment repair on
`aa48920e...`; the 16 WARNs are the same INV8 phantom-attribution class
`duna-one-recorded` carries 15 of, from the restored whole-career `ledger.pgld`.

**What is owed**: a lane of its own. The rendering question it exists to ask - how
Parsek renders a ghost that sits on a heliocentric parking orbit for 156 days and
then departs - is unmeasured, and no committed subject other than this one can
ask it. Until a lane flies, nothing reads these bytes.

## M-A7-RENDER-COMPOSITION-PHASES-3-AND-4: what the render-composition manifest still owes after Phases 1-2 landed, plus the four deferrals those phases took deliberately [OPENED 2026-08-25 with the M-A7 Phases 1-2 PR. TODO, not a defect. Status authority for the module is `docs/dev/autotest-status.md`; the design is `docs/dev/design-autotest-render-composition.md`]

**PHASE 3C - THE CORPUS-WIDE DECLARATION WAVE (the operator's stated architecture,
recorded 2026-08-26).** The B-flights generate recordings, the V-lanes loop them
synthetically at new UTs, and the composition manifest audits the composed render -
so the rollout is declaration, not new machinery. Plan, in waves: (A) batch-declare
the bare `[expectations.renderComposition]` block + export step + tracer steps
(where missing) across the eligible V-M lanes (V7M, V9-V13, V15M, V16M, V17M, V19M,
V21M, V22M, V23M) plus the first T-host lane (`V14T`, the TS pilot - NO tracking-
station-host manifest has ever been captured) and the K-host lane (`V22K`); each
declaration changes that lane's anomaly exposure where tracers were previously off,
which is why nothing arms at declaration. (B) readings accumulate FOR FREE via
normal tier cadence once declared - every flight of a declared lane produces facets
into its results JSON; targeted flights only where cadence is too slow. (C) arm in
batches off accumulated readings with per-lane windows, the established three-run
discipline per lane. Gates that must exist first for specific lanes: the map-open
seam verb (RC-OWN-DRAW-HALF-IS-MAP-GATED) before any lane whose subject carries
TracedPath phases arms the ownership clauses.

~~(A) batch-declare ... across the eligible V-M lanes ... plus `V14T` and `V22K`~~
**WAVE A EXECUTED 2026-08-26 - ALL FIFTEEN LANES ON THE LIST DECLARED, ZERO
DEFERRALS, NOTHING ARMED.** The declarer roster goes 4 -> 19: V7M, V9, V10, V11,
V12, V13, V15M, V16M, V17M, V19M, V21M, V22M, V23M on the flight-map host, plus
`V14T-ike-ts-arrival` (the FIRST tracking-station-host manifest the module has ever
been able to take) and `V22K-kerbin-splashdown-ksc-arrival` (the FIRST KSC-host
one). Per lane the edit is exactly three things and no more: one
`ExportRenderManifest` step immediately before `FlushAndQuit`, a bare
`[expectations.renderComposition]` last in `[expectations]`, and a compact
renderComposition arming ledger in the header - no step, no jump UT, no budget and
no existing expectation moved (the S4.1 rule). THE ONE EXPOSURE CHANGE the plan
anticipated landed on exactly two lanes: V14T and V22K armed only `mapRenderTracing`
+ `verboseLogging`, and `test_every_declarer_arms_the_tracers_the_seam_capture_needs`
requires all three of every declarer, so both gained `ghostRenderTracing`. On those
two it buys NO manifest content (every capture predicate the recorder reads is
`MapRenderTrace.IsEnabled`-gated) and adds only a previously dark FLIGHT-scene
anomaly surface - `loop-seam-teleport` and the GhostRenderTrace raises - against
tight sweeps (V22K `allowedAnomalies = []`, V14T tolerating only `icon-off-orbit`);
a red on either reading run from a newly gated raise is a TRACER-ARMING READING to
record, not a regression to re-diagnose. Two expected-thin classes are pre-registered
so an arming pass does not misread them: the four ARM-ONLY lanes (V9 / V11 / V12 /
V13) quit ~1 s after `StartLoopPlayback` and are the corpus's FLOOR case, and the two
LANDED-TERMINAL subjects (V22M / V23M) render flight-mesh only. Verified with
`discover -s lib` green (1709 tests) and `run.py --dry-run` exit 0 on all fifteen,
each showing `PARSEK_RENDER_MANIFEST=1` at [LAUNCH] and
`renderCompose(report-only: renderComposition; declared: {})` at [VERIFY], with the
three armed lanes still reading `armed:` and V6M still report-only. Roster and full
rationale: `RENDERCOMPOSE_DECLARER_SPECS` in `harness/lib/test_hlib.py`.
**NOW OWED: (B)** let readings accumulate off the normal tier cadence and take
targeted flights only where cadence is too slow (V6M is ahead of the wave: its
reading flew 2026-08-25 - though it now owes a SECOND one, since it gained the
map-open pair on 2026-08-26 and the first reading measured a shape it no longer
flies; see the RC-OWN entry and the Phase-3b bullet);
**(C)** arm in batches off those facets, per
lane, on the three-run discipline. The map-open seam-verb gate above is
PARTIALLY lifted: the verb exists (PR #1539) and V6M now drives it, but until
that re-fly reports `ownershipChanges > 0` no ownership clause may be armed
anywhere - a step added is not a reading taken.

**WAVE B ADDED TWO MORE DECLARERS 2026-08-26, ROSTER 19 -> 21, and they are a
different kind from Wave A's:** `V18T-depot-route-ts-arrival` and
`V25M-duna-park-player-loop` are NEW LANES against the two Phase-4 harvest
fixtures rather than declarations bolted onto lanes that had already flown, so
each owes a FIRST FLIGHT before it owes a window. Details in the Phase 4 bullet
below; rationale in `RENDERCOMPOSE_DECLARER_SPECS`.

**ARMED LANES 3 -> 5 ON 2026-08-26: `V25M-duna-park-player-loop` and
`V6M-mun-player-loop` both closed the full three-run discipline the same day, so
the armed roster is now V14M / V8 / V24W / V25M / V6M against 21 declarers.**
  * V25M armed off THREE readings of one unchanged drive shape - `2026-08-26_1744`
    (full measurement), `_1817` (red on `line-blink`, and the run that validated
    the RC-SEAM verifier fix live at zero FAIL findings) and `_1823` (the clean
    PASS). Structure equal to the integer on all three (dwells 3 +2 open, cycles
    0, treatments StockConic 2 / TracedPath 1, seams rigid 8 / flexible-soi 2);
    only the unevaluable census moved, 384 / 410 / 409. WINDOWS `dwells {1,32}`,
    `unevaluable {max 1400}`, `requireSeamKinds [rigid, flexible-soi]`, with
    `cycles` deliberately omitted on the V8 pattern (this subject closes zero
    cycles, so a floor would red the runs it was armed off). The ceiling is ~3.4x
    the largest reading - the siblings' ratio-to-measurement SCALED to a
    ~400-record endpoint population, not copied from their 200 / 250. ARMED
    RE-FLIGHT `2026-08-26_1837` PASS, zero mismatches, unevaluable 388 (inside the
    readings' own spread). NEGATIVE CONTROL `2026-08-26_1839`
    `PARSEK-FAIL(render-composition)` off a temporary `dwells = { min = 50 }`,
    EXACTLY ONE mismatch (`renderComposition.dwells 3 < min 50`), every sibling
    row clean, reverted in the same change.
  * V6M armed off THE PAIR `2026-08-25_2056` (map closed) + `2026-08-26_1745`
    (map open), which bracket the one change the lane made between them. WINDOWS
    `dwells {1,32}`, **`cycles {min 2, max 16}` - the floor no other lane in the
    suite can carry**, `unevaluable {max 300}` sized off the map-open reading (and
    clearing the map-closed 110 besides), `requireSeamKinds [rigid,
    flexible-soi]`. The `cycles` floor was CHECKED for vacuity rather than
    assumed: neither census carries `no-dwells-attributable-to-unit`, so the five
    closed dwells really were attributed to the unit and the isomorphism
    comparison really ran. ARMED RE-FLIGHT DISCHARGED FOUR TIMES (`_1838`,
    `_1842`, `_1843`, `_1844`, all PASS attempt 1, zero mismatches,
    `ownershipChanges = 6` on every one - the RC-OWN closure now rests on FIVE
    map-open flights). NEGATIVE CONTROL `2026-08-26_1840` red on exactly
    `PARSEK-FAIL(render-composition)` off a temporary `cycles = { min = 9 }`,
    sibling rows clean, reverted in the same change.
  * TWO FINDINGS CAME OUT OF THE PASS AND BOTH ARE FILED AT THE TOP OF THIS FILE
    RATHER THAN BURIED HERE: `V6M-CYCLE0-ARRIVALLOITER-DWELL-CLOSE-RECORD-LOST`
    (the V6M control's SECOND, uninvited mismatch, DIAGNOSED the same day off all
    six archived manifests: the cycle-0 ArrivalLoiter dwell renders identically on
    every flight and it is its CLOSE RECORD that is lost, when the sparsest-
    sampling run gets zero frames in the few-frame `7 -> -1` inter-cycle-tail
    state. A recorder bookkeeping gap, NOT renderer intermittency and NOT a
    jump-target race - which undercuts the reason V6M's arming was justified with,
    so that decision is referred back in the entry) and
    `RENDERCOMPOSE-OWNERSHIPCHANGES-IS-NOT-WINDOWABLE` (the RC-OWN premise could
    only be armed through the FAIL-finding gate, because `ownershipChanges` was a
    recorded facet and not one of the three windowable keys). BOTH ARE NOW CLOSED:
    the recorder fix landed the same day with a direct determinism proof, and the
    windowable gap closed in two halves - the schema on `window-facets-arm-v18t`,
    then V6M's own arming pass on `v6m-ownership-window` (`ownershipChanges =
    { min = 1 }`, re-flight `2026-08-26_2042` PASS, control `2026-08-26_2043`).
  * The controls were NOT shared: V25M inverted `dwells`, V6M inverted `cycles` -
    a clause V25M's block does not even carry - so each red lands on its own armed
    window rather than re-proving the shared `rendercompose` evaluator. Both were
    applied by LINE-ANCHORED edits of the real key, each verified with
    `grep -n '^<key>'` AND through `run.py --dry-run`'s `declared:` line before
    launch, which is the standing discipline since
    `NEGATIVE-CONTROL-EDIT-NEVER-REACHED-THE-KEY`.


Phases 1-2 shipped the C# recorder (env-gated, `ExportRenderManifest` verb,
scene-exit flush), the pure Python verifier `harness/lib/rendercompose.py`, and
the `renderCompose` verifier row REPORT-ONLY. FOUR committed scenarios now
DECLARE `[expectations.renderComposition]`. THREE of them (see the Phase-3
bullet) flew their report-only reading runs on 2026-08-25 - so the module is
live-proven on real game manifests - and ALL THREE OF THOSE BLOCKS WERE ARMED
that day off those readings (windows in the Phase-3 bullet), EACH LANE THEN
CLOSING THE FULL THREE-RUN WORKFLOW THE SAME DAY: V14M and V8 with six runs
between them, and V24W - the RC-WARP lane - with six of its own (three readings,
two armed re-flights, one negative control). **PHASE 3 IS THEREFORE COMPLETE AND
ITS LAST DEBT, RC-WARP, IS DISCHARGED**; D14 `warp-rails` coverage is real rather
than claimed, because the gate behind it (`warpBuckets`, armed on V24W alone) now
reds a run whose rails buckets come back empty. THE FOURTH DECLARER,
`V6M-mun-player-loop`, was authored 2026-08-25 as a BARE UNARMED block and still
owes its reading run - see the Phase-3b bullet: it is the MUN subject the
operator asked about, and the suite's first TWO-COMPLETE-CYCLE RC-CYCLE dataset.
WHAT REMAINS OWED for this module is that lane's reading + arming (Phase 3b),
Phase 4's route surfaces (via G1's B27/V18), and the parked
instrument-calibration items listed at the end of this entry - nothing in the
original Phase 3.

REMAINING PHASES.

- **Phase 3 (lanes). COMPLETE 2026-08-25 - three lanes declared, armed and
  discipline-discharged, RC-WARP included; nothing in this bullet is still owed.**
  ~~Extend two committed V lanes with an
  `[expectations.renderComposition]` block~~ LANE EXTENSIONS AUTHORED 2026-08-25:
  `harness/scenarios/V14M-ike-player-loop.toml` (the phase-lock moon loop,
  V6/V14 class) and `harness/scenarios/V8-eve-player-loop.toml` (the re-aim
  interplanetary landing loop, V8/V13 class) each gained ONE
  `ExportRenderManifest` step immediately before `FlushAndQuit`, plus a BARE
  report-only block - no `gating`, no assertion key - so the windows get authored
  FROM the first manifests' facets instead of predicted. Both lanes already armed
  the three tracers the seam capture needs (`ghostRenderTracing` /
  `mapRenderTracing` / `verboseLogging`), and NOTHING ELSE in either flown shape
  moved: no jump UT, no pacing spacer, no budget, no expectation. The DECLARER
  pin moved with them - `test_no_committed_spec_declares_the_block_yet` became
  `RenderComposeVerifierWiringTests.RENDERCOMPOSE_DECLARER_SPECS` naming both
  files, joined by three cells pinning that no declarer arms gating, that every
  declarer arms the three tracers, and that every declarer exports immediately
  before teardown. `RENDERCOMPOSE_ARMED_SPECS` is still EMPTY on purpose -
  deliberately NOT named `*ARMED_ALLOWLIST`, because the save-structure roster is
  scraped out of that file's source by a first-match regex on that name.
  ~~STILL OWED: the report-only READING RUN for each lane (nothing has yet flown
  with `PARSEK_RENDER_MANIFEST=1`)~~ BOTH READING RUNS FLOWN 2026-08-25, BOTH
  PASS, BOTH REPORT-ONLY - the module is live-proven on real game manifests:
  `2026-08-25_0953_V14M-ike-player-loop` (PASS attempt 1, wall 68 s, 22/22 steps)
  and `2026-08-25_0956_V8-eve-player-loop` (PASS attempt 1, wall 53 s, 31/31
  steps), each `renderCompose status=REPORT gating=false armedBlocks=[]
  mismatches=[]` with exactly ONE INFO finding (RC-QUAL's endpoint-ratio trend
  line) and zero WARN / zero FAIL. Facets and the two instrument observations are
  recorded in `docs/dev/autotest-status.md` -> M-A7 and in each spec's own arming
  ledger; the headline pair is V8's `hold-engage`/`hold-release` observed at 1x
  (the program's first) and V14M's all-`StockConic`, 1x-only, 107-endpoint
  reading.
  ~~ARMING DECISION PENDING OPERATOR - no window has been authored off the
  reading facets and both blocks stay BARE~~ ~~ARMING IN PROGRESS: BOTH BLOCKS
  ARMED 2026-08-25, ARMED RE-FLIGHT + NEGATIVE CONTROL STILL OWED~~ **ARMING
  DONE AND VALIDATED 2026-08-25: both blocks armed AND both lanes discharged the
  full three-run workflow the same day.** The operator
  took the call off exactly the reading facets, nothing else, and nothing in
  either flown shape moved (the S4.1 rule).
    * V14M: `dwells {1,32}` (measured 3), `cycles {1,16}` (measured 1 CLOSED
      cycle), `unevaluable {max 200}` (measured 56), `requireSeamKinds
      ["rigid","flexible-soi"]` (measured rigid 14 / flexible-soi 2).
    * V8: `dwells {1,32}` (measured 2), `unevaluable {max 250}` (measured 76 over
      a 272-record endpoint population - 2.5x V14M's, hence the proportionally
      higher ceiling), `requireSeamKinds ["rigid","flexible-soi"]` (measured
      rigid 6 / flexible-soi 4). `cycles` DELIBERATELY OMITTED: that subject
      closed ZERO cycles, so a floor would red the run it was armed off and a
      `{min = 0}` pin can never red at all.
    The count floors plus `requireSeamKinds` are the anti-vacuity halves; the
    ceilings are runaway guards, not pins, because dwell and endpoint counts move
    with frame timing. `warpBuckets` on neither, ever. Rosters and per-lane armed
    KEY-SET pins: `RENDERCOMPOSE_ARMED_SPECS` +
    `test_v14m_declares_the_render_composition_block_armed` /
    `test_v8_declares_the_render_composition_block_armed_without_a_cycles_floor`
    / `test_every_armed_block_keeps_an_anti_vacuity_floor` in
    `harness/lib/test_hlib.py`, where `test_no_declarer_arms_gating_yet` was
    replaced (per its own docstring discipline) by
    `test_every_declarers_arming_state_matches_the_recorded_rosters`.
  ~~STILL OWED: the rest of the three-run arming workflow per lane (armed
  re-flight -> negative control, the latter shareable across the pair per the
  V4/V5/V14 shared-evaluator precedent) and the per-criterion negative
  controls.~~ **DISCHARGED 2026-08-25 - six runs, both lanes:**
    * V14M: reading `2026-08-25_0953` -> armed re-flight `2026-08-25_1050` PASS
      (gating=True, ZERO mismatches; dwells 3, cycles 1, seamKinds rigid 14 /
      flexible-soi 2, unevaluable 108, one INFO RC-QUAL) -> negative control
      `2026-08-25_1052` `PARSEK-FAIL(render-composition)` off a temporary
      `cycles = { min = 5 }`, single mismatch
      `renderComposition.cycles 1 < min 5`, reverted in the same change.
    * V8: reading `2026-08-25_0956` -> armed re-flight `2026-08-25_1051` PASS
      (ZERO mismatches; dwells 2, cycles 0, seamKinds rigid 6 / flexible-soi 4,
      unevaluable 78, one INFO RC-QUAL; the `hold-engage`/`hold-release` pair
      and the `reaim-window` event both REPRODUCED) -> negative control
      `2026-08-25_1054` `PARSEK-FAIL(render-composition)` off a temporary
      `dwells = { min = 50 }`, single mismatch
      `renderComposition.dwells 2 < min 50`, reverted in the same change.
    The controls were NOT shared: each lane inverted a window of its OWN, so
    each red lands on that lane's armed clause rather than re-proving the shared
    `rendercompose` evaluator, and on both controls every sibling verifier row
    (saveParse / anomalySweep / driverValidity / logValidate / analyzer) stayed
    PASS. SCOPE (2026-09-10): these two controls discharge each lane's
    RENDER-COMPOSITION lens only. Criterion (b)'s logContracts destination lens was
    discharged on 2026-09-10 by V14M `2026-09-10_1910` (ProtoOrbitLine
    `body=Ike` -> `body=Eeloo`, drift gate 2/0) and V8 `2026-09-10_2039` (census
    `outsideSoi=0` -> `outsideSoi=9`, drift gate 1/0), each PARSEK-FAIL(expectation)
    on exactly the inverted element (V-PAIR-CRITERION-B-RENDER-CONTROL-DEBT, done).
    Also measured: V14M's armed run confirmed the sticky-bit fix
    (`mapRenderTracingOn=true`, `seam-data-unavailable-tracing-off` gone) while
    its unevaluable TOTAL rose 56 -> 108 on `seam-endpoint-skipped` variance
    (106 vs 53) - inside the `{max 200}` runaway guard by design.
  Arming stays an operator decision taken only
  after a report-only reading run whose facets match the declared windows,
  exactly as R9's `[expectations.rewind]` arming was. One MEASURED constraint on
  that decision, HONOURED IN THE ARMING: V8's reading returned
  `cut-run-period-absent: 1`, so RC-CUT's
  whole-ratio check could not evaluate the corpus's only non-zero loiter cut (no
  run period to divide by; `cutWholeRatios` came back empty). An RC-CUT window
  armed off that reading would arm a clause that never fires, so NO RC-CUT
  surface was armed - nor any RC-HOLD clause, one observed engage/release pair
  not being a window.
  ~~STILL OWED AFTER THE ARMING, and the ONLY Phase-3 debt left: the design's
  **warp schedule** bullet (RC-WARP)~~ **DISCHARGED 2026-08-25 by V24W's own
  six-flight discipline - see the RC-WARP block below.** Both subjects move the clock with
  instantaneous `TimeJump`s, so their warp histogram is 1x-only by construction
  and `warpBuckets` must never be declared on either. RC-WARP is satisfiable only
  by the V1/autopilot rails-warp ladder shape, which wants a third lane (or a
  re-shaped drive) rather than a re-pin of these two. CONFIRMED FOUR TIMES: the
  histograms came back 1x-only on both reading runs (173 samples on V14M, 296 on
  V8) and on both armed re-flights (177 and 295), every other bucket zero.
  **COMPLETE 2026-08-25 - THE LANE IS AUTHORED, THE MEASUREMENT IS TAKEN
  (reading flights 2 and 3, below), THE BLOCK IS ARMED OFF THAT PAIR, AND THE
  ARMED RE-FLIGHT (`_1722`, plus `_1811` which was meant to be the control and
  never armed) AND THE NEGATIVE CONTROL (`_1925`,
  `PARSEK-FAIL(render-composition)` on `RC-WARP [FAIL] warpBuckets.warpHigh`)
  HAVE BOTH FLOWN.**
  ~~which wants a third lane~~ the third lane exists:
  `harness/scenarios/V24W-duna-one-warp-stair.toml`, a loop-arrival map dwell
  over `fixtures/saves/duna-one-recorded` (the harvest of the first free-play
  ground-truth session - the one whose histogram, `warp100 4727 / warp1000 15488
  / warpHigh 11446` with zero 1x, is what proved this subject exists outside a
  hypothesis) driving a COMMANDED rails stair
  (10x/50x/100x/1000x/100x/50x/10x, up and back down, each step held 10 poll
  frames) at each of its three span-clock windows. `V24` is reserved in
  `autotest-roadmap.md`'s register and MINTS the `W` warp-schedule suffix
  alongside `M`/`T`/`K` - `W` names a drive shape rather than a render host,
  which is why the lane may share the flight map with V2 and still be a separate
  id. The stair is a FLAG-GATED extension of `m3_loop_arrival_dwell`
  (`dwellRampFactors` empty by default = the pre-RC-WARP machine, byte-identical
  action stream / state / assertion rows, pinned by
  `M3WarpStairInertnessTests`), so V2 / V3F / V3R and both armed composition
  lanes are untouched.
  **READING FLIGHT 1 FLEW 2026-08-25 (run `2026-08-25_1415`, PASS/REPORT) AND
  MEASURED AN EMPTY OBSERVATION.** Zero `GhostCreated` lines in the collected
  KSP.log, zero dwells / seams / histogram buckets in the manifest, while the
  recorder's own clock events (two cycle-rollovers, a hold-engage, two
  inter-cycle-tail entries) proved the unit clock ran. TWO ROOT CAUSES, both
  fixed, neither in Parsek's render pipeline:
  (1) **The dwell windows rode the RECORDED clock while the loop clock
  COMPRESSES.** A re-aim unit excises whole-period loiter intervals
  (`GhostPlaybackLogic.CompressSpanUT`); this unit logged `loiterCuts=1
  cutSeconds=11393869 compressedSpan=7001129/18394999`, and all three declared
  offsets (11.47M / 18.33M / 18.39M) exceeded the 7.00M compressed span, so
  every dwell sat in the inter-cycle tail where the clock parks at spanEnd and
  nothing renders. The cycle advance itself was correct - `phaseAnchorUt` was
  forward-advanced by the C# - so the arithmetic looked right and was not.
  ~~Fix~~ FIXED: the `MissionConfig` seam now publishes the unit's cut list and
  clock primitives (`loiterCutCount`, `loiterCuts` as `startOffset:length`
  pairs relative to `spanStartUt`, `compressedSpanSeconds`, `spanSeconds`), and
  `m3_loop_arrival_dwell` compresses every recorded offset through them at the
  single seam where an offset becomes a live UT (`_m3_window_ut`). Spec params
  stay on the RECORDED clock by design - mapping them is the machine's job.
  An offset that lands INSIDE a cut, or at/past the compressed span, is now a
  NAMED give-up at ARM time (a spec authoring error, refused rather than
  silently clamped: the cut collapses to one compressed instant, so clamping
  would green a run that dwelled somewhere other than the instant its own
  ledger claims). An older DLL that publishes no cut keys behaves exactly as
  before, pinned by a cell. **V2 / V3F / V3R were NOT latently affected** and
  this is a measurement, not an assumption: their subject
  `duna-direct-recorded` logs `loiterCuts=0 cutSeconds=0
  compressedSpan=4506891/4506891` on all nine collected flights, and V2's own
  logs carry 362 `ghosts=1` probe frames plus `body=Duna` x1421 /
  `body=Sun` x740 / `body=Kerbin` x392 - real observations, not vacuous
  passes. A direct transfer never parks long enough for
  `ReaimLoiterCompressor.ComputeCuts` to detect a >1-rev loiter run, which is
  why V24W is the first affected lane.
  (2) **The three "1x holds" never left rails warp**, so the mission cost 455
  wall seconds against the ~2,850 its own spec sized. `ACTION_CANCEL_WARP` in
  `harness/missions/mission_runner.py` zeroed the warp factors only through
  `WarpService.cancel`, and `self._warp` is created ONLY by
  `ACTION_WARP_TO_UT`. `m3_loop_arrival_dwell` moves the clock with seam
  `TimeJump` epoch shifts by design, so it had no warp service and the handler
  was a no-op: 128 consecutive `warp=RAILSx10.000` telemetry frames per hold,
  all four issued cancels logging nothing, `allow_rails_warp=True` tolerating
  the residual state, and a PASS that never once dwelled at 1x. It is the first
  mission in the suite to pair `SET_RAILS_WARP` with `CANCEL_WARP` without
  `WARP_TO_UT`, which is why a latent runner gap surfaced here and nowhere
  before. ~~Fix~~ FIXED: the primary-connection factor reset is now
  unconditional, which is what the handler's own comment already claimed;
  idempotent with `WarpService.cancel` where a service does exist.
  WHAT WAS OWED AFTER FLIGHT 1 (every leg now discharged): a
  RE-FLY of the reading run (flight 1's observation measured nothing about
  Parsek) - flown as flights 2 and 3 below - then `warpBuckets` written FROM the
  measured histogram - armed 2026-08-25 off the flight 2 + flight 3 pair - then
  the armed re-flight and a negative control inverting a required token of this
  lane's own - flown 2026-08-25 as `_1722` / `_1811` and `_1925` respectively.
  **READING FLIGHT 2 FLEW 2026-08-25 (run `2026-08-25_1502`,
  PARSEK-FAIL(anomaly) attempt 1) AND MEASURED THE FULL COMPOSITION. THE RED IS
  THE PRE-REGISTERED DOCTRINE OUTCOME, NOT A LANE DEFECT.** All ten driver steps
  met; every other verifier row green (driverValidity PASS, mission MISSION-OK,
  analyzer PASS red=0, logValidate PASS, saveParse REPORT, renderCompose REPORT
  parsed). Wall 2,895 s total / 2,817 s mission - within 2 % of the ~2,850 s the
  spec sizes and inside the unchanged 3000 / 3600 budgets, which CONFIRMS the
  flight-1 warp-cancel fix on the cost line and not just in code. Both flight-1
  root causes are measured closed: all three dwell windows now land inside the
  compressed span and RENDER (2 closed dwells + 2 open at export, `treatments
  StockConic 2`, `coverages InSegment 2`, `transitions 2`, `chainBuilds 2`,
  `lineBranches 2`, `cycles 1`), and the histogram is a real stair -
  `warp1x 322078 / warp100 10602 / warp1000 2170 / warpHigh 0 / warpPhys 0` with
  **`holdsAboveOneX = 1` and `seamsAboveOneX = 2`**, the first time any committed
  subject has put a cataloged seam AND a hold above 1x. That is RC-WARP's
  anti-vacuity statement satisfied by measurement; what remains before it can be
  ASSERTED is the arming. Other facets, recorded because the arming pass writes
  its windows from these and nothing else: seams `rigid 11` / `flexible-soi 4`,
  `seamEndpoints 1024`, `seamTangents 0`, `maxEndpointRatio 0.4366947421168355`;
  clock events `cycle-rollover 2` / `hold-engage 1` / `hold-release 1` /
  `inter-cycle-tail 1` / `reaim-window 1`, with the observed hold 7050.236864 s
  TWICE against a per-cycle plan of [39607.052804, 35444.407972] (an ~18-20 %
  observed/planned ratio at BOTH plan units - a measurement handed to RC-HOLD,
  deliberately not armed off one engage/release pair); decimation `SEAM_ENDPOINT
  292546` + truncated `41280` + skipped `512` driving `unevaluable 334342`, so
  the endpoint census on this drive shape is a SAMPLED population and no count
  window may be written over it; findings 4 x INFO `RC-QUAL` and nothing worse
  (zero FAIL, zero WARN, zero mismatches, `ownershipChanges 0`); recordings count
  13, equal to the fixture's own population, so the optimizer split nothing at
  the four cross-body seams and nothing was lost.
  ANOMALY CEILINGS AUTHORED OFF THIS RUN: `hitCounts {icon-teleport: 65,
  icon-off-orbit: 2, loop-seam-teleport: 2}`, and all three are now tolerated in
  the spec's `allowedAnomalies` with `2026-08-25_1502` cited on each, per the
  S1.4 "a token is added WITH the flight that shows it" rule. THEY ARE BARE, NOT
  BUDGETED, and the reason is a whole-suite property rather than a call about
  this lane: the ceilings the measurement authorizes are 130 / 8 / 8
  (icon-teleport doubled off 65 because the probe threshold defect is open and
  this lane commands rails; the other two 4x off 2, bounding a per-seam raise
  without tolerating a per-frame one), and
  `test_no_committed_spec_arms_a_count_budget` +
  `test_every_committed_spec_parses_under_the_budget_surface` in
  `harness/lib/test_hlib.py` hold the `maxCount` mechanism INERT across every
  committed spec. V14T and V15T each wrote the budget form first and each backed
  out of it for exactly this reason; arming it means moving both cells to a named
  allowlist in the same edit, and that move should cover V14T / V15T / V24W
  together as an operator decision, not arrive as a side effect of this lane's
  ledger commit.
  FIRST LIVE RAISE OF `seam-endpoint-outside-soi`, and it is a finding in its own
  right: the run reported ONE unlisted (report-only) anomaly reason,
  `seam-endpoint-outside-soi`, an instrument the M-A7 design doc's prior-art item
  3 described as "report-only, has never fired". It has now fired. Echo evidence
  in the collected manifest: `recId 61e9177193444e329247d0e8288cf91e`,
  `pidKey 839899670`, `ut 5355599259.994832`, on the departure-loiter `DWELL`
  that OPENS in the `Sun` frame and CLOSES at `Duna` (`treatment StockConic`,
  `coverage InSegment`, `openAtExport = True`) - i.e. on the arrival SOI handoff,
  exactly where a cross-SOI endpoint check is meant to have an opinion. It moved
  no verdict (not in `hlib.ANOMALY_TOKENS`, surfaced via `unlistedReasons`) and
  is deliberately NOT added to `allowedAnomalies`: a tolerance for an ungated
  token is an inert declaration `parse_allowed_anomalies` warns about, and the
  whole-set parse cell asserts zero warnings. The design doc's item 3 is
  annotated with this run id.
  **READING FLIGHT 3 FLEW 2026-08-25 (run `2026-08-25_1616`, CLEAN PASS attempt
  1) AND THE BLOCK IS NOW ARMED OFF THE `_1502` + `_1616` PAIR.** Flight 3 was
  the gate section 1 named before it flew: a re-fly of the unchanged spec with
  the three tolerances flight 2 authored, which had to return PASS with those
  tokens RECURRING inside their stated populations. It cleared the gate exactly
  rather than merely inside the populations - `hitCounts {icon-teleport: 65,
  icon-off-orbit: 2, loop-seam-teleport: 2}`, the same three integers, plus the
  same single report-only `seam-endpoint-outside-soi` echo. Three counts that
  repeat to the integer across two independent flights are a deterministic
  property of this drive shape, which is what the gate was for.
  THE PAIR MATCHES FACET FOR FACET, and that is what the arming rests on: dwells
  2 (+2 open), cycles 1, transitions 2, chainBuilds 2, lineBranches 2,
  `treatments StockConic 2`, `coverages InSegment 2`, seams `rigid 11` /
  `flexible-soi 4`, `seamEndpoints 1024`, `seamTangents 0`, `holdsAboveOneX 1`,
  `seamsAboveOneX 2`, four INFO `RC-QUAL` findings and nothing worse - every one
  equal to flight 2's integer; `maxEndpointRatio 0.43669474211704823` against
  0.4366947421168355; observed hold 7061.161390 s against 7050.236864 s at the
  same per-cycle plan; `unevaluable 335146` against 334342; and the histogram
  `warp1x 322868 / warp100 10626 / warp1000 2160 / warpHigh 0 / warpPhys 0`
  against `322078 / 10602 / 2170 / 0 / 0` - **every bucket within 0.5 %**.
  **ARMED 2026-08-25**: `gating = true`, `dwells = { min = 1, max = 32 }`,
  `unevaluable = { max = 500000 }`, `requireSeamKinds = ["rigid",
  "flexible-soi"]`, and - THE FIRST IN THE SUITE - `warpBuckets = ["warp100",
  "warp1000"]`. Arming off a PAIR rather than off one green reading is a
  deliberate deviation from the V14M/V8 precedent and is stated in the spec: this
  lane's subject IS a histogram, and a histogram read once is a sample.
  Declaring `warpBuckets` also arms RC-WARP's two non-list clauses at FAIL level
  (`seamsAboveOneX` and `holdsAboveOneX` must be non-zero, both backed twice at 2
  and 1), so **RC-WARP's anti-vacuity statement is now ASSERTED and not merely
  measured** - the measurement half of this debt is closed. The unevaluable
  ceiling is ~1.5x rather than the siblings' ~3.3x because 99.8 % of the census
  is the SEAM_ENDPOINT decimation (the per-pid cap reporting loudly on a rails
  drive shape), so the ceiling is the honest anti-vacuity bound over the
  decimation regime and tightening it would red on the instrument's own
  bookkeeping. NOT declared, each for a measured reason: `warpHigh` (0 twice -
  the commanded ladder tops out at KSP rails index 5, so 1000x IS this subject's
  ceiling), `cycles` (1 CLOSED twice, so V14M's `{1,16}` window WOULD hold, but
  the closed-cycle count here is a property of three supervisor-chosen windows on
  a COMPRESSED span clock and of the export instant rather than of what this lane
  contributes), any RC-CUT surface (`cut-run-period-absent: 2` on both runs), any
  RC-HOLD clause (one engage/release pair per run), and any endpoint count window
  (the population is decimated). Arming re-pinned nothing in the flown shape and
  claimed D14 `warp-rails` in the same commit - the gate that makes the claim
  true - while still declining `warp-high`. Pins: `RENDERCOMPOSE_ARMED_SPECS`
  gains the file with both reading ids, and a third per-lane armed KEY-SET pin
  (`test_v24w_declares_the_render_composition_block_armed_with_the_warp_buckets`)
  pins the exact key set including the bucket list.
  ~~STILL OWED, AND THIS ENTRY STAYS OPEN UNTIL BOTH FLY: the ARMED RE-FLIGHT
  and the NEGATIVE CONTROL~~ **BOTH FLEW 2026-08-25 AND THE DISCIPLINE IS
  COMPLETE - SIX FLIGHTS ON THIS LANE.** THE ARMED RE-FLIGHT flew TWICE:
  `2026-08-25_1722` (PASS attempt 1, `gating=True
  armedBlocks=['renderComposition']`, ZERO mismatches; dwells 2 +2 open,
  seamKinds rigid 11 / flexible-soi 4, `holdsAboveOneX 1`, `seamsAboveOneX 2`,
  histogram `warp100 10644 / warp1000 2168 / warpHigh 0`, unevaluable 334093,
  four INFO RC-QUAL and nothing worse) and `2026-08-25_1811` (PASS attempt 1,
  ZERO mismatches, `warp100 10580 / warp1000 2162 / warpHigh 0`) - 1811 having
  been flown AS the negative control and having evaluated the UNINVERTED block,
  because its substring edit hit a rationale COMMENT quoting the same
  `warpBuckets` literal ahead of the real key (entry
  NEGATIVE-CONTROL-EDIT-NEVER-REACHED-THE-KEY above), so it reclassifies as a
  second re-flight rather than being discarded. Across ALL FOUR full PASS
  flights the anomaly triple repeated to the integer (`icon-teleport 65 /
  icon-off-orbit 2 / loop-seam-teleport 2`) and every armed facet re-measured
  inside its declared window. THE NEGATIVE CONTROL flew as `2026-08-25_1925`:
  `warpBuckets` inverted to `["warpHigh"]` - a composition token of this lane's
  OWN, measured 0 on every flight - by a LINE-ANCHORED edit of the real key,
  with `run.py --dry-run`'s `declared:` line read first to confirm the control
  was actually loaded. Verdict `PARSEK-FAIL(render-composition)` attempt 1 with
  EXACTLY ONE mismatch naming the zero-count bucket (`RC-WARP [FAIL]
  warpBuckets.warpHigh: spec declared warp bucket 'warpHigh' and the manifest
  counted zero frames in it - the run did not visit that warp regime`), every
  sibling verifier row clean, the four INFO RC-QUAL findings still beside the one
  FAIL, and the composition facets equal to the PASSing flights' - so the red is
  the declaration and nothing else. The run JSON's
  `verifiers.renderCompose.declared` records `warpBuckets: ['warpHigh']`, the
  audit surface added because of the 1811 miss and demonstrated by the very
  control that needed it; the control was reverted in the same change on the
  verified real key. Deliberately not shared with V14M/V8, on their own stated
  ground: a shared inversion re-proves the `rendercompose` evaluator rather than
  this block.
  ~~ONE RED IS PRE-REGISTERED AS LIKELY AND DELIBERATELY NOT BUDGETED AWAY~~ THE
  PRE-REGISTRATION WAS DISCHARGED AND IT PAID: the spec shipped
  `allowedAnomalies = []` although the free-play session behind its fixture
  raised `icon-teleport` 95 times on a render the operator called visually
  correct (the entry below), and the header promised in writing that a red on
  that token would be the measurement rather than a defect. Flight 2 red on
  exactly that token at 65, and because nothing was pre-tolerated, the count is
  recorded instead of swallowed - which is what gave the icon-teleport
  calibration entry its second dataset and its first machine-driven
  discriminator.
  ONE THIRD-PARTY ANOMALY, FILED AND NOT ACTIONABLE: flight 1's single
  `NullReferenceException` (17:22:29.551) is `MuMech.MechJebCore.OnDestroy`
  reading `vessel.isActiveVessel` after `FlightGlobals` teardown during
  `Application.Quit`. Zero Parsek frames in the stack, unique in a 3.8 MB log,
  0.6 s after `FlushAndQuit` returned OK and the save was written, `kspExit
  code=0`. MechJeb quit noise; no Parsek work owed.
- **The manifest header's `mapRenderTracingOn` bit should be STICKY
  (was-ever-on), not export-instant** [OPENED 2026-08-25 off the two Phase-3
  reading runs. Small, C#-side, verifier-facing]. `TryExportNow`
  (`Source/Parsek/MapRender/RenderCompositionRecorder.cs`) stamps the header from
  `MapRenderTrace.IsEnabled` AT THE EXPORT INSTANT, so the bit describes one moment rather than the run. Measured: V14M's
  `2026-08-25_0953` manifest says `mapRenderTracingOn=false` while carrying 107
  seam endpoint records that only exist because tracing was on in flight, while
  the V8 sibling read `true` off the same drive shape - the two disagree about
  runs that both flew with the tracer armed (both manifests were the
  `exportReason=process-teardown` write). It is not cosmetic: the false reading
  is what put `seam-data-unavailable-tracing-off: 1` into V14M's unevaluable
  census, so a lane can look tracer-off to the verifier while its seam numbers
  are real. ~~Fix: accumulate a was-ever-on flag in the recorder (set on any
  frame the tracer is enabled) and stamp THAT~~ FIXED 2026-08-25 in the arming
  pass. `RenderCompositionRecorder.mapRenderTracingWasEverOn` is latched by
  `LatchMapRenderTracing()` once per ARMED frame (from `Update`, after the
  `IsEnabled` gate) plus once more at export, and cleared ONLY in `Reset()` -
  the same boundary at which the accumulated records are dropped, so the bit can
  never describe a population it did not accumulate with. The header KEY
  `mapRenderTracingOn` is unchanged (the Python reader keys off that spelling
  verbatim); its semantics are documented on
  `RenderCompositionManifest.ManifestHeader.MapRenderTracingOn` and in
  `rendercompose.py`'s unevaluable-discipline docstring. No second export-instant
  key was added - nothing consumes one. Cells:
  `RenderCompositionRecorderTests.StickyTracingBit_*` (four: starts false and
  latches, survives the tracer going quiet, clears with the records, still
  serializes under the unchanged key). PYTHON SEMANTICS RE-CHECKED and unchanged:
  `seam-data-unavailable-tracing-off` still raises only on "record family empty
  AND header false", which the sticky bit makes honest in BOTH directions - the
  complementary reading (header true, empty seam family) is now a MEASURED
  absence rather than an unknown instrument, and correctly raises nothing.
  CONSEQUENCE FOR THE LANES, predicted then MEASURED on V14M's armed re-flight
  `2026-08-25_1050`: the bit read `true` and
  `seam-data-unavailable-tracing-off` is gone from the census, so the fix is
  CONFIRMED - but the predicted 56 -> 55 total did not land, because
  `seam-endpoint-skipped` independently ran 106 against the reading run's 53 and
  the total read 108. The prediction was about the one reason and held; the total
  is not a controlled quantity, which is exactly why the armed
  `unevaluable = { max = 200 }` window is a runaway guard rather than a pin.
- **Phase 3b (the MUN subject + the first two-complete-cycle RC-CYCLE dataset).
  IN PROGRESS - lane AUTHORED 2026-08-25 on branch `mun-composition-lane`,
  READING RUN TAKEN 2026-08-25 (`2026-08-25_2056_V6M-mun-player-loop`), ARMING
  PASS OWED.** Additive to a COMPLETE Phase 3, not a re-opening of it: the
  three armed lanes above stay exactly as they are.
  THE OPERATOR'S QUESTION, verbatim: "did we get to test if a looped mission to
  the Mun renders correctly after 1, 2 periods?" It had TWO gaps, and this item
  closes both.
    * GAP (a): NO MUN SUBJECT. All three declarers are Ike / Eve / Duna; no
      composition accounting has ever gated on a Kerbin->Mun loop.
    * GAP (b): NO LANE ANYWHERE HAS FED RC-CYCLE TWO COMPLETE CYCLES, so the rule
      has never actually compared two structures. The mechanism, read off the
      code rather than assumed: `rendercompose._cycle_windows` pairs CONSECUTIVE
      `cycle-rollover` clock events (N events -> N-1 closed windows) and
      deliberately does NOT synthesise the trailing open cycle against the export
      UT ("a cycle the export cut in half is not a cycle"); `_rule_cycle` then
      skips any unit with `len(windows) < 2` as `no-cycle-rollover-events`
      unevaluable. `RenderCompositionRecorder.ObserveUnitFrame` emits ONE
      rollover per CHANGE of `frame.CycleIndex`, and on a SCHEDULED (zero-drift)
      unit `ComputeSpanLoopFrame` takes the schedule branch - `CycleIndex = sIdx`
      (the index of the largest scheduled launch <= currentUT), UNRESOLVED before
      the first one - so the rollover count equals the number of scheduled
      launches the clock ENTERS. Every loop lane in the suite flies TWO cycles
      and therefore closes ONE: V14M measured exactly that
      (`clockEvents {cycle-rollover: 2}` -> `cycles 1` +
      `no-cycle-rollover-events: 1`).
  WHAT WAS AUTHORED: `harness/scenarios/V6M-mun-player-loop.toml` gained a THIRD
  cycle (one `StartLoopPlayback`, the same -180/-60/+140 arrival bracket about
  the cycle-3 seam, the same +20,800 park epoch, and deliberately NO third
  `EnterWatchMode` - a watch attempt is a pinned-verdict bet), one
  `ExportRenderManifest` immediately before `FlushAndQuit`, and a BARE
  `[expectations.renderComposition]`. 21 steps -> 27, and NOTHING in the existing
  flown shape moved: no jump UT, no budget, no expectation (the S4.1 rule). The
  lane already armed the three tracers the seam capture needs. It is registered
  in `RENDERCOMPOSE_DECLARER_SPECS` as the FOURTH declarer and is deliberately
  ABSENT from `RENDERCOMPOSE_ARMED_SPECS`.
  THE CYCLE-3 ANCHOR IS DERIVED, NOT GUESSED, and the derivation is validated
  twice before it is used: replaying `TryFindNextScheduleK`'s purely periodic
  filter with the throttle `ceil((L_last + span - ut0)/T_anchor)` reproduces this
  lane's two MEASURED anchors (k=13 -> 280,176.9473801677, k=26 ->
  560,319.4747603355) and the product's own
  `scheduleWorstResidual=4347.54846243246` to the digit, then continues the
  header's faithful-k series to **k=45 -> relaunchUt 969,758.553** (throttleK 28,
  residual 3,166.503) - hence seam 986,129.115 and park 990,558.553. A mis-pin
  self-detects as the already-forbidden `timejump refused reason=backward-jump`.
  WHY THIS SUBJECT AND NOT A SECOND COPY OF V14M: V6M is PAD-ROOTED with TWO
  constraints and a NON-UNIFORM ZERO-DRIFT SCHEDULE (`zeroDrift=yes`, faithful-k
  13, 26, 45, 58), where V14M is orbit-rooted, single-constraint and
  uniform-cadence - so RC-CYCLE's `cycleLengthResidualsSeconds` trend
  (`(hi - lo) - unit.cadenceSeconds` per window) is a genuinely different surface
  here. And both windows land in the SAME warp bucket (`warp1x`, every clock move
  being an instantaneous `TimeJump`), which is RC-CYCLE's SHARP FAIL-level
  isomorphism clause rather than its cross-bucket INFO one - so the comparison
  gets its strong form on its first outing. `warpBuckets` must NEVER be declared
  here (1x-only by construction; V24W owns the histogram).
  THE READING RUN IS IN: `2026-08-25_2056_V6M-mun-player-loop`, verdict PASS,
  `renderCompose` status REPORT. Steps (1) and (2) of the owed list are DONE and
  both headline predictions LANDED. `clockEvents {cycle-rollover: 3,
  inter-cycle-tail: 2}` -> **`cycles = 2`**, the suite's first two-closed-cycle
  dataset, and **RC-CYCLE EVALUATED for the first time anywhere** rather than
  reporting `no-cycle-rollover-events` - it found the two closed cycles
  **ISOMORPHIC** and raised nothing, with both windows in the SAME `warp1x`
  bucket (`warpBuckets {warp1x: 250}`), i.e. the SHARP FAIL-level form of the
  clause rather than the cross-bucket INFO one. Facets for the arming pass:
  `treatments {StockConic: 2, TracedPath: 3}`, `dwells 5` + `openDwells 3`,
  `transitions 5`, `chainBuilds 3`, `planUnits 1`, `lineBranches 11`,
  `coverages {InSegment: 5}`, `seamEndpoints 109`,
  `seamKinds {rigid: 21, flexible-soi: 3}`, `seamTangents 0`,
  `ownershipChanges 0`, `unevaluable 110`
  (`seam-endpoint-skipped` 109 + `warp-hold-traversal-evidence-absent` 1),
  `cycleLengthResidualsSeconds [-2.53, 129297.47]` (the second window spans the
  schedule's k=26 -> k=45 step, so the large residual is the NON-UNIFORM schedule
  reading correctly), `exportReason process-teardown`, `scene FLIGHT`,
  `mapRenderTracingOn true`.
  STILL OWED: (3) an arming pass by operator decision, whose obvious first clause
  is `cycles = { min = 2 }` - the one floor no other lane in the suite can carry -
  followed by the armed re-flight and a negative control inverting a window of
  this lane's OWN. READ `RC-OWN-DRAW-HALF-IS-MAP-GATED` below FIRST: on this lane
  (and on every other manifest lane today) RC-OWN's draw->publish direction is
  structurally unevaluable, so "no RC-OWN finding" must not be armed as
  "ownership conserved".
  A FAIL-level RC-CYCLE role-structure finding on the reading run would have been
  a MEASUREMENT, not a spec bug (a bare block gates on nothing): the render-side
  statement of "cycle 2 does not arrive where cycle 1 did", which is the question
  this lane's own desync hunt asks with coarser instruments. It did not fire -
  on this subject, at this resolution, cycle 2 arrives where cycle 1 did.
- **Phase 4 (routes + product).** Ride G1's B27/V18 for the route surfaces so
  RC-ROUTE evaluates against a real route line, and start the RC-QUAL trend
  record (kink angles, endpoint ratios, hold durations) that feeds
  promote-to-fixed decisions on the ratified visual artifacts.
  **WAVE B AUTHORED 2026-08-26 - TWO NEW LANES, BOTH BARE, BOTH UNFLOWN.** These
  are not Wave A's shape: Wave A declared the block on lanes that had already
  flown, while these are NEW LANES against the two fixtures the Phase-4 harvest
  landed, so each owes a FIRST FLIGHT before it owes a window. Declarer roster
  19 -> 21.
  `V18T-depot-route-ts-arrival` over `depot-route-recorded` is **the route half
  of this bullet and G1's first lane of any kind**: the only subject in the suite
  that can emit a `ROUTE_LINE_BUILD` record or a per-unit `ROUTE` node, so
  `rendercompose._rule_route` has never executed against live data and
  `routeLineBuilds` has never read non-zero anywhere. It arms NO mission loop -
  the committed ROUTE drives - and flies the TS host first on the measured fact
  that `RouteTrajectoryLineRenderer.DrawAll`'s only call site carries no
  `MapView` gate. ~~D10 `route-map-lines` stays UNDECLARED until a gating token
  earns it.~~ **FLEW AND ARMED 2026-08-26**: two matching readings, armed
  re-flight `2026-08-26_2015` PASS, negative control `2026-08-26_2017_a2` red on
  `renderComposition.routeLineBuilds 1 < min 5`, and D10 `route-map-lines` is now
  DECLARED - the gating token earned it.
  `V25M-duna-park-player-loop` over `duna-park-recorded` is **re-aim's second
  departure class** - a heliocentric-parking departure, the path
  `ReaimClassifier`'s own exception comment names by fixture and that no
  committed lane has driven; its manifest would also carry the first
  DESTINATION-side loiter cut (43,963.92 s) rather than V8's launch-side one.
  STILL OWED on this bullet: both readings, then the arming passes, plus the
  RC-QUAL trend record, which no lane has started.

DEFERRALS TAKEN IN PHASES 1-2, each of which a lane author must know.

- **`RC-OWN-DRAW-HALF-IS-MAP-GATED`: no manifest lane has ever observed the
  polyline draw/publish half, because none of them opens the map view. INSTRUMENT
  / LANE-SHAPE GAP, NOT A PRODUCT DEFECT [FOUND 2026-08-25 by diagnosing V6M's
  reading run `2026-08-25_2056`, which raised three report-only RC-OWN FAILs].**
  The finding as it read: `recId=4cfa06ce...: a visible TracedPath dwell
  [296370, 296690] / [576510, 576830] / [985950, 986270] exists with NO ownership
  record`, one per cycle, at exactly the three arrival `TimeJump` brackets.
  THE DIAGNOSIS (manifest + collected KSP.log + source): RC-OWN's two halves do
  not share a gate. The INTENT half - `ShadowRenderDriver.RunFrame`, which stamps
  the TracedPath intent that the DWELL records AND the stock line/icon
  suppression both read - is driven from `ParsekFlight`'s per-frame update and
  runs map-open or not. The PUBLISH half - `drewNonOrbitalLegRecordings` ->
  `RenderCompositionRecorder.NoteOwnershipPublish` - sits at the END of
  `GhostTrajectoryPolylineRenderer.Driver.LateUpdate`, whose SECOND statement is
  `if (!MapView.MapIsEnabled) return;`.
  THE EVIDENCE, all three independent: (1) the collected KSP.log carries the
  Driver's awake + destroy lines and ZERO `Polyline frame:` summaries - that
  summary is emitted at the end of EVERY completed walk, so zero means every
  LateUpdate bailed early, and the only other early return is the scene gate,
  which cannot have fired (`scene = FLIGHT`); (2) all three descent dwells carry
  `markerPolyline = False` with `markerIconSuppressed = True`, i.e. the marker
  rode the icon fallback because no leg drew; (3) `ownershipChanges = 0` for the
  whole run, and no seam verb opens the map - there is no `EnterMapView` command
  and no production path calls `MapView.EnterMapView`.
  SCOPE: this is the state of EVERY manifest lane today. V24W measured
  `ownershipChanges = 0` as well and escaped only because it never opened a
  TracedPath dwell; V6M is simply the first lane whose Director did. With the map
  closed nothing is drawn on any map surface, so a visible TracedPath dwell is an
  INTENT record and not evidence of a draw: the rule's premise was never
  established, and the three FAILs were the instrument speaking about a question
  the run never asked.
  DONE IN THIS PASS (minimal, one clause): `rendercompose._rule_own`'s
  draw->publish direction now names the precondition. ZERO `OWNERSHIP_CHANGE`
  records ANYWHERE stands the direction down as the defined-unevaluable
  `ownership-publish-surface-never-ran` (counted in the census, never silent);
  ONE publish anywhere proves the walk ran past the map gate, and from there a
  recording whose visible TracedPath dwell has no ownership record keeps its FAIL
  - that IS the leg-that-never-draws defect the direction exists to catch. The
  `falls outside every published ownership interval` clause is untouched (it
  already requires that recording to have published). Two cells pin both halves
  in `harness/lib/test_rendercompose.py`.
  ~~STILL OWED, and it is NOT a rule edit: a MAP-OPEN LANE, so the draw half is
  observed at least once. That needs a new command-seam verb (a C# change: the
  seam has no map-view verb at all)~~ **THE VERB HALF IS BUILT (2026-08-26).**
  The seam now carries `EnterMapView` and `ExitMapView` (M-A2, additive: 27
  implemented / 7 reserved), so the sentence above that says "there is no
  `EnterMapView` command" is history from here on. Both are SINGLE-PHASE, no
  args, precondition `RequiresFlight`, idempotent (`OK alreadyOpen=true` /
  `alreadyClosed=true` without calling stock), and their OK is a READ-BACK of
  `MapView.MapIsEnabled` - the very property the polyline Driver's LateUpdate
  gates on - never the bare fact that the void stock call returned. Single-phase
  is a decompile fact, not a convenience: KSP 1.12.5's `MapView.enterMapView()`
  assigns `MapIsEnabled = true` and fires `GameEvents.OnMapEntered` synchronously
  before returning (the deferred `Invoke("endEnterMapTransition", ...)` after it
  only disables the UI cameras), and `exitMapView()`'s FIRST statement is
  `MapIsEnabled = false`. Refusals split on WHO declined: `REJECTED
  map-view-unavailable` is the one PRE-call gate (null `MapView.fetch`, stock
  never called), and everything AFTER the call is `ERROR` - `map-not-entered` /
  `map-not-exited` when the read-back still disagrees (stock declined through
  `ConstantMode`, `CanUseMap` off, or a `MissionSystem` camera-switch block; the
  class is deliberately NOT re-derived) and `map-view-threw` when it threw. That
  is `SimulateStockSwitchClick`'s line verbatim (REJECTED before the stock call,
  ERROR after), so a spec's `expect` has one rule to learn rather than a per-verb
  exception.
  Files: `TestCommandMapViewVerbs.cs` (pure) +
  `ParsekTestCommandAddon.MapView.cs` (applier) + the verb / interface /
  precondition tables, `hlib.IMPLEMENTED_SEAM_VERBS` with both role rows
  (`world-mutating` on the tail axis, `recording` on the post-mission axis), and
  the design doc's "Update (the map-view pair)" block.
  ~~WHAT IS STILL OWED ... (1) ADD THE STEP TO A LANE~~ **(1) IS DONE
  (2026-08-26): `V6M-mun-player-loop` ADOPTED THE PAIR**, and it took the
  placement this entry asked for. `EnterMapView` sits immediately after the three
  tracer `SetSetting`s and BEFORE `MissionConfig`, so the polyline Driver walk
  runs during EVERY observation window the lane opens - all three cycles' arrival
  brackets and all three park epochs - rather than only the tail. An
  `EnterMapView` after the first bracket would leave cycle 1 measuring the old
  map-closed shape and make the reading incomparable across the three cycles,
  which a two-closed-cycle dataset must not be.
  `ExitMapView` goes BEFORE `ExportRenderManifest`, and THAT placement is FORCED
  rather than chosen: `test_every_declarer_exports_immediately_before_teardown`
  pins the last two commands of every declarer as
  `[ExportRenderManifest, FlushAndQuit]`, so an `ExitMapView` between them reds
  the cell. It costs nothing - the recorder has accumulated every observation by
  then, and the export is an in-memory read that does not need the map open.
  THE FLOWN SHAPE MOVED (`EnterMapView` is `TAIL_ROLE_WORLD_MUTATING`), which is
  admissible without an armed re-validation debt only because V6M is
  DECLARED-UNARMED on `[expectations.renderComposition]`; the two save-structure
  blocks it DOES gate cannot see a map-view toggle (no vessel, no save, no Parsek
  persisted state), and no jump UT, budget or existing expectation moved.
  ~~WHAT IS STILL OWED ... (2) THE READING THAT PROVES THE PUBLISH FLOWS~~
  **(2) IS DONE - THIS ENTRY IS CLOSED (2026-08-26).** The reading flew as
  `2026-08-26_1745_V6M-mun-player-loop`, PASS attempt 1, and BOTH pre-registered
  criteria landed: the collected `KSP.log` carries `Polyline frame:` summaries
  (the 2026-08-25 run carried zero) and the manifest reports
  `ownershipChanges = 6` - THREE CLEAN appear/disappear PAIRS, one per TracedPath
  dwell, all on `recId=448cd680`, at `[296370, 296690]`, `[576510, 576830]` and
  `[985950, 986270]`: the SAME three brackets whose missing records raised the
  original three FAILs. RC-OWN findings went 3 -> 0 and
  `ownership-publish-surface-never-ran` is GONE from the run's unevaluable
  reasons entirely, so the stand-down that had fired on every manifest lane in
  the suite is lifted BY EVIDENCE rather than by decision.
  **OWNERSHIP IS CONSERVED ON THIS SUBJECT, and the three earlier FAILs are
  confirmed as the instrument gap this entry diagnosed rather than a
  leg-that-never-draws defect.** The caveat above is what makes that a real
  result: the verb was necessary but not sufficient, a map-open lane could still
  have published nothing, and THAT would have been a real finding. It published.
  The rest of the composition reproduced `2026-08-25_2056` exactly (cycles 2,
  isomorphic again; dwells 5 +3 open; transitions 5; seams rigid 21 /
  flexible-soi 3; cycle residuals identical to the digit), so the closure is not
  bought with a changed subject. A pass may now read "no RC-OWN finding" as
  "ownership conserved" ON A LANE THAT OPENS THE MAP AND PUBLISHES; the design
  doc's ratified deviation #5 amendment is discharged on the same evidence. What
  remains for V6M is ARMING (windows off the pair `2026-08-25_2056` +
  `2026-08-26_1745`, then the armed re-flight and the negative control), which is
  a scenario-ledger item and not this entry's.
  **THE QUALIFIER IS NOW DISCHARGED ON A SECOND CLASS TOO (2026-09-02), AND THE
  ENTRY IS NARROWED RATHER THAN REOPENED.** The closure above is scoped by its own
  words - "ON A LANE THAT OPENS THE MAP AND PUBLISHES" - and V6M is a MUN ORBIT
  subject, so every other class stayed unmeasured. `H59-surface-route-map-lines`'s
  census (`2026-09-02_0947`, PASS attempt 1) measured it on a SURFACE SUPPLY-ROUTE
  subject in the FLIGHT map, the first lane to open the map on either a route or a
  landed host. Both halves landed:
  the DRAW half directly - four `Polyline frame: scene=FLIGHT drawn=1 warp=1x
  suppressed=0 hidden=0` summaries, where this entry's own evidence rule is that
  ZERO summaries means every LateUpdate bailed early;
  and the PUBLISH half through an instrument this entry did not anticipate - the
  route renderer's own skip counter. `RouteTrajectoryLineRenderer.DrawAll`
  increments `skippedOwned` only when
  `GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(memberRecordingId)`
  returns true, and that method's whole body is a read of
  `drewNonOrbitalLegRecordings` (`GhostTrajectoryPolylineRenderer.cs:443-448`) -
  THE SAME SET `RenderCompositionRecorder.NoteOwnershipPublish` is fed from, on the
  same epilogue, as that file's `:451-463` comment states. NINE
  `Route line draw: ... skippedOwned=1` frames are therefore nine frames on which
  the publish surface ran AND held this route's member, against two frames at
  `routesDrawn=1 legsDrawn=1 skippedOwned=0` where it did not - both producers present
  rather than a one-sided reading. (Not an ordering claim: the second of those two frames
  falls 140 ms after `exitmapview`, since the route-draw slot is not `MapView`-gated. Every
  handoff frame IS inside the map-open window, which is what this entry needs.)
  **WHAT IS STILL UNMEASURED IS THE MANIFEST FACET ON THIS CLASS, NOT THE
  BEHAVIOUR.** H59's `ownershipChanges` read 0, and that is NOT an ownership
  finding: its manifest was exported in SPACECENTER after a scene switch that
  Resets the recorder, so the FLIGHT accumulation was not in the exported file at
  all - see
  RENDER-MANIFEST-VERB-EXPORT-IN-A-SECOND-SCENE-CLOBBERS-THE-FIRST-SCENE-ACCUMULATION.
  A reader of that run's results JSON must not take `ownershipChanges = 0` for the
  `ownership-publish-surface-never-ran` shape this entry originally diagnosed; the
  two look identical in the facet and are told apart by the log, which is why H59
  arms on log tokens and leaves its render-composition block bare.
  **CONFIRMED ACROSS THREE RUNS (2026-09-02).** `_0947`, `_1017` and the armed
  re-flight `_1038` read the same two drawn frames and the same nine
  `skippedOwned=1` handoff frames, on three DIFFERENT generated route ids, so the
  publish-surface evidence above is a repeatable measurement rather than a single
  reading. `_1038` PASSED with those tokens armed as literals, and a negative
  control red on exactly the headline draw token.
- **`RC-SEAM` blamed the LAST boundary of a warped-over transition. VERIFIER
  MISREAD, NOT A RENDERER DEFECT. FIXED 2026-08-26** [found by diagnosing V25M's
  reading run `2026-08-26_1744`, which red
  `RC-SEAM [FAIL] TRANSITION[pid=3129690249 ut=5360143765.0]: body change
  Sun->Duna classified as a rigid seam`].
  THE DIAGNOSIS. A `TRANSITION` is a DWELL-stream event: it fires when the
  Director's rendered segment index MOVES, and a dwell only opens on a segment
  the render clock actually sat in. V25M's arrival straddle warped the head clean
  ACROSS an interior segment, so the record read
  `fromSegmentIndex 6 -> toSegmentIndex 8` and spanned TWO boundaries. The chain
  classified both correctly: boundary 7 is the real crossing (PHASE[6]
  heliocentric-transfer Sun -> PHASE[7] departure-loiter Duna) and it is
  `flexible-soi`; boundary 8 is Duna -> Duna and it is `rigid`.
  `RenderCompositionRecorder.NoteChainBuild` emits `BoundaryIndex = i` for
  segment `i`'s `LeadingSeam`, so keying the seam table on `toSegmentIndex` -
  which wave-1's `_rule_seam` did - reads the LAST boundary of the span and
  blames its correct `rigid` for a body change that happened at an earlier one.
  `2026-08-25_0956` (V8) is the near miss that shows the clause was never really
  being evaluated on this shape: the same 6 -> 8 span with a Mun -> Sun change
  passed only because BOTH crossed boundaries happened to be `flexible-soi`.
  THE FIX: `rendercompose.transition_boundaries` (pure) enumerates every boundary
  a transition crossed and takes each one's bodies from the CHAIN's own `PHASE`
  records, so the rule tests the boundary the body actually changed at. The
  observed `fromBody -> toBody` pair is used only for a SINGLE-boundary span,
  where the two agree by construction (this keeps `assembler-fallback` chains
  evaluable). A multi-boundary span the PHASE list cannot resolve is the new
  defined-unevaluable `seam-boundary-bodies-absent` - counted, never guessed. A
  retire (`toSegmentIndex = -1`) or a loop wrap names no boundary at all.
  Findings now carry `boundary=N` in the target.
  VERIFIED OFFLINE ACROSS THE WHOLE ARCHIVE: the V25M manifest re-reads with ZERO
  findings and all SIXTEEN other archived manifests are byte-identical (same
  findings, same unevaluable counts). Five cells in
  `harness/lib/test_rendercompose.py` pin the shape, including the negative
  control where the interior boundary IS rigid and the finding must name it.
- **Seam measurement is double-gated.** The tangent and endpoint evaluation
  sites are `mapRenderTracing`-gated and were NOT widened, so a manifest lane
  that wants RC-SEAM / RC-QUAL numbers must arm BOTH `PARSEK_RENDER_MANIFEST=1`
  and the `mapRenderTracing` setting. With tracing off the header says so and
  the verifier reports `seam-data-unavailable-tracing-off` as a DEFINED
  unevaluable - never a silent pass.
- **Transition endpoint positions ride the same gate.** They come from the
  `MapRenderProbe` truth push, and the probe runs only when tracing is on; with
  it off the position clauses are defined-unevaluable.
- **RC-HOLD's marker half is capped pending V6.** Leg 2 compares the observed
  `hold-release` accumulated seconds against the recomputed value with a
  tolerance of `max(2 s, 2 * local maxUtStep)`, and a hold whose accumulated
  stall never reaches the C# detector's `HoldMinStallSeconds = 5.0` emits no
  engage/release pair at all (below-resolution, not a mismatch). Sizing that
  half wants a V6-class lane that actually dwells through a hold at a known
  warp. UPDATED 2026-08-25 with the C# review pass, which replaced the
  frame-step stationarity test with STALL ACCUMULATION: the resolution floor is
  now that 5 s constant rather than one live frame step (the old test could not
  see a 1x hold at all, and misread a mid-hold warp drop as a release plus a
  second engage), and MORE THAN ONE run per `(ownerIndex, cycleIndex)` is a legal
  shape. UPDATED AGAIN by the same pass's second review: the STATIONARITY
  predicate is now RELATIVE - a frame counts as stalled only when
  `|delta loopUT| < min(HoldStationaryLoopUtEpsilonSeconds, 0.5 * liveStep)` on a
  positive live step, so the 0.25 s constant is a CEILING on the window rather
  than the window itself. The absolute form sat ABOVE ordinary 1x clock advance
  (~0.02 s per frame), so plain 1x playback accumulated a phantom stall past the
  5 s floor and reported a hold nobody planned - and a real hold that released at
  1x never emitted its release, because no 1x frame ever left the window either.
  A hold freezes the render clock EXACTLY (the hold formula returns a constant
  phase), so near-zero is the true signal and the relative form is warp-proof in
  both directions. Two consequences a lane author has to know. (1) Pairing is on
  `(ownerIndex, cycleIndex, detailA)` where `detailA` is the run's 0-based
  ORDINAL - it used to repeat the cycle index. (2) The plan predicts ONE arrival
  hold per cycle, so exactly one run per cycle is compared and the rest are
  counted `hold-run-not-attributable-to-planned-hold`.
  ~~a lane that wants the second stall adjudicated needs the
  frozen-loopUT-vs-hold-position join the manifest does not carry yet~~ **DONE
  2026-08-25 (leg-2 attribution by frozen loopUT), forced by the first free-play
  ground-truth manifest.** The join the old note said the manifest did not carry
  was already there: `ObserveUnitHoldRun` stamps `StallStartLoopUT` into
  `detailB` on BOTH hold events, and the plan says where the arrival hold freezes
  the clock - `rendercompose.arrival_hold_frozen_loop_ut`, composed the way
  `SpanClock` composes it
  (`decompress_span_ut(spanStartUT + (compress_span_ut(arrivalHoldAtUT, cuts) -
  spanStartUT), cuts)`; NOT plain `arrivalHoldAtUT`, which is the identity only
  when the hold instant sits past the end of every cut before it). A release is
  now compared only when its frozen loopUT sits within `max(2 s, 2 * local
  maxUtStep)` of that position; longest-per-cycle survives only WITHIN the
  attributable set.
  WHY IT COULD NOT WAIT: on `.scout/s15-duna-manifest.txt` - the first free-play
  session, operator-eyeballed as correct - the old rule manufactured a FAIL. One
  unit carried three engages: a 12.346 s launch-repay stall in cycle 6 frozen at
  the span end (`detailB` 70964232.983), the GENUINE arrival hold in cycle 7
  frozen exactly at `arrivalHoldAtUT` (70898646.058, observed 55400 s against a
  55581.371 s prediction - agreeing to 181 s inside a 40000 s tolerance), and a
  third engage still open at scene exit. Cycle 6's only release was the repay
  stall, so longest-per-cycle compared it against cycle 6's 53299.326 s
  arrival-hold prediction and red at delta -53286.98 s. With positions compared
  it is 65586.92 s away from where the arrival hold freezes and is simply not the
  run the prediction is about. Post-fix that manifest yields ZERO RC-HOLD
  findings, one `hold-run-not-attributable-to-planned-hold` and one
  `hold-engaged-never-released`.
  TWO FALLBACKS, so a schema-poor manifest does not silently stop being checked:
  when the plan carries no `arrivalHoldAtUT`, or when NO release on the unit
  carries a frozen position, attribution is undecidable and the rule reverts to
  the pre-calibration longest-per-cycle pick. A release missing `detailB` on a
  unit where others carry it is NOT attributable.
  NEW COUNTED UNEVALUABLE: `hold-engaged-never-released`, for an engage the
  manifest never releases. Deliberately NOT the FAIL a release-with-no-engage
  gets - a release is only ever emitted by a run the detector already engaged, so
  a release alone means the record lost a row, while an engage owes nothing yet
  and a scene exit mid-hold produces exactly this.
- **The InteriorGap duration bound uses the unit CADENCE.** The design's
  seam-gap-plus-reseed bound is the Phase-3 tightening; the module docstring
  says so.
- **RC-OWN's publish->draw direction is WARN, not FAIL, pending live
  calibration** (design deviation #5). Two ratified populations legitimately
  publish ownership without a TracedPath dwell - proto-less pid-0 recordings
  (no Director dwell exists at all) and the Driver-direct bridge / forward legs
  (the concurrent dwell is `StockConic`) - and both are exempted by name and
  counted. Whatever is left over is REPORTED at WARN and counted
  (`ownPublishWithoutDraw`) because no live run has yet shown whether a third
  benign population exists. The MIRROR direction, a draw with no publish, stays
  FAIL. A lane that wants this armed should first read `ownPublishWithoutDraw`
  off a report-only run and confirm it sits at zero.
  CALIBRATION DATA, 2026-08-25, from the first free-play ground-truth manifest
  (`.scout/s15-duna-manifest.txt`, a Kerbin-Duna re-aim loop the operator
  visually validated as correct): `ownPublishWithoutDraw = 2`, and read out of
  the record BOTH turned out to be the SAME population - and it is not "a publish
  with no draw". Both are a RENDER-ENTRY LEAD-IN: an ownership window that closes
  a fraction of a second BEFORE that recording's FIRST `DWELL` record opens.
  * `61e9177193444e329247d0e8288cf91e` (transfer member, pid 3445082362, 18
    dwells): publish `[5329053994.561617, 5329053997.561617]`, first dwell opens
    5329053998.061617 - a 0.5 s gap, under that dwell's own 2.5 s `maxUtStep`.
    Its other EIGHT publishes all intersect a dwell and raise nothing.
  * `6561c8eb97dd48d6825e9d6c7c04d22a` (member 27, pid 650833675, exactly ONE
    dwell, still open at export): publish `[5329057525.398653,
    5329057531.898653]`, its only dwell opens 5329057532.898653 - a 1.0 s gap.
  MECHANISM: `drewNonOrbitalLegRecordings` publishes on the frame the Director
  actually draws, while the `DWELL` record for that ghost opens on a later
  classification frame, so a sub-frame publish window sits ahead of the
  recording's first dwell. Neither ratified exemption can see it - (a) wants NO
  dwell anywhere and both recIds have dwells; (b) wants a CONCURRENT `StockConic`
  dwell and the dwell starts just after the window closes.
  NO RULE CHANGE was made off this. Both shapes are now pinned as fixtures in
  `harness/lib/test_rendercompose.py` (`test_s15_shape_a_...`,
  `test_s15_shape_b_...`, plus the one-second-later control that IS claimed by
  exemption (b)), so a later pass that ratifies a lead-in exemption - or promotes
  the rest of deviation #5 to FAIL - has to move them deliberately instead of
  rediscovering the population from another live run. A lane arming this
  direction today would still red on a correct session, so it stays WARN.
- **RC-COVER counts only VISIBLE dwells as coverage.** An invisible dwell whose
  coverage is `InInteriorGap` or `OutsideWindow` is a RATIFIED hidden window and
  still explains its span (counted as `coverRatifiedHiddenSpans`); an invisible
  dwell whose coverage is `InSegment` is NOT coverage - a covering segment
  existed and the leg still did not draw, which is the defect class the rule
  exists for. A lane reading a high `coverRatifiedHiddenSpans` is reading a run
  whose "coverage" is mostly cataloged darkness, not drawn line.
- ~~**The Phase-1 tail is still open:** one manual armed play session producing a
  manifest over a committed loop fixture.~~ **DONE 2026-08-25.** The session flew
  (Kerbin-Duna re-aim loop, `exportReason=scene-exit`, warp100 / warp1000 /
  warpHigh all covered, operator-eyeballed as correct); the manifest is
  `.scout/s15-duna-manifest.txt` (495 KB) and the collected run is
  `logs/2026-08-25_1537_s15-duna-one-manifest-run2`. The recorder's hooks DO fire
  in the shapes the rules expect on real geometry: 1 plan unit, 2 chain builds,
  17 closed + 2 open dwells, 17 transitions, 10 clock events (3 hold-engage /
  2 hold-release), 20 line branches, 30 ownership changes, 1024 seam endpoints.
  Everything the reading disagreed with the operator about was the INSTRUMENT's
  fault and is calibrated in this section (leg-2 attribution, the two RC-OWN
  lead-ins, the seam-endpoint volume, the `icon-teleport` entry below).
- **Free-play SEAM_ENDPOINT volume: the per-pid cap is a CLIFF over a long
  session, so the section now DECIMATES above half of it** [OPENED and FIXED
  2026-08-25 off the first free-play manifest]. A harness flight is minutes long
  and never reaches `MaxSeamRecordsPerPid = 512`; a free-play session reaches it
  in minutes and everything after it is dropped, so the section describes the
  HEAD of the session and is silent about the rest. Measured on
  `.scout/s15-duna-manifest.txt`: 1024 endpoints admitted against 28985 dropped
  (`truncated-section-seam_endpoint`) - 3% of the session, all of it at the
  front. FIX: `RenderCompositionManifest.TryPassSeamEndpointDecimation` counts
  per-pid OFFERS (not accepts - an accept-keyed rate stalls the moment the cap
  bites and reproduces the cliff) and, above
  `SeamEndpointDecimationThreshold = MaxSeamRecordsPerPid / 2`, admits one offer
  in `SeamEndpointDecimationKeepEveryNth = 8`. The caps stay the FINAL bound;
  decimation only changes WHICH offers reach them, so a pid now spans ~2304
  offers before the cliff instead of 512. Thinned drops are counted like any
  other drop but under their own TRUNCATED kind, `seam-endpoint-decimated`, so
  the Python ledger can tell a thinned section (trend intact, rate reduced) from
  a cut-off one: `decimated-section-<section>` instead of
  `truncated-section-<section>`, a `decimatedSections` facet, and an RC-QUAL
  INFO trend row. SEAM_TANGENT passes the gate untouched (shares the caps, not
  the volume problem). Below the threshold nothing changes at all, which is why
  `Source/Parsek.Tests/Fixtures/RenderManifest/sample-manifest.txt` is byte-
  identical. Cross-language token pin:
  `test_the_decimated_kind_token_is_the_one_the_writer_declares` reads the C#
  declaration, so a rename on either side reds instead of silently degrading
  every thinned section back into a cliff.
- **`MapRenderProbe`'s `icon-jump` / `icon-teleport` threshold looks
  over-sensitive above warp1000 and needs its own calibration pass**
  [OPENED 2026-08-25 off the first free-play ground-truth manifest. FILE-ONLY:
  the probe was deliberately NOT changed. Owner: whoever owns `MapRenderProbe`].
  The s15 session was played by hand and visually validated as CORRECT by the
  operator, and current code raised `icon-teleport` **95 times** during it
  (evidence: `logs/2026-08-25_1537_s15-duna-one-manifest-run2` plus the
  manifest's own `anomalyEchoes` census, which reports exactly
  `{"icon-teleport": 95}` and no other reason). 95 raises on a session with no
  observed defect is either a real render defect the operator could not see or a
  threshold that does not survive rails warp - and the manifest carries the
  discriminator for telling those apart: its warp histogram over the same frames
  is `warp1x 0 / warpPhys 0 / warp100 4727 / warp1000 15488 / warpHigh 11446`,
  i.e. the session is ENTIRELY above warp100 and mostly at warp1000+. The
  calibration pass owed is a per-raise warp attribution: bucket the 95 raises by
  the warp regime of the frame that raised them, and if they concentrate at
  warp1000+ the threshold is the suspect rather than the render. NOTE what is
  already known not to be the cause: the delta is already measured in the orbit's
  OWN reference-body frame precisely so a body's world motion cannot dominate at
  high warp (see the `MapRenderProbe` entry in `.claude/CLAUDE.md`), so this is
  not the raw-world-delta mistake being re-made. `icon-teleport` is a GATED token
  (`hlib.ANOMALY_TOKENS`, promoted 2026-08-04) with a `maxCount` budget, so an
  over-sensitive threshold costs real lanes real reds - which is why this is
  filed rather than left as a curiosity.
  **SECOND DATASET 2026-08-25, and it is the one this entry was waiting for: a
  MACHINE-DRIVEN commanded-rails session over the SAME subject raised the token
  65 times.** V24W reading flight 2
  (`harness/results/2026-08-25_1502_V24W-duna-one-warp-stair.json`) flew
  `fixtures/saves/duna-one-recorded` - the harvest of the very s15 session above
  - through a commanded stair (10x/50x/100x/1000x/100x/50x/10x at each of three
  windows) and came back `anomalySweep hitCounts {icon-teleport: 65,
  icon-off-orbit: 2, loop-seam-teleport: 2}`, PARSEK-FAIL(anomaly) on a
  deliberately empty `allowedAnomalies` with every other verifier row green.
  WHY THE PAIR MATTERS: both sides are VISUALLY-VALIDATED-SUBJECT contexts (the
  s15 session was eyeballed correct by the operator; V24W replays that same
  recorded state by machine), so what differs between them is the DRIVE, not the
  subject - 95 raises hand-played versus 65 raises machine-driven. AND THE
  DISCRIMINATOR THIS ENTRY ASKED FOR NOW EXISTS ON BOTH SIDES: s15's histogram is
  `warp1x 0 / warpPhys 0 / warp100 4727 / warp1000 15488 / warpHigh 11446`
  (entirely above warp100), while V24W's is `warp1x 322078 / warp100 10602 /
  warp1000 2170 / warpHigh 0 / warpPhys 0` (overwhelmingly AT 1x, with a bounded
  rails excursion and nothing at all above warp1000). So the raise count did NOT
  collapse with the time-above-warp100 fraction - a ~96 % 1x session still raised
  two thirds as many as a 0 % 1x one - which is a real constraint on the
  "threshold does not survive rails warp" hypothesis and the reason the owed work
  is still the per-raise warp attribution rather than a formality. Both manifests
  carry per-raise `ANOMALY_ECHO` records with `ut` stamps, so that attribution is
  now an offline join over two committed artifacts rather than another flight.
  **THIRD DATASET 2026-08-27, AND IT IS A DISCRIMINATOR RATHER THAN ANOTHER COUNT:
  `V20T-jool-kerbin-ts-arrival` reading run 1 (`2026-08-27_1857`) raised the token
  THREE times, ALL AT `warpRate=1 dt=0.0167`** - dead 1x, the regime this entry's
  hypothesis says should be clean - **and each raise carries the orbit pair it jumped
  between**: `fromOrbit=[Jool|10246978796|0.9424] toOrbit=[sma=590325785 ecc=0.0000]`,
  i.e. the recording's post-escape Jool ellipse re-seeded onto its SEGMENT ZERO, with
  `dPos` 334,981,793 / 1,623,652,431 / 2,935,994,339 m. **THOSE THREE ARE NOT THRESHOLD
  FALSE POSITIVES - they are a real re-seed the probe correctly caught**, which means
  the token has AT LEAST TWO DISTINCT PRODUCERS and the per-raise warp attribution this
  entry asks for must separate them rather than bucket them together. Their own home is
  TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS above; they
  are recorded here because they constrain THIS entry's hypothesis, not because they
  belong to it.
  CONSEQUENCE ALREADY TAKEN, and it is a tolerance and not a fix: V24W now lists
  `icon-teleport` BARE in `allowedAnomalies` citing `2026-08-25_1502`. The
  ceiling that measurement authorizes is 130 (2x the measured 65, DOUBLED rather
  than tightly bracketed precisely because this probe defect is open and that
  lane commands rails), and it cannot be declared while
  `test_no_committed_spec_arms_a_count_budget` and
  `test_every_committed_spec_parses_under_the_budget_surface` hold the budget
  mechanism inert across every committed spec - the same blocker V14T and V15T
  each hit and each backed out of. So an over-sensitive threshold currently costs
  the suite an UNBOUNDED tolerance on a third lane rather than a bounded one,
  which raises the value of fixing the probe rather than lowering it.
- **RESEED-LAG-DARK-GAP-AT-CLOCK-JUMP: the ghost proto orbit line goes dark for
  ~3 frames when a discontinuous clock step overruns the applied segment's end
  bound, because the reseed lags the clock** [OPENED 2026-08-26 off V25M reading
  2 (`harness/results/2026-08-26_1817_V25M-duna-park-player-loop.json`), the
  lane's single `line-blink` raise. GENUINE MINOR RENDER TRANSIENT - the
  instrument is NOT the suspect here. Owner: whoever owns the map-render segment
  reseed]. Mechanism, read straight off the collected trace (pid=2657480491,
  recId=aa48920e, frames 7784-7787): the third arrival TimeJump landed
  `currentUT` 43 s past the applied segment's end bound
  (`bounds=[...,5360144042.2]` vs `currentUT=5360144085.0`), the line went OFF
  with `reason=stale-segment-awaiting-reseed`, and 3 frames later the reseed
  landed (`bounds=[...,5360144182.1]`) and the line relit via
  `director-stockconic-visible`. Everything downstream behaved per contract:
  `stale-segment-awaiting-reseed` stamps NO `RenderWindowCoverage` BY DESIGN
  (its "outside bounds" is the applied bounds lagging INSIDE the window - see
  the `MapRenderTrace` entry in `.claude/CLAUDE.md`), so the
  `windowTransitionExempt` half-proof cannot apply (`priorToggleVerdict=Other`)
  and the `line-blink` raise is the instrument correctly reporting a real
  ~100 ms dark flicker at the jump. TWO FACTS THAT BOUND THE DEFECT: (1) it is
  TIMING-DEPENDENT, not deterministic - reading 1 (`2026-08-26_1744`) drove the
  IDENTICAL jump triple and raised zero, so whether the reseed lands in the
  same frame as the jump or a few frames later decides the raise; (2) TimeJump
  is not required - a high-warp frame can also advance the clock past a stale
  segment's end bound in free play (one frame at 100,000x is thousands of
  seconds), so the same dark gap is reachable by warp overrun at any segment
  boundary. Possible fix directions (NOT taken - file-only entry): hold the
  previous line state through the `stale-segment-awaiting-reseed` frames
  instead of darkening (the segment is known to be lagging, not ended), or
  reseed synchronously when the overrun is detected on the same frame.
  CONSEQUENCE ALREADY TAKEN, tolerance not fix: V25M lists `line-blink` bare in
  `allowedAnomalies` citing `2026-08-26_1817`, ceiling 2 (2x measured) in
  prose, pending the budget-mechanism allowlist move.**
- **V15M-LINEBLINK-IS-TRACEDPATH-HANDOFF-CADENCE: V15M's single `line-blink`
  raise is the Director's DESIGNED StockConic->TracedPath descent handoff at
  the Gilly loop tail, flagged only when frame cadence lands the toggle pair
  inside the 8-frame blink window - pre-existing, deterministic in UT, and NOT
  from PR #1556 or the map-render wave** [ATTRIBUTED 2026-08-28 by comparing
  the #1556 confirmation flight `2026-08-28_1703` (branch `watch-mode-fixes`
  fc49e71d5; collected log `logs/2026-08-28_2004_V15M-gilly-player-loop`)
  against the lane's arming flight `2026-08-19_1810` (branch `gilly-loop-lane`
  9301b945d; collected log `logs/2026-08-19_2111_V15M-gilly-player-loop`;
  result JSON in `Parsek-gilly-loop-lane/harness/results`). Owner: whoever
  owns the `MapRenderTrace` blink exemptions. **REMEDY (a) SHIPPED 2026-08-28**
  - see FIX DIRECTIONS at the end of this entry for what landed].
  THE TWO RUNS RAISE THE IDENTICAL EVENT, NINE DAYS AND TWO CODEBASES APART:
  same recId `77f724bb`, same `currentUT=16656457.000`, same
  `intentReason=director-traced-path-suppress`, same missing-exemption
  fingerprint (`offWindowCovered=False polylinePainted=False
  polylineOwns=False windowTransitionExempt=False bodyChanged=False
  priorToggleVerdict=InsideWindowOn toggleVerdict=Other`), `sinceFrames` 5
  (2026-08-28) vs 8 (2026-08-19), both <= `LineBlinkFrameWindow=8`. The
  2026-08-19 run predates the ENTIRE map-render wave (#1526-#1551; the M-A7
  manifest landed 2026-08-25) and ran the dead-watch-camera code (it is the
  443-NRE-storm flight), yet raised the same blink at the same UT. That kills
  BOTH candidates PR #1556's todo note deliberately left open: "main moved"
  (the raise existed before main moved) and "watch working changes what the
  map sees" (the raise fires with the watch camera dead and alive alike; the
  same-millisecond `Watch focus dist=786m` coincidence in `_1703` is script
  ordering, not causation). **PR #1556 is EXONERATED, and so is the
  map-render wave.** When #1556 merges, fold its unattributed note into this
  entry.
  MECHANISM (read off both traces, and replicated by BOTH ghost incarnations
  within EACH run): after the loop-cycle rollover the proto re-resolves onto
  the recording's tiny terminal Gilly orbital window (bodyFrame
  [16656187.2, 16656357.5], ~170 s): line ON `director-stockconic-visible`
  (Inside stamp). A few ~130 s warp frames later the drive clock crosses the
  last recorded orbit segment's end into the TracedPath descent leg;
  `ShadowRenderDriver.IsTracedPathOwnedThisFrame` flips true and the
  `GhostOrbitLinePatch` Postfix kills the line with
  `director-traced-path-suppress` (which stamps NO `RenderWindowCoverage` and
  `hasBounds=false` BY DESIGN - it is not one of the four stamping sites).
  The proto never relights: it retires ~25 frames later (`left-orbit-segments`,
  "Orbit proto retired AT terminal orbit bound"). A designed, permanent
  handoff - not a flicker.
  WHY NO EXEMPTION CAN MATCH: `bodyChanged` false (Gilly->Gilly);
  `windowTransitionExempt` needs an Outside stamp the suppress site never
  writes; `polylinePainted`/`polylineOwns` false because ownership publishes
  ONLY on an ACTUAL polyline draw and the Driver never drew this run - V15M is
  a map-closed flight-scene lane (renderCompose:
  `ownership-publish-surface-never-ran`). So the exempting fact the closed
  V1-REPLAY-LINE-BLINK diagnosis established for handoffs (the
  paint/ownership bit) is structurally unavailable exactly when the map is
  closed - i.e. exactly when NO line is visible to any viewer and a "blink"
  has no observer.
  CADENCE, NOT CODE, DECIDES THE RAISE: in BOTH runs the FIRST incarnation
  crossed the identical handoff one loop earlier and was NOT flagged
  (`sinceFrames=10 > 8`: frames 6834->6844 in `_1703`, 7108->7118 in
  `_1810`); the second incarnation landed at 5 and 8. Whether ~130 s/frame
  stepping crosses the ~170 s terminal window in <= 8 frames is frame-rate
  jitter. Corollary: **V15M has never had a green armed run** - the arming
  flight itself red'd on this exact raise (its NRE storm took the attention),
  so the 2026-08-28 red reproduces the lane's standing state; it is not a
  regression.
  NOT the creation-frame render family: that family (V20 artifacts,
  TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS)
  is overlap protos
  SETTLING onto segment zero at creation because jump order sets creation
  frames. Here incarnation 2 was created correctly onto the loop-shifted
  visible segment (segIdx 2-3, `from visible-segment`) and behaved per
  contract until the designed handoff; TimeJump's only role is cadence.
  FIX DIRECTIONS: (a) the principled exemption
  EXISTS as a positive fact stamped at a site whose branch condition IS the
  measurement: the OFF edge's branch condition is
  `IsTracedPathOwnedThisFrame(pid, frame)` - "the Director's spine says a
  non-orbital leg owns this pid this frame" is a measured transition fact,
  not a widened window. Exempt an OFF whose intent is
  `director-traced-path-suppress` with that selector true and a prior Inside
  ON. CAVEAT to design around: bare, it would also exempt a map-OPEN handoff
  where the polyline then FAILS to draw (a real dark gap) - so conjoin the
  ownership/paint bit whenever the publish surface ran, and accept the
  selector alone only when it never ran (the case with no visible line at
  all). (b) V25M-style tolerance: list `line-blink` bare in V15M
  `allowedAnomalies` citing `2026-08-19_1810` + `2026-08-28_1703` - same
  unbounded-tolerance cost as the V24W/V25M precedents while the budget
  mechanism stays inert. Preference: (a) - unlike RESEED-LAG above (a real
  transient) this raise is a measurement artifact of a designed handoff.
  **SHIPPED 2026-08-28: (a), with the caveat designed around.** The stamp is a
  new two-value enum `MapRenderTrace.LineHandoffKind` (`None` /
  `TracedPathOwned` - an enum, not a bool, so every write must be SPELLED and is
  therefore countable by a source gate) written at EXACTLY ONE site - the
  `director-traced-path-suppress` branch, whose own condition IS
  `IsTracedPathOwnedThisFrame` - riding the SAME single-writer intent channel as
  `RenderWindowCoverage` (`RecordLineIntent`). It deliberately does NOT stamp
  coverage: that site hides the line because the spine handed the leg away, not
  because a clock left a window, and the two exemptions stay disjoint (the
  coverage cell's 2/2 counts would red if the suppress site became a fifth
  coverage stamp). The pure predicate is
  `MapRenderTrace.ResolveTracedPathHandoffExempt`, fail-closed on SIX
  conjuncts: definitively DARK edge; fresh intent AGREEING with the truth read;
  handoff `TracedPathOwned`; prior toggle a proven `InsideWindowOn` (the same
  both-halves discipline `ResolveWindowTransitionExempt` enforces); and TWO
  anti-masking conjuncts. (5) When the ownership/paint publish surface RAN this
  frame, the polyline must ALSO actually have covered the ghost
  (`polylinePainted || polylineOwns`), so a map-OPEN handoff that claims the leg
  and never draws still raises. (6) THE SELECTOR-ALONE LANE IS ITSELF A POSITIVE
  FACT - it requires a positively measured CLOSED map (`mapWasOpen` false), never
  the absence of a publish. `publishSurfaceRan == false` is a NEGATIVE fact and
  strictly broader than map-closed: the Driver walk also misses its epilogue on
  the TRACKSTATION / FLIGHT controller-not-yet-awake defers (both AFTER the
  `MapView.MapIsEnabled` gate), on any exception escaping the walk body, and when
  no Driver exists - all reachable with the map OPEN and nothing drawn, i.e.
  exactly what the detector is for. The alone-lane's justification is
  `ownership-publish-surface-never-ran` meaning no line on screen for anyone to
  see blink, and only the closed map establishes that.
  "Did the publish surface run" reuses an EXISTING signal rather than a new
  per-frame flag: `GhostTrajectoryPolylineRenderer.DidOwnershipPublishRunOnFrame`
  reads the Driver's `pendingDrawsFrame` walk-completed stamp - written in the
  decide walk's epilogue, after every early return (scene gate,
  `MapView.MapIsEnabled`, controller defers), and already asked the identical
  question one slot later by `OnMapCameraPreCull`. Ordering, stated exactly
  because it is easy to invert: that stamp is written ~50 lines BEFORE
  `NoteOwnershipPublish`, not after it, and what makes the reuse sound is that
  the probe's actual inputs (the ownership + S0 paint sets) are populated during
  the per-recording walk, ahead of the stamp; the recorder publish below it is
  the manifest's own diff, which the probe never reads. Both the raise and the
  `line-blink-suppressed` lines now carry `tracedPathHandoffExempt=` /
  `intentHandoff=` / `publishSurfaceRan=` / `mapWasOpen=`, so an exempted pair
  stays visible rather than going silent - and the last two together separate
  "coverage proof missing" from "walk never reached its epilogue while the map
  was open". Gates in `LineBlinkWindowExitExemptionTests`: the archived V15M
  fingerprint replayed at both cadences (exempt post-fix), the pre-fix-behavior
  proof driven through the full replay with `intentHandoff: None` (still raises
  at the archived geometry - the hardcoded three-guard helper beside it is
  detector CHARACTERIZATION, not a fails-before proof), the first incarnation's
  `sinceFrames=10` unraised before AND after with the post-fix half deliberately
  given NON-exempting inputs so it pins the cadence arithmetic rather than
  short-circuiting at the exemption, the map-OPEN never-draws masking pin, the
  map-OPEN-but-walk-deferred pin (conjunct 6), the fail-closed conjuncts, a
  one-spelling source gate on the handoff stamp, and a pin that the
  publish-surface signal keeps reusing the walk-completed stamp AND that the
  stamp still precedes the publish. **V15M's armed `anomalySweep` is expected GREEN on the
  next nightly** - this raise was its only standing red (the lane has never had
  a green armed run; its arming flight red'd on this same event), so that sweep
  IS the regression catcher for this change.
- ~~**GHOST-MAP-TEARDOWN-NRE-WHEN-CAMERA-TARGETED: destroying a ghost map vessel
  that is the `PlanetariumCamera`'s current target NREs stock's KnowledgeBase
  during the forced retarget**~~ **FIXED 2026-10-06, branch `release-cheap-fixes`:**
  the camera is moved off a targeted ghost while the UI is still intact. Finding: the
  NRE is not on the dying MapObject but on `KbApp_PlanetParameters`' own transform -
  every collected quit log (V15M `2026-08-28_2004`, H5, R1, H36, GS-2) prints
  `KbApp.OnDestroy Planet Parameters` before the stock `Focus:` line, so ANY retarget
  fired from an OnDestroy-time teardown NREs, whoever fires it (stock picks the nearest
  body, `FindNearestTarget`). Fix: pure
  `GhostMapPresence.DecideCameraRetargetBeforeGhostRemoval` (camera on a dying ghost ->
  active vessel, else the ghost's reference body, else the home body; during a scene
  teardown the active vessel is skipped, since it dies with the scene and stock would
  retarget again from its OnDestroy) behind the thin `RetargetPlanetariumCameraOffDyingGhosts`
  (null-guarded `PlanetariumCamera.fetch`, Info `Planetarium camera retargeted off dying
  ghost ...`). Called before the Die loop of every in-scene `RemoveAllGhostVessels`
  (rewind / scene-change request via `DestroyAllTimelineGhosts`, TS Fly, test resets),
  and from `ParsekHarmony.OnApplicationQuit` (before Unity destroys anything) for the
  quit; the OnDestroy removals (`scene-cleanup`, `tracking-station-cleanup`, new
  `sceneTeardown: true`) and any removal after the quit latch stand down with a Verbose
  line rather than fire the event into a torn-down KnowledgeBase themselves. Not
  covered: a Tracking Station scene CHANGE with the camera on a ghost still lets stock
  retarget at ghost destroy (no request-time hook in the TS; same as before), and the
  single-ghost removers keep stock's retarget (the UI is alive there, no NRE). Tests:
  `GhostMapCameraRetargetTests`. Original report: [OPENED 2026-08-26 off V25M reading 3
  (`harness/results/2026-08-26_1823_V25M-duna-park-player-loop.json`,
  `unityExceptions` report-only row: 2 NRE lines, both this one event's ERR+EXC
  pair). Owner: `GhostMapPresence`]. Stack has NO Parsek frames but the trigger
  is ours: `RemoveAllGhostVessels reason=scene-cleanup` at 21:23:49.942 destroys
  `Ghost: Kerbal X` while it is the camera target; stock then walks
  `Vessel:OnDestroy -> PlanetariumCamera:OnVesselDestroy -> SetTarget ->
  KnowledgeBase.OnMapFocusChange -> KbApp_PlanetParameters.ActivateApp`, which
  NREs on the dying MapObject's transform. INTERMITTENT BY CONSTRUCTION -
  readings 1 and 2 of the same lane (`2026-08-26_1744` / `_1817`) logged ZERO
  NREs, because it only fires when the camera happens to be targeting a ghost at
  teardown. The same collect also shows the adjacent, already-warn-logged
  `Die() threw for 'Ghost: Kerbal X Probe'` in the same sweep - two symptoms of
  the same "destroy while stock still references it" moment. Candidate fix (NOT
  taken - file-only): in `RemoveAllGhostVessels` (and the single-ghost removal
  paths), if `PlanetariumCamera.fetch?.target` resolves to the ghost being
  destroyed, retarget the camera (active vessel / home body) BEFORE `Die()`.
  Benign in effect today (scene is being torn down anyway; stock swallows the
  exception), but it dirties every armed lane's `unityExceptions` census and
  would mask a real NRE regression behind an expected one.**
- **`maxUtStep` on a DWELL is POLLUTED by a seam `TimeJump` epoch shift that
  lands inside an open dwell, so every tolerance derived from it balloons on
  jump-driven lanes** [OPENED 2026-08-25 off V24W reading flight 2
  (`2026-08-25_1502`). REPORT-ONLY: nothing red on it, no code changed, and no
  rule reads it as a gate today. Owner: `RenderCompositionRecorder` /
  `rendercompose`]. `DWELL.maxUtStep` is the largest single head-UT step observed
  while the dwell was open, and it exists so a rule can size a tolerance against
  how coarsely THAT dwell was sampled (the RC-HOLD leg-2 attribution window and
  RC-COVER's gap resolution both derive from it). It assumes the step measures
  SAMPLING RATE. A seam `TimeJump` breaks that assumption: the epoch shift is a
  discontinuity in the clock, not a coarse sample, and if it lands while a dwell
  is open it becomes that dwell's `maxUtStep`. MEASURED on flight 2's manifest
  (`harness/results/2026-08-25_1502_V24W-duna-one-warp-stair_shots/parsek-render-manifest.txt`),
  which has four `DWELL` records and a clean split:
    * `61e9177193444e329247d0e8288cf91e` seg 6 (heliocentric-transfer, closed),
      loop UT 64044033.92 -> 70898645.36: `maxUtStep = 6846984.9646892548`.
    * `6561c8eb97dd48d6825e9d6c7c04d22a` seg 1 (departure-loiter, still open at
      export), loop UT 64043475.99 -> 70931658.54: the SAME
      `maxUtStep = 6846984.9646892548`.
    * the other two, which no jump crossed: `maxUtStep = 2` and
      `maxUtStep = 53745.585304260254`.
  Two dwells reporting the identical 6,846,984.96 s to the digit is the tell -
  that is one clock event stamped into both, not two independently sampled
  intervals. The lane's own `driver.midMissionSeamWrites` records
  `verbs ["MissionConfig", "TimeJump"]`, and the depart -> arrive leg's compressed
  offsets (80,672.8 -> 6,935,286.1) bracket the value, so the producer is
  identified rather than inferred. The result JSON's
  `quality.maxUtStepSeconds = 6846984.964689255` is the max over all four, i.e.
  the run-level facet is the polluted one. IMPROVEMENT, recorder side (preferred,
  because the recorder is the only layer that can see the seam verb): PARTITION a
  dwell at TimeJump epochs, or exclude an epoch-shift step from the `maxUtStep`
  accumulation - the seam verb is observable at the point the shift is applied,
  so this is a classification the recorder can make honestly rather than a
  heuristic. VERIFIER-SIDE ALTERNATIVE if the recorder cannot be touched: cap the
  contribution `maxUtStep` may make to a derived tolerance, so one epoch shift
  cannot silently widen an RC-HOLD or RC-COVER window by six orders of magnitude.
  WHY IT IS NOT URGENT: no armed window reads `maxUtStep` today, and V14M / V8
  are TimeJump-driven but their dwells are short enough that the question has not
  yet cost a reading. It becomes urgent the moment an RC-HOLD or RC-COVER clause
  is armed on a jump-driven lane, which V24W's own arming pass will be.

---

## BDOCK-2-SAME-TREE-DOCK-COVERAGE: no harness scenario flies a same-tree cross-session dock

`BDOCK-1-station-interceptor` covers the cross-tree dock recording pipeline; the
same-tree cross-session shape (fly A, commit, switch-fly the offshoot D, dock
A->D: records single-parent with a same-tree target, model extract section 1.5)
is flown by no spec. Candidate `BDOCK-2`: fly, commit, switch-fly, dock, assert
the recovered same-tree link derives. Not a merge gate for the dock-event-graph
scope (the derivation is pinned by synthetic in-game cells); file when docking
coverage next expands. Also missing per BDOCK-1's own header note: an
orbital-rendezvous-dock D10 value and a same-craft-twice identity D18 value.

## PART-ACTION-RECORDING-COVERAGE: audit backlog for what Parsek records vs the stock part-action surface [OPEN 2026-08-09]

Full matrix and reasoning:
`docs/dev/research/part-action-recording-audit-2026-08-09.md`. 103 stock
`PartModule` types decompiled and cross-matched against the 35 `PartEventType`
values, 22 `GameStateEventType` values, 59 subscribed `GameEvents`, the playback
dispatch and the Re-Fly restore path. That doc is the authority; this entry is
the index so the items are not lost.

**Both MUST-table `C` items are now closed.** `C1` (restore non-slot vessels,
asteroids/comets, flags) shipped 2026-08-09 - see the
REFLY-DELETES-NON-SLOT-WORLD entry above. `C2` (pre-invoke advisory naming what
the revert takes back) shipped 2026-08-11 into the Re-Fly confirmation dialog +
one `[Rewind]` Info line; details in that same entry.

**The `S` table is now CLOSED OUT: S1-S3 and the wheel corollary shipped 2026-08-11
(`playback-fidelity`), and S4 / S6 / S7 shipped 2026-08-12 (`part-event-fidelity`/P8),
which also converted the §2 science-timeline row into four explicit WON'Ts.** Nine new
`PartEventType` members (36-44), no schema-generation bump (verified against both
sidecar readers rather than assumed), and a new in-game category `PartEventFidelity`
wired as `H37-part-event-fidelity`. **CONFIRMED LIVE 2026-08-12** by the H37 re-fly
(`total=5 passed=5 failed=0 skipped=0`, every verifier green): the break/repair/loop
round trip on `solarPanelOX10C` (break subtree `rootHinge`, 13 transforms - so the
`breakName` -> `pivotName` fallback resolves in production, which was the wave's
likeliest silent no-op), the converter loop at `quarterRot=90.0001deg` with
`driftAfterStop=0deg`, the empty-deploy-name ISRU shape with 4 looping transforms, and
the EVA plume built and gated. Its FIRST flight red 3/5 and bought two fixes worth
naming here, since both are the wave's own failure mode - a replay that renders or
reports plausibly rather than correctly:
- **A silent `Play()` no-op.** `ParticleSystem.Play()` on a ghost that is not
  `activeInHierarchy` neither throws nor sets `isPlaying`, and a ghost is inactive for
  the whole of its spawn-time prefix replay - so an EVA ghost spawning mid-burst stayed
  dark for the entire burst while the log reported it emitting. Fixed with a pure
  `ClassifyEvaPlumeReconcile` that DEFERS on an inactive hierarchy, a per-frame
  `UpdateEvaJetpackPlumeForFrame` self-heal beside the launch-dust drive (dust already
  had that property for free by re-calling Play every frame), and decision-vs-truth
  logging that reads `isPlaying` BACK rather than reporting success on the strength of
  having called Play.
- **A rotation-blind test.** The Goo verification cell skipped VACUOUSLY because its
  precondition tested POSITION only, while a science canister's `Deploy` clip swings its
  doors; the re-fly's `span(pos=0 rot=29.99998deg)` on `mk2LanderCabin.v2` is the
  diagnosis in one number. Precondition and assertions are now per-component against the
  builder's own `CollectTransformDeltas` thresholds. No `RUNTIME_SKIPS` entry: the skip
  was fixed, not accepted. Three findings from P8 worth carrying forward
because each corrects something a future reader would otherwise trust:
- Deployable `BROKEN` is **REVERSIBLE** (stock `eventRepairExternal` -> `DoRepair` lands
  on RETRACTED), so it is a reversible-family split seed, not a permanent one. The audit
  described it as "permanent".
- Of the three KerbalEVA members, `JetpackDeployed` IS a `[KSPField]`; the other two are
  not. Typed casts remain correct, but the reason is "two of three are invisible to a
  `module.Fields` walk", not "none is a KSPField".
- The EVA pack MESHES are still absent, and NOT because the asset is unreachable
  (`KerbalEVA.JetpackTransform` is a public serialized `Transform`). The missing piece is
  the VISIBILITY signal - `HasJetpack`, driven by inventory contents - which Parsek does
  not record; the honest follow-on is to read the snapshot's `ModuleInventoryPart`.
- **`EmitTerminalEngineAndRcsEvents` must not mutate its tracking sets** - all four
  families, jetpack included. The first cut of the S4 terminal close removed each pid as
  it emitted, which reads like double-emit safety but breaks the false-alarm unwind: the
  STOP path leaves the sets intact ON PURPOSE so `ResumeAfterFalseAlarm` can undo an
  abandoned chain-boundary stop and keep tracking a burn that never ended, and with the
  pid gone the resumed poll's real thrust-end edge hit
  `thrustingParts.Remove(pid) == false` and emitted nothing - the pair stayed open to
  recording end, the exact failure the terminal emit exists to prevent. Double-emit safety
  is the CALLER's, exactly as for engines: the rails site
  (`EmitTerminalEventsAndClearActiveState`) clears every set right after the emit, so the
  second emit's `Count > 0` gate is a no-op. Fixed 2026-08-12; guarded by
  `PartEventFidelityTests.AFalseAlarmStopMidBurst_ResumesTracking_AndClosesAtTheREALThrustEnd`
  and `...TheRailsPathClearIsWhatMakesASecondTerminalEmitANoOp_NotARemoveOnEmit`.
- **NOTE ONLY, no fix owed:** a kerbal-to-kerbal `ContinueOnEva` switch mid-burst closes
  the DEPARTING kerbal's thrust pair only at the recording's final-stop UT, not at his
  real thrust end - late, not lost. `PruneDepartedTrackingKeys` covers the engine / RCS /
  robotic sets and does not reach the EVA sets, so the departing pid stays in
  `thrustingJetpackParts` until a terminal emit closes it. Recorded because the late close
  is a plausible-looking plume, not an obviously wrong one.

The `M` items and the remaining non-`S` items below are untouched.

**Establish the restore model before reasoning about any "world desyncs" claim.**
The RP quicksave is a full `GamePersistence.SaveGame`, so the whole `GAME` node
comes back. Anything in a `PART`/`MODULE` node on the selected slot is restored
verbatim and is BENIGN - experiment `Deployed`/`Inoperable`, container contents,
lab accrual, ISRU tank gains, per-part resource amounts,
`flowState`/`flowMode`/crossfeed, `ACTIONGROUPS`, seats, inventory, `BROKEN`
panels, ablator. So are ore depletion and biome/planet unlock (they live in
`ResourceScenario`, a `GAME`-node scenario) - `grep ResourceMap Source/` returns
0 hits and that is fine, not a gap. Recording those buys divergence DETECTION,
not correctness.

Open items, highest leverage first:

- ~~FIXED 2026-08-11 (PR #1443)~~ **Ghost initial state is read from the part PREFAB, not the recorded
  snapshot.** The snapshot IS a full `ProtoVessel` backup
  (`FlightRecorder.cs:6080-6082`) but `GhostVisualBuilder` reads only
  name/pid/pos/rot (`:431-441`) and builds from `ap.partPrefab`. Exactly three
  module states are read from it: `ModuleJettison.isJettisoned`, fairing
  fsm/XSECTION, `ModulePartVariants`. Everything else rides a 16-family
  `PartStateSeeder` whitelist. The promotion gate
  (`FlightRecorder.cs:6544-6581`) deliberately emits engine-only seeds for EVERY
  continuation segment including Re-Fly forks, so a post-rewind ghost renders
  gear up / panels folded / lights off for its whole span. Fix by reading the
  snapshot at BUILD time (sidesteps the `#263` `FindLastInterestingUT` invariant
  the gate protects). MUST land with the robotic split-seed fix - a
  `RecordingTreeSplitter` TIP inherits the parent's LAUNCH-UT snapshot, so the
  snapshot read alone gives forks a confidently-wrong pre-launch pose.
- ~~FIXED 2026-08-11 (PR #1443)~~ **Robotic events are in neither split-seed family** and
  `ReapplySpawnTimeModuleBaselinesForLoopCycle` never calls `ApplyRoboticPose`.
- ~~FIXED 2026-08-11 (PR #1445)~~ **Two confidently-wrong recorded signals** that playback faithfully renders:
  parachute REPACK is classified as CUT (`FlightRecorder.cs:1665-1690`) so a
  repacked chute renders as an empty can; and wheel spin records `driveOutput`
  (percent-of-max-torque) replayed at `value * 6` deg/s as if RPM
  (`GhostPlaybackLogic.cs:3588`), with `Mathf.Abs` making reverse identical to
  forward and a coasting rover showing stationary wheels. Re-deriving spin from
  trajectory ground speed is storage-NEGATIVE.
- ~~FIXED 2026-08-11 (branch `playback-fidelity`)~~ **Recorded magnitude that playback discards.**
  `SetEngineEmission` branched only on `power > 0f`; only the audio curves read the
  magnitude, and `ComputeScaledRcsEmissionRate`/`...Speed` had ZERO production call
  sites. **Fix:** both `Set*Emission` choke points now scale the plume as a RATIO of a
  baseline captured once at build time (`GhostVisualBuilder.CaptureFxMagnitudeBaselines`,
  run AFTER the #383 size boost and the world-space velocity floor so scaling composes
  with them rather than overwriting them), written into the cloned `KSPParticleEmitter`'s
  own persistent fields plus the `ParticleSystem` multipliers. Persistent fields, not a
  per-event computation, because `RestoreAllRcsEmissions` re-enables emitters WITHOUT
  going through `SetRcsEmission` — a per-event scale would restore a full-magnitude plume
  on a quarter-throttle thruster after every high-warp suppression. The two RCS helpers
  are the ratio's numerator and denominator, which gives them production callers with
  their showcase visibility floors intact. Degradation is one-directional: an unreadable
  baseline answers ratio 1.0, i.e. today's boolean behaviour, never a zero plume. Audio
  untouched (it already consumed the magnitude; touching it would double-apply). Review
  follow-up: the one baseline the ratio cannot scale freely is a WORLD-SPACE emitter's
  `localVelocity`, because that baseline may BE the minimum-flow floor
  (`ApplyWorldSpaceEmitterVelocityFloor`, 6 m/s) rather than a magnitude — a 0.2 ratio would
  write 1.2 m/s, back under the 4 m/s pooling threshold the floor exists to clear.
  `GhostPlaybackLogic.ScaleEmitterLocalVelocity` re-clamps that one write to the floor (never
  above the baseline's own magnitude) and leaves every other write a plain ratio; reachable only
  on ReStock's world-space SRB smoke at genuine partial throttle.
  **FLIGHT FOLLOW-UP 2026-08-12: the whole of the above was a SILENT NO-OP in the live game
  until now.** The H36 run of 2026-08-11 logged, for engine midx=0, engine midx=1 AND rcs
  midx=0, `FX magnitude write failed ... Object of type 'System.Single' cannot be converted to
  type 'System.Int32'.; plume stays at its baseline`. `KSPParticleEmitter.minEmission` and
  `maxEmission` are declared **int** (`minSize`/`maxSize` are float, `localVelocity` is Vector3
  — verified by decompiling Assembly-CSharp, not assumed); `FieldInfo.SetValue` performs no
  numeric conversion, so a boxed float threw on the FIRST of the three writes and, because all
  three shared one `try`, took the other two with it. **Fix:** every write converts to the
  `FieldInfo`'s own `FieldType` (`GhostPlaybackLogic.TryConvertMagnitudeForField`) and each
  field is written independently; integral fields round to nearest with a NONZERO floor, which
  keeps `ComputeFxMagnitudeRatio`'s "never zero for a lit engine" contract at the quantisation
  boundary, and the floor keeps the SIGN so stock's negative-`maxEmission` "does not emit"
  sentinel survives. `CaptureFxMagnitudeBaselines` now type-checks each field at capture and
  drops an unwritable one to null instead of caching it for the applier to fail on every frame.
  **Why three review passes and 66 headless cells missed it:** every headless cell pinned the
  ratio ARITHMETIC (floats in, float out) and none ever met the real field types; and the
  in-game suppress/restore cell passed VACUOUSLY, because "unchanged baseline in, unchanged
  baseline out" satisfies a round trip perfectly. Both holes are now closed — headless cells
  reflect over the real `KSPParticleEmitter` type and perform a real `SetValue` against an
  uninitialised instance (`FormatterServices.GetUninitializedObject`, no ctor, no ECall), and
  the RCS cell asserts the scaled value differs from the captured baseline BEFORE asserting the
  round trip preserves it. `FxMagnitudeWriteFailureCount` is a hard-zero assertion in both
  plume cells, because the failure LOG line is rate-limited to one per minute per module and so
  undercounted a total no-op as a curiosity.
  **CONFIRMED IN FLIGHT 2026-08-12** (H36 re-fly, run `2026-08-11_2211`, PASS 7/7). The write
  LANDS on the real cloned emitters and the scaled values are genuinely off the baseline:
  engine `part='ionEngine' fullSpeed=6.5 lowSpeed=1.95 fullEmission=350 lowEmission=105
  restored=6.5 writeFailures=0` — the int-typed `maxEmission` scaled 350 -> 105 at reduced
  throttle and came back EXACTLY to 350 at full, which is the ratio-not-rewrite property — and
  RCS `part='mk1-3pod' baselineSpeed=48 scaledSpeed=12 afterRestore=12 baselineEmission=400
  scaledEmission=104 afterEmission=104 writeFailures=0`, where the scaled value DIFFERS from the
  baseline (the anti-vacuity assertion) and then survives the `RestoreAllRcsEmissions` round
  trip unchanged. ZERO `FX magnitude write failed` lines anywhere in that run's KSP.log, against
  three (engine midx=0, engine midx=1, rcs midx=0) on the first flight.
- **Five dead reflection probes**, four of them documented as shipped at
  `done/next-parts-event-support-priority.md:43-47`. `module.Fields` is
  `[KSPField]`-only (`FlightRecorder.cs:3733-3748`); `ModuleControlSurface`'s
  real field is `deploy` and the table lists `deployed`; the piston probe latches
  `traverseVelocity`, a `[KSPAxisField]` SPEED SLIDER that resolves and is
  constant during a stroke, shadowing the working `servoTransformPosition`
  fallback; `ModuleAnimateHeat`'s live scalars are plain public fields whose
  accessor is the `IScalarModule.GetScalar` property - ONE cast lights up the
  complete already-built Hot/Medium/Cold playback path
  (`GhostPlaybackLogic.cs:3693-3770`).
- **Recorder cache and rails holes.** `cachedEngines` is assigned only in
  `ResetPartEventTrackingState` (sole caller `StartRecording`) and
  `CheckEngineState` guards only `part == null`, so a staged-away booster that
  keeps burning writes into the PARENT recording. The background rails
  transition ERASES state instead of deferring: `BackgroundRecorder.cs:2316-2336`
  drops `loadedStates` with no terminal emit, so a BG ghost's plume latches on
  for the whole rails span.
- ~~FIXED 2026-08-11 (branch `playback-fidelity`)~~ **Continuous motion to SYNTHESIZE, never
  sample:** gimbal and control-surface deflection, wheel steering, sun tracking, launch dust.
  **Fix:** one new per-frame entry, `GhostPlaybackLogic.UpdateSynthesizedMotion`, beside
  `UpdateActiveRobotics`, everything gated so a craft with none of these families pays a
  reference compare. ONE CORRECTION TO THE ORIGINAL PRESCRIPTION: the attitude derivative
  reads the ghost's APPLIED WORLD ROTATION, never `TrajectoryPoint.rotation` from a flat
  Points list — that field is anchor-local in a RELATIVE track section, so differencing two
  of them across a section boundary mixes frames and invents a rotation that never happened.
  Wheel steering became a new `RoboticVisualMode.WheelSteeringHeading` that IGNORES the
  recorded `ModuleWheelSteering` scalar for the same reason wheel spin ignores
  `driveOutput`: it was an unsigned steering INPUT, not a caliper angle. Review follow-up: the
  PRODUCER is gone too, following the #1445 precedent exactly — once playback discards every
  `RoboticMotion*` event in that mode the scalar is a write-only surface, so the recorder gate
  widened from `IsWheelMotorSpinModuleName` to `IsDerivedWheelVisualModuleName` (foreground and
  background). Storage-negative, playback stays tolerant of legacy events, nothing retired in
  `PartEventType` (hinges, pistons, rotation servos, rotors and wheel SUSPENSION still use it). Launch dust is
  narrower than written — Parsek owns its own particle system (the reentry `fireParticles`
  template) instead of driving `ModuleSurfaceFX`, is built lazily under the existing
  per-frame build cap, and is gated on a ground reference latched from
  `recordedGroundClearance`; no clearance anywhere in the trajectory means no dust,
  permanently, rather than an invented reference that would put a dust cloud around a ghost
  in orbit. Every new mutable visual resets in BOTH `ResetForLoopCycle` (the numbers) and
  `ReapplySpawnTimeModuleBaselinesForLoopCycle` (the transforms), including a new
  `WheelSteeringHeading` branch in `ApplyRoboticSpawnBaseline` — `ApplyRoboticPose` writes
  nothing for that mode, so without it a caliper left turned would carry across a loop
  boundary while the numbers claimed straight. Also review follow-up: under SUSTAINED warp the
  steering rate now DECAYS toward zero on every re-seed frame instead of holding its last value
  (`DecayRateTowardZero`), so a warping rover eases its calipers straight rather than freezing
  mid-turn; the same re-seed hold is left in place for gimbals and control surfaces on purpose
  (bounded by the clamp, invisible at warp's visual scale, self-correcting on the first
  sub-second frame). Live coverage: the `PlaybackFidelity` in-game category (7 cells) driven by
  `harness/scenarios/H36-playback-fidelity.toml` (FLOWN twice: 2026-08-11 PARSEK-FAIL 5/7, then
  LIVE-PROVEN on the 2026-08-12 re-fly after both fixes — run `2026-08-11_2211`, PASS attempt 1,
  `total=7 passed=7 failed=0 skipped=0`; the tally pin is now whole and the id has left
  `INTERIM_PIN_IDS`).
  **FLIGHT FOLLOW-UP 2026-08-12, the sun-tracking red.** `SunTrackingPivotAimsOnlyWhenFully-
  Deployed` red with `solarPanelOX10C` never resolving an aim angle. It was NOT the deployed
  gate — the same run's `Spawn baseline: stowed 1/1 deployable(s)` line proves the part's
  `DeployableGhostInfo` exists and that `ApplyDeployableFraction` applies, so the cell's
  ARM-2 immediate deploy necessarily set `deployFraction = 1`. It was the AIM FRAME.
  `DriveSunTracking` measured the angle about the PARENT's up and from the pivot's neutral
  world FORWARD, while `ApplySunTrackingAngle` post-multiplies `neutralRotation *
  AngleAxis(angle, axisLocal)` — i.e. applies it about the PIVOT'S OWN local axis. Two
  consequences: the measured axis and the applied axis disagreed whenever the pivot's neutral
  rotation was not identity, and on a pivot authored with a quarter-turn (OX-10C) the reference
  landed PARALLEL to the parent's up, its projection into the aim plane vanished, and
  `TryComputeAimAngleDegrees` declined every frame forever. **Fix:** both the axis and the
  reference are now read in the pivot's own neutral frame
  (`parentRotation * neutralRotation * axisLocal`), and the reference is ORTHOGONALISED against
  the axis by construction (`ResolveSunTrackingReferenceLocal`), so the only surviving decline
  is the legitimate one — the Sun lying along the rotation axis. This also matches stock, which
  tracks with `Atan2(pivot.InverseTransformPoint(sun).x, ....z)` applied as
  `pivot.rotation * Euler(0, y, 0)`: the pivot's own local +Y as axis, its own local +Z as the
  zero-angle reference. **Diagnosis cost, now paid down:** the cell could only list the two
  candidate causes. `DriveSunTracking` now emits two discriminating rate-limited lines
  (`Sun tracking held (gate closed) ...` with the full deployable gate state, and
  `Sun tracking held (aim unresolved) ...` with both aim-plane projection magnitudes), and the
  cell pastes `GhostPlaybackLogic.DescribeSunTrackingState` into its own failure message — a
  near-zero `targetPerp` is the legitimate hold, a near-zero `referencePerp` is a frame bug,
  and `gate=closed` is a deployable-path bug.
  **CONFIRMED IN FLIGHT 2026-08-12** (H36 re-fly, run `2026-08-11_2211`, PASS 7/7). The cell's
  own state line now reads `part='solarPanelOX10C' stowedDrift=0 aimed=90 holdDelta=0 gate=open
  deployable=present currentDeployed=True transitionActive=False deployFraction=1 transforms=13
  aimResolved=True aim=89.99999 targetPerp=1.04331008E+10 referencePerp=1 currentAngle=89.99999
  hasAimed=True` — an angle RESOLVES on the very pivot that declined every frame before, and the
  discriminating numbers read exactly as designed: `referencePerp=1` (the orthogonalised
  reference is now unit-length in the aim plane, where the frame bug drove it to zero) beside a
  large `targetPerp`, with `stowedDrift=0` proving the deployed gate still holds the pivot still
  and `holdDelta=0` proving it holds once aimed.
  The gimbal cell additionally carries the SIGN/HANDEDNESS pin for the hand-rolled quaternion
  product — the headless cells build their inputs in the convention the product assumes, which
  is circular, so the only non-circular authority is Unity's own
  `Inverse(prev) * cur -> ToAngleAxis`, compared by axis dot rather than by unsigned angle.
- **Recorded magnitude that playback discards.** `SetEngineEmission`
  (`:2172-2204`) branches only on `power > 0f`; only `:2405`/`:2423` (audio
  curves) read the magnitude. `ComputeScaledRcsEmissionRate`/`...Speed` exist at
  `:3354`/`:3367` with ZERO production call sites (only
  `RuntimePolicyTests.cs:210,219,228,230`). Worst on Waterfall installs, whose
  premise is a throttle-continuous plume.
- ~~FIXED 2026-08-11 (S5)~~ **Dead reflection probes**, documented as shipped at
  `done/next-parts-event-support-priority.md` (that section is now corrected in
  place, per-bullet, with the decompiled reason). `module.Fields` is
  `[KSPField]`-only, so a plain public field is invisible however obvious its
  name. Four real fixes and one audit correction:
  - `ModuleControlSurface` / `ModuleAeroSurface`: the real field is `deploy`
    (`[KSPField(isPersistant)]`), added at the HEAD of
    `AeroSurfaceDeployedFieldNames`. `deployAngle` / `aeroDeployAngle` are a
    separate VETO table, not deflection candidates - they are `[KSPAxisField]`
    tweakables that `OnStart` resolves from `NaN` to `ctrlSurfaceRange`, so
    treating them as "non-zero means deployed" would have classified every
    control surface in the game as permanently out. `aeroDeployAngle` leads
    because an airbrake carries BOTH and deactivates the inherited one.
  - `ModuleAnimateHeat`: not fixable by names. New non-name-keyed
    `IModuleFieldValues.TryGetScalarModuleScalar` → one `as IScalarModule` cast
    (`ModuleAnimationSetter.GetScalar => inputState`), consulted BEFORE the name
    table. Lights up the complete already-built Hot/Medium/Cold recorder +
    playback path.
  - `ModuleRoboticServoPiston`: `currentExtension` + `targetExtension` prepended;
    `traverseVelocity` (a `[KSPAxisField]` SPEED slider, constant during a
    stroke) and `targetPosition` (`private float`, never reachable) removed. Note
    the DELIBERATE divergence from the snapshot-side ghost baseline, which reads
    `targetExtension`: only that one is `isPersistant`, so it is the only one a
    saved craft carries, while only `currentExtension` sweeps live.
  - `ModuleWheelSuspension`: `suspensionOffset` (a config constant applied once to
    the wheel collider) dropped so the live `suspensionPos` vector fallback wins.
  - `ModuleRobotArmScanner`: **the audit claim was wrong; nothing changed.** It
    derives from `ModuleDeployablePart`, whose `[KSPEvent] Extend()`/`Retract()`
    the scanner actively toggles, so the probe's event-activity stage resolves.
    Its `ArmDeployState` is behind a `new` property over a private unattributed
    `_deployState` (unreachable by name) AND redundant - that setter mirrors every
    arm state onto the base `deployState` that `CheckDeployableState` polls, so an
    accessor would emit a duplicate `DeployableExtended` under a second key.
- **Recorder cache and rails holes.**
  - ~~FIXED 2026-08-11 (M5)~~ **Cached engine/RCS/robotic modules were polled
    without a vessel-identity check.** `cachedEngines` was assigned only in
    `ResetPartEventTrackingState` (sole caller `StartRecording`) and
    `CheckEngineState` guarded only `part == null`, so a staged-away booster that
    kept burning wrote into the PARENT recording. Fix: the pure
    `FlightRecorder.DecideCachedModulePoll` gates every per-frame read on
    `ReferenceEquals(part.vessel, recordedVessel)` - a LIVE object comparison, not
    a pid or guid one, so the craft-baked-pid identity rule does not apply - across
    the foreground engine/RCS/robotic polls AND their background mirrors in
    `BackgroundRecorder.PartEventPolling.cs`. `FlightRecorder.OnVesselWasModified`,
    wired from the already-subscribed `ParsekFlight.OnVesselWasModified`, rebuilds
    the three cache LISTS, which also closes the inverse hole: a welded-on
    EVA-construction engine or a newly docked module was absent from a cache built
    at `StartRecording` and emitted nothing at all for the rest of the flight.
    **Rebuild is FOREGROUND-ONLY, deliberately.** `BackgroundRecorder` assigns its
    per-vessel caches once in `InitializeLoadedState` and has no
    `onVesselWasModified` hook, so a module ARRIVING on a BG-loaded vessel still
    records nothing until that vessel next re-enters loaded state. Accepted, not
    overlooked: the BG half of the ownership guard stops the POLLING direction (a
    departed part writing into the wrong recording), and the missing direction
    needs a per-BG-vessel subscription whose cost scales with the background
    fleet. The M5b re-review sharpened the residual: the D1 key-rot shape itself
    also survives on the BG side - a BG-loaded vessel that sheds a burning part
    with NO detected split keeps the departed key in
    `loadedState.activeEngineKeys` (guard skips polls, no prune hook), and
    `EmitBackgroundRailsTerminalEvents` writes a stale `EngineShutdown` for that
    pid at the next rails transition. Bounded differently from the FG case:
    DETECTED sheds route through `CloseParentRecording`, which discards the
    parent's `loadedState` so the stale key dies unemitted; the residual
    manifests only for undetected sheds, and becomes a genuine tail artifact
    only when the vessel then stays on rails until commit.
  - ~~FIXED 2026-08-11 (M5b)~~ **Departed-part keys rotted in the tracking sets.**
    The M5 guard makes a departed booster's burnout UNOBSERVABLE, so its key never
    left `activeEngineKeys` - and the terminal emit
    (`EmitTerminalEventsAndClearActiveState` at the rails transition,
    `FinalizeRecordingState` at stop) walks the ACTIVE sets, writing an
    EngineShutdown / RCSStopped into the PARENT recording for a pid that left
    minutes earlier. It landed at the TAIL, where
    `RecordingOptimizer.IsInertPartEventForTailTrim` counts EngineShutdown as
    interesting, so the #263 boring-tail trim was defeated. Fix: the pure
    `FlightRecorder.PruneDepartedTrackingKeys`, called from `OnVesselWasModified`
    (which the `ContinueOnEva` rebuild also routes through), silently drops keys
    for departed pids across all eight cache-fed collections - `activeEngineKeys` /
    `lastThrottle` / `allEngineKeys`, `activeRcsKeys` / `lastRcsThrottle` /
    `rcsActiveFrameCount`, `activeRoboticKeys` / `lastRoboticPosition` /
    `lastRoboticSampleUT`. No synthetic event: the Decoupled event already hides
    the subtree and the child recording owns the burn. Two subtleties. (1)
    Survival is measured against `Vessel.parts` UNIONED with the rebuilt caches,
    not the caches alone, so a part whose module list momentarily reads empty (the
    dock/undock shuffle window) is not pruned into losing a continuing burn; an
    all-empty read prunes nothing at all (`CanPruneAgainstSurvivingPids`). (2)
    Engine/RCS state is MOVED to `DepartedEngineThrottles` /
    `DepartedRcsThrottles`, not dropped: `InheritedEngineState.FromRecorder` (the
    #298 parent-to-child snapshot) is taken from `ProcessBreakupEvent`, a whole
    crash-coalescer window AFTER `onVesselWasModified` fires, so an unconditional
    prune would delete "the booster was at full throttle when it came off" before
    the child debris recording could inherit it. `MergeInheritedEngineState`
    filters by the child's own part pids, so a carry-over entry only ever reaches
    the child that holds that part; an entry is dropped the moment its pid returns.
  - ~~FIXED 2026-08-11 (M6)~~ **The background rails transition ERASED state
    instead of deferring it.** `OnBackgroundVesselGoOnRails` dropped `loadedStates`
    with no terminal emit (BG ghost's plume latched on for the whole rails span),
    and on re-entry `SeedBackgroundPartStates` re-synced every tracking set to live
    truth while `TrySeedLoadedPartEvents` declined to write (`PartEvents.Count > 0`),
    so a change during the warp was erased rather than deferred. Fix, two halves:
    `EmitBackgroundRailsTerminalEvents` runs the same
    `FlightRecorder.EmitTerminalEngineAndRcsEvents` the foreground already ran at
    ITS rails transition (a vessel that packs with nothing running still emits
    nothing, so a parked rover's boring tail is not extended), and
    `CaptureRailsSpanPartStates` deep-copies the now-quiet tracking sets into
    `railsSpanPartStates`. On re-entry `TryEmitRailsSpanDiff` feeds them to the pure
    `PartStateSeeder.EmitDiffEvents` (all 17 families - six pid-keyed boolean sets,
    blinking lights, parachutes, six module-keyed deployable sets, thermal, engines,
    RCS; one-way shroud/fairing emit on ARRIVAL only; parachutes route through the
    shared 4-state `ClassifyParachuteTransitionEvent`; `EngineThrottle` quantises on
    the shared `FlightRecorder.EngineThrottleDeadband`). Snapshot is consumed once
    and dropped at every BG teardown site - `OnBackgroundVesselWillDestroy` was
    missing its drop and got one, since `persistentId` is craft-baked and an orphan
    would be handed to the next launch of the same craft. Both invariants are now
    drift-proofed by reflection/source sweeps rather than hand-enumerated lists:
    `PartTrackingSetsFieldSweepTests` asserts the deep clone copies every field to a
    distinct instance and the reconciler is SENSITIVE to every field (explicit
    exempt-list with reasons; `allEngineKeys` is the only entry), and
    `RailsSpanSnapshotTeardownGateTests` asserts every `loadedStates.Remove` method
    also drops `railsSpanPartStates` (sole exemption: the go-on-rails capture site).
    The `FlushLoadedStateForOnRailsTransitionForTesting` seam now runs the terminal
    emit and the snapshot capture too, so injected fixtures exercise the product
    transition rather than the pre-M6 one.
- **Continuous motion to SYNTHESIZE, never sample:** gimbal (`ModuleGimbal`, 243
  stock parts, ZERO Parsek references) and control-surface deflection from the
  recorded `srfRelRotation` derivative; wheel steering from heading change; sun
  tracking from `Planetarium.fetch.Sun`; launch dust (`ModuleSurfaceFX`, 183
  parts, zero references) from engine power + altitude. Precedent:
  `ApplyAblationChar` already synthesizes reentry char from live physics.
- ~~**Career-bearing modules with a modest visual, which fell through both the "is
  it visible" and "does the quicksave restore it" sieves:**~~ **RESOLVED AS FOUR
  EXPLICIT WON'Ts, 2026-08-12** (`part-event-fidelity`/P8) - net ZERO new event
  types, and the audit's own §2 row is CORRECTED in the process. Full reasoning and
  evidence in
  `docs/dev/research/part-action-recording-audit-2026-08-09.md` -> "CORRECTION
  2026-08-12"; the load-bearing findings:
  - `ModuleScienceExperiment` (158): the deploy visual was **already recorded**. The
    animation belongs to a SEPARATE `ModuleAnimateGeneric` named `Deploy` (Goo and
    Science Jr, wired via `FxModules = 0`), and `ModuleScienceExperiment` is not in
    `HasDedicatedAnimateHandler`, so `CheckAnimateGenericState` has always polled it.
    The audit's "`Deployed` ... gates the deploy animation" wording is what made the
    verdict look like a gap. P8 added the VERIFICATION that never existed instead of a
    recorder: an in-game cell that reds if the claim stops holding.
  - `ModuleDataTransmitter` (201): the only stock transmit visual is one the recorder
    ALREADY polls. **Evidence corrected 2026-08-12** - the first write-up claimed zero
    stock parts set `DeployFxModuleIndices` / `ProgressFxModuleIndices`, which was a
    GREP-NAME TRAP: those are the C# field names, while the cfg keys `OnLoad` parses into
    them are `DeployFxModules` / `ProgressFxModules`. Six stock antennas DO set
    `DeployFxModules = 0` (HighGainAntenna, commDish88-88, commsAntennaDTS-M1,
    commsAntenna16, HG-5, HG-5_v2), and index 0 is each part's `ModuleDeployableAntenna`
    - so the stock transmit visual is the dish extending, a `deployState` change the
    existing `ModuleDeployablePart` poll has always recorded. `ProgressFxModules` (the
    transmit-progress visual proper) is the one with genuinely zero stock setters. Verdict
    unchanged; a transmit event type would be a second recorder for one visual.
  - `ModuleTestSubject` (709): the tested part's own action is already recorded by its
    family, and contract completion is already in `GameStateEvent` types 2/15/17.
  - `ModuleOrbitalSurveyor` (2): M700 deploy already recorded via the AnimationGroup
    family; `PerformSurvey` has no part visual and fires
    `GameEvents.OnOrbitalSurveyCompleted`. The planet-unlock facet is a LEDGER concern
    for a ledger wave.
- **Ledger facets:** kerbal XP is zero-coverage (`ModuleTripLogger` /
  `flightLog` / `ArchiveFlightLog` / `experienceLevel` all return NOTHING across
  `Source/`) and survives a supersede that refunds the funds; it is monotone, so
  the patcher is a re-assert. Contract snapshots are ACCEPT-time only
  (`GameStateRecorder.Handlers.cs:84-93`, and `ContractsModule.cs` has zero
  occurrences of "parameter"), so `PatchContracts`' rebuild branch
  (`KspStatePatcher.cs:2050-2085`) returns a reinstated contract at 0/N
  waypoints.
- **UNTESTED INTERACTION, trace before assuming:** `ScienceChanged` is in the
  seven-facet patch set while the `ResearchAndDevelopment` node - including each
  subject's decayed `scientificValue` - is a `GAME`-node facet reverted to the
  rewind UT. A surviving branch's post-rewind `ScienceChanged` rows are
  re-applied against a rolled-back subject table. `PatchScience` was NOT traced.
  The boundary between a restored `GAME`-node scenario and a patched ledger facet
  that reads from it is where the remaining rewind bugs will be.
- **Doc defects:** `done/next-parts-event-support-priority.md:43-47` (five
  families claimed shipped, all dead probes); the rewind design doc §7.13 still
  claims v1 never un-completes a contract, but `PatchContracts` can remove a
  tombstoned finished row.
## HARNESS-TIER-TAXONOMY: `tier` encodes cadence membership, not cost or readiness, so "run everything" needs out-of-band knowledge [RAISED 2026-08-02 by the `V1-map-dwell-mun-orbit` promotion (PR #1407). NOT STARTED. Design change against a binding authority; the shape below is a problem statement, not a chosen solution]

The invocation model we actually want is agent-driven: an agent runs the WHOLE
batch on request, or a cheap smoke subset. The selection layer is built around
scheduled cadences instead, and the two do not line up.

### What exists today, measured not assumed

`run.py` selects by `--id | --tier | --tag | --cadence` (no other selector).
`hlib.TIERS` is `("perpr", "daily", "nightly", "weekly", "pending-fixture",
"operator")` - four cadence memberships plus two EXCLUSIONS - and
`CADENCE_TIERS` is cumulative:

| cadence | tiers it resolves to |
|---|---|
| `per-pr` | `perpr` |
| `daily` | `daily` |
| `nightly` | `daily`, `nightly` |
| `weekly` | `perpr`, `daily`, `nightly`, `weekly` |

with an explicit note in `hlib` that `operator` is in NO set: those specs "are
never picked up by a cadence and run only under an explicit `--tier operator` /
`--id`". Spec counts on 2026-08-02: nightly 34, daily 22, operator 5,
`pending-fixture` 0, `weekly` 0.

### The friction, stated precisely

1. **"Run everything" is three invocations plus knowledge.** `--cadence weekly`
   is the closest thing and it IS nearly-all, but it silently omits the
   `operator` and `pending-fixture` tiers, so the full set is
   `--cadence weekly` + `--tier operator` + `--tier pending-fixture`. Nothing
   tells a caller that; you have to know `CADENCE_TIERS` leaves them out. There
   is no `--all`.
2. **There is no smoke set.** One spec is named `H13-ksp-api-smoke` and a
   `SMOKE-autopilot` fixture exists, but no tag or selector means "cheap, fast,
   proves the stack is alive". The 18 tags in use are subject-matter labels
   (`mechjeb`, `rewind`, `eva`, `mun`), not cost labels.
3. **The data to select on already exists and nothing selects on it.**
   `coverage/duration.json` (tracked) holds per-scenario `last/p50/p95`;
   `coverage/flake.json` (gitignored, advisory) holds the trust signal. A
   cost-aware or trust-aware selection is a read away and is not wired.
4. **`tier` therefore does double duty** - it is read as a cost/readiness label
   by humans (see every spec's TIER header) while functioning as cadence
   membership. `operator` is not "a human runs it"; it is "no cadence will pick
   this up", which is why a green scenario can sit invisible to automation.

### Why it bites, with the case that raised it

`V1-map-dwell-mun-orbit` sat at `tier = "operator"` while GREEN and fully
automated, invisible to every cadence, until a human promoted it on 2026-08-02.
Nothing failed; nothing could. That is the same class
`PendingOperatorTagHonestyTests` already documents for `EVA-1-pad-flag` ("the
tier stays nightly until the operator promotes it" - a pending human call with
no signal that could go red), and the same reason that check exists as two
hand-maintained lists.

### Constraints for whoever takes this

- `docs/dev/design-autotest-harness-core.md` (M-A5) is the BINDING authority for
  the selection layer; section 10 owns the cadence -> tier map. Change the doc in
  the same commit.
- `hlib.TIERS` is validated by `validate_spec`, so the vocabulary cannot be
  narrowed without a spec sweep.
- `PendingOperatorTagHonestyTests` pins the `pending-operator` carrier set in
  BOTH directions AND covers the `tier = "operator"` population, so collapsing
  or renaming the tier vocabulary reds it until that inventory is re-reasoned -
  by design.
- `harness/lib/test_doc_spec_sync.py` reads `docs/dev`, so spec/doc drift is
  caught there.
- Additive beats destructive: an `--all` selector and a cost tag can land
  without touching `TIERS` at all, which is the cheap first step.

### What done looks like

An agent can ask for "everything" or "the smokes" in ONE invocation with no
out-of-band knowledge, and selection can use the cost and trust data the harness
already records. Whether `tier` then shrinks to a pure cost/readiness label or
disappears is the open design call, not a foregone one.

[2026-08-05] Invocation is now AGENT-DRIVEN ON REQUEST: the operator asks for a tier by type, and an agent flies it via `harness/tools/tier_runner.py --tier {daily|nightly|operator}` following the `run-tier` project skill (`.claude/skills/run-tier/SKILL.md`; mechanics in `harness/README.md` -> "Running a tier on request (agent-driven)"). That is the direction this item's "the invocation model we actually want is agent-driven" line points, and it removes the out-of-band memory for the three per-tier invocations - but it is still only packaging: it adds no `--all`, no smoke selector, no cost or trust-aware selection, and touches neither `TIERS` nor `CADENCE_TIERS`. The taxonomy question above stands unchanged.

## R7-SESSION-BATCH-ISOLATION: running the `Rewind` category beside a LIVE re-fly session breaks seven of its own tests, and starves nine more [FOUND 2026-08-04 by roadmap R7's abandoned `R7b-rewind-session-live` spec. FOUR defect-items FIXED here across THREE tests ((1)+(2) in `F5MidReFlyResume`, (2b), (3)); the FOUR remaining failing tests in (4) are RECORDED, not fixed. The nine starved are collateral of (2b), explained below - the spec is NOT committed]

R7 set out to drive `Rewind` in both of its precondition modes. R7a (no live
session) shipped green. The second spec - arm a REAL session with the seam's
`InvokeRewind`, then run the category inside it, so the twelve marker-dependent
members stop skipping - did NOT ship, and the reason is worth writing down
because it is a property of the tests, not of the fixture.

**Four flights, and every one taught something.** All measured on
`career-pad-craft` + `rewind-crew-loss` + `InvokeRewind rp=rp_cl_root slot=1`,
merge dialog deliberately unanswered:

| Flight | Tally | What it found |
|---|---|---|
| `2026-08-04_1333` | `passed=14 failed=1 skipped=22` | `F5MidReFlyResume` FAILED - see (1) |
| `2026-08-04_1641` | `passed=15 failed=1 skipped=21` | after (1)+(2): `KerbalRecoveryOnSupersede` reached its core assertion for the FIRST TIME EVER and failed - see (3) |
| `2026-08-04_1644` | `passed=15 failed=1 skipped=21` | a `StartRecording` step does NOT bind to the re-fly provisional; refusal reason unchanged |
| `2026-08-04_1646` | `passed=10 failed=4 skipped=23` | after (3): four MORE tests reached their assertions and failed - see (4) |

**(1) `F5MidReFlyResume` asserted a total-count proxy. FIXED.** Its assertion
compared `RecordingStore.CommittedRecordings.Count` before and after
`LoadTimeSweep.Run()`, while its own comment said it meant "any Immutable /
CommittedProvisional entries". The total is a proxy that holds only while the
test's synthetic provisional is the sole NotCommitted entry. Beside a real
session there are two, the test installs its own marker, and the sweep CORRECTLY
discards the now-orphaned real one (`Zombie discarded rec=...
supersedeTarget=cl-pod-a`). Product was right; the assertion was reading a
correct discard as a failure. Now counts settled (non-NotCommitted) recordings.

**(2) `F5MidReFlyResume` is destructive beside a real session. FIXED.** Even with
(1), the sweep still reaps the real provisional, and restoring the marker
afterwards cannot un-discard the recording - so every later marker-dependent test
finds a marker whose `ActiveReFlyRecordingId` resolves to nothing, which is an
`InGameAssert.IsNotNull` FAIL rather than a skip. It now SKIPS when a foreign
session is live, naming the context per the house rule.

**(2b) `JournalFinisherMarkerPresentVariant` ATE the session. FIXED.** It borrows
the live marker, runs the finisher (which clears the marker - that is the
asserted behaviour), restores the prior JOURNAL, and never restores the MARKER.
It had no `try`/`finally` at all, unlike every sibling in the family. That single
omission starved NINE later members, which skipped on "No active re-fly session"
having been reachable moments earlier (`ContractTombstonesAcrossSupersede` and
`InvokeRPStripAndActivate` both ran ahead of it and saw the marker). Now restores
in a `finally`, after the assertions, so nothing is weakened.

**(3) `KerbalRecoveryOnSupersede` asserted against an unmet precondition. FIXED.**
This is the test the status doc records as having AUTO-SKIPPED SINCE IT WAS
WRITTEN for want of a kerbal death in a supersede subtree. The crew-loss fixture
finally gave it one - `found 1 kerbal-death action(s) covering 1 kerbal(s)` - and
it then failed on "KerbalAssignment+Dead action must be tombstoned after merge".
NOT a product defect: the merge logged `AppendRelations
outcome=refused-unflown-provisional ... reason=empty Points -- the re-fly attempt
has no playable trajectory, so it cannot replace the origin`, then `Tombstoned 0
career actions`. A spec that arms a session without FLYING it cannot produce a
supersede, and without a supersede there is nothing to tombstone. The test now
skips on an unflown provisional; on CL-3's flown shape (which measures
`Tombstoned 1 career actions (... Kerbal=1 ...)`) the guard is false and every
assertion runs unchanged.

**(4) NOT FIXED - four more of the same family, and why the spec was abandoned.**
With (3) no longer consuming the session through a real `CommitSupersede`, four
further members reached their assertions and failed:
`MergeInterruptionRecovery` ("Expected supersede relations to be durable at
Durable1Done; got 0" - the same unflown-provisional root cause as (3)),
`DiscardReFly_PrelaunchContext_DispatchesEditorWithFacility`
("DiscardReFlyLoadGameForTesting should fire exactly once"),
`ReFlyRevertDialog_Prelaunch_BlocksStockRevert_AndShowsDialog` ("Prelaunch body
copy should mention VAB") and
`DiscardReFly_LaunchContext_PreservesSiblingState_DispatchesSpaceCenter`
("Marker should be cleared after Discard Re-fly"). All four install their OWN
synthetic marker and drive a GLOBAL handler, then assert on global state a real
session perturbs. All four PASS in R7a, where no session exists.

THE GENERAL SHAPE: roughly a third of this category is written as
"install synthetic session state, drive the handler, assert" and is only correct
when it is the only session in the process. That is a reasonable thing for a
test invoked by hand from Ctrl+Shift+T to assume and a wrong thing for a batch
run inside a live session. Fixing them one at a time is real work with a real
risk of weakening assertions, and it was out of proportion to the remaining
yield - R7a already executes 16 members, and the session-live mode's best
measured result was 15.

WHAT A FUTURE ATTEMPT SHOULD DO DIFFERENTLY, in order:
1. CONCLUDE the re-fly - land or crash it and save - not merely fly it. That is
   a mission-driven spec (CL-3's shape), not a seam-only one. `StartRecording`
   after `InvokeRewind` does NOT work - flight `2026-08-04_1644` measured the
   refusal reason unchanged.
   CORRECTED 2026-09-09 by RF-6 (see `RF6-FOUR-REWIND-CELLS-FAIL-ONLY-WITH-A-LIVE-SESSION`
   above): this item used to read "Every unflown-provisional failure above
   disappears if the provisional carries Points", and carrying Points is
   NECESSARY AND NOT SUFFICIENT. `SupersedeCommit.ValidateSupersedeTarget` has a
   second clause. RF-6 measured both, one flight apart, on the same lane: without
   a time jump the merge refused with `reason=empty Points points=0
   trackSections=0`; with one it refused with `reason=null TerminalState points=1
   playableSections=1 terminal=<null>`. The time jump DID clear the payload
   clause and the tally was byte-identical because the terminal clause caught it.
2. Then give the four in (4) the same foreign-live-session skip guard (2) got.
3. Expect the ceiling to stay near R7a's: the first member to perform a real
   merge ends the session for everyone after it, which is inherent, not a defect.

---

## HARNESS-SHELL-READSET-UNCHECKED: a mission shell can read a telemetry field its own control flags never populate, and nothing catches it until a flight [RAISED 2026-08-05 by GS-2 flight 1, which was lost to exactly this. IDEA, NOT STARTED - recorded with its cost so a future wave can decide rather than rediscover]

**What happened, as the concrete instance.** `gs2_orbital_probe_deploy`'s DEPLOY
gate read `TelemetrySnapshot.vessel_count`, and its shell built the control with
`read_docking=False`. That field is populated ONLY inside `if self._read_docking:`
(`mission_runner.py:678-686`), so it sat at its `0` unread sentinel for the whole
flight and the gate compared two sentinels. The shell even carried a comment
asserting the field was "in the base snapshot" - a runner-population claim that was
simply false, written by hand and checked by nobody. One flight lost
(`2026-08-05_0842`).

**Why the existing discipline did not catch it.** Every opt-in channel is carefully
documented AT THE FIELD (`crew_count` says "Read only when the control was built
with `read_crew=True`"), and `forge_lko`'s shell documents its own opt-in and why.
The convention is good. What is missing is that nothing MECHANICALLY relates the
two: a shell's flags and its machine's read-set are written in different files, by
different authors, at different times, and drift silently.

**The shape of the check.** For each mission shell: derive the set of snapshot
fields its machine actually reads, derive the set the runner populates under that
shell's control flags, and assert the first is a subset of the second. It would
have caught this at unit time, and it covers every future shell for free.

**Why it is not built.** The population side is easy (the flags are literals in
`make_control`, and the runner's opt-in blocks are greppable). The READ side is
not: "which snapshot fields does machine X read" needs either an AST walk over
functions resolved per mission - fragile, since machines are named by convention
rather than registered - or a declared read-set per machine, which is a new
contract every mission must maintain and can itself go stale. For a three-lane
wave that is more machinery than the bug costs. The judgement to revisit: **if the
mission library grows past ~5 lanes, or the next shell adds a fourth opt-in flag,
build it.**

**The cheap 80% in the meantime**, which costs nothing and is where a future author
should start: a cell asserting that any shell whose machine mentions a
population-gated field sets the matching flag, for the SHORT closed list of gated
fields (`vessel_count`/`read_docking`, `crew_count`/`read_crew`,
`craft_chute_state`/`read_chute`, `node_executor_enabled`/`read_node_executor`,
`time_to_periapsis`/`read_periapsis`, the landing block/`read_landing`,
`read_camera`). That list is short, stable, and already written down at each field.

**Blast radius of the instance, verified rather than assumed** (Lane A, 2026-08-05):
`gs2_orbital_probe_deploy` was the ONLY `read_docking=False` shell reading
`vessel_count`. Every consumer of `mlib.separation_evidence`
(`bdock_dock_transfer`, `forge_lko`) sets the flag, as do b11/b12/b13/b14/b16 and
v1; GS-1 reads the field nowhere. And the instance was a CAN-NEVER-SUCCEED hazard
rather than a silent-wrong-pass: every consumer compares `current > baseline` with
the baseline captured from the same always-0 field, so an unread channel yields
`0 > 0` and fails closed. See the trap note on the field itself for the shapes
(`== 0`, `< N`) that would NOT fail closed.

---

## Autotest coverage: build-order TODOs from the basics roadmap [TODO, branch `autotest-roadmap`]

Rationale, dependency justification, measured costs and the full uncovered-cell
breakdown are in `docs/dev/autotest-roadmap.md`. Do not restate them here; these are
the actionable units only. Coverage ground truth they are all sized against:
`hlib.compute_coverage` over the 38 committed specs returned 241 values / 83 covered /
158 uncovered, with D1 at 13 of 18 uncovered, D13 at 11 of 11, D17 at 6 of 6.
(**84 covered / 157 uncovered** since R1's debris gate landed 2026-07-27, D3 6 -> 5.
**Re-measured 2026-07-28 at `7f5efa738`: 55 specs, 242 values / 97 covered / 145
uncovered** - the denominator moved because CL-1 added D12 `crew-death-in-flight`
per the growth rule. D1 is 8 of 18 uncovered, D13 7 of 11, D17 still 6 of 6.
Status of the items below at that HEAD: R1 SHIPPED (#1362), R2 STILL OPEN (both
phantom cells verified present), R3 partly overtaken (#1357 re-tier +
R1-EMPTY-PROVISIONAL resolved as a fixture artifact; first green nightly rows
for S1.5/S4.1 still unconfirmed), R4 mostly shipped (#1358 + H21; FinalizeLimbo
and Bug289 remain), R5 SHIPPED (#1367), R11 done. Re-measure rather than
trusting this line; it is a snapshot, not a maintained total.)

**R1. Gate the recording surfaces the B-lane already produces on every nightly.**
~~Debris population on the five Kerbal X flights~~ DONE 2026-07-27 (branch
`claude/docs-pr-testing-tasks-f4bor2`); the rest below is still open.

**Shipped.** `B2-lko-ascent`, `B4-reentry-splashdown`, `B5-mun-flyby`,
`B6-minmus-flyby` and `B7-duna-flyby` all fly `fixtures/saves/b2-lko-craft` (the
stock Kerbal X), shed six radial boosters, and record each as a parent-anchored
debris child - while their windows read `count = { min = 1, max = 8|9 }`, so the
total loss of that population read PASS. Each now requires a debris-creation token
and a per-mission `count.min`. D3 `parent-anchored-debris` claimed on all five;
coverage 83 -> 84 of 241, D3 6 -> 5 uncovered. Proof: the next nightly, no new
flight. A red is a real finding - re-pin `count` to the newly measured value and
record which recordings the run produced; do NOT widen back toward 1.

**THE FIRST CUT SHIPPED THE WRONG TOKEN AND WOULD HAVE RED ALL FIVE.** Caught by
independent review before merge; recorded because the mistake is repeatable.
`Child recording created \(debris, TTL=` (`BackgroundRecorder.cs:1177`) was pinned
as "the SOLE creation site". It is one of TWO, and it is the BACKGROUND-split one:
reachable only via `OnBackgroundPartJointBreak`, which early-returns unless the
vessel is in `tree.BackgroundMap`, and `RecordingTree.IsBackgroundMapEligible`
excludes `rec.RecordingId == ActiveRecordingId`. These craft shed boosters while
ACTIVE, so it cannot fire; staging goes through `ParsekFlight.ProcessBreakupEvent`
-> `CreateBreakupChildRecording` -> `ProcessBreakupEvent: debris child created:
pid=` (Info, tag `Coalescer`, `ParsekFlight.cs:7668`). Cost had it merged: ~3.8 h
of flying and five reds that read as a Parsek recording regression. **Rule:
presence in source is not reachability on a profile. Trace the call chain or grep
an archived KSP.log before pinning** - the discipline this entry already
prescribed for the tokens it declined to claim.

The intermediate fix accepted EITHER site, because the trace above was argued from
source and never confirmed against a log. **The shipped gate requires the
FOREGROUND token alone**, settled 2026-07-27 by grepping all 60 archived B-lane
`logs/*/KSP.log` folders (B2 10, B4 8, B5 27, B6 6, B7 9): the foreground token
appears in 58 of 60, and the substring `Child recording created` - broad enough to
catch the CONTROLLED sibling at `:1185` as well - appears in **zero**. The two
without it are `2026-07-20_{1846,1854}_B2-lko-ascent`, both INVALID runs that
recorded nothing. A green ascent emits it exactly 6 times, one per booster.

**`min` is per-mission, and the rule is not the flameout watchdog.** The floor
follows whether the mission commands a debris-producing stage drop BEYOND launch
ignition. `B5`/`B6`/`B7` drop a flameout-staged core (`_b5_flameout_stage`, reached
only from `b5_decide`) -> 8. `B4` drops its service stage via an
`ACTION_ACTIVATE_STAGE` on the SOLE transition into `B4_REENTRY` -> also 8, and
`run.py` judges expectations only on a driver-valid non-short-circuited run, so a
B4 that never reached REENTRY is SKIPPED rather than judged under a lower floor.
Only `B2` stages once, at ignition -> 7 (structural: `mlib.py:401-405` records that
the spent core never autostages, because MechJeb autostage fires only on EMPTY
stages and the Kerbal X core keeps residual fuel).

**Every floor is now MEASURED, not derived.** Read 2026-07-27 off
`verifiers.expectations.observed.recordings.count` in verdict=PASS result JSONs
(the field landed in `72cf344fb`, 2026-07-25 06:48, so every citation is from that
morning): B2 7 (`2026-07-25_0824`), B4 8 (`0828`), B5 8 (`0643`, `0847`), B6 8
(`0636`, `0856`), B7 8 (`0916_a2`). Both previously-unmeasured floors - B4's
structural derivation and B6's inference from the shared decide function -
measured at exactly what was derived, so no re-pin was needed.
**Do not re-derive these counts from the archived `logs/*/` folders.** `run.py`
collects logs on NON-PASS only (`run.py:2324`), so every archived B-lane folder is
a run whose expectations were SKIPPED rather than judged; their `.prec` sidecars
number 7 for B4 and B6 because those runs aborted before the extra stage drop.
Reading that 7 as a contradiction and lowering the floor would re-open the exact
hole this entry exists to close.
The first cut used 7 everywhere; the second kept B4 at 7 by keying on
`_b5_flameout_stage` alone. Both left the exact value `B11`/`B12` record as
**considered and rejected**: "7 is the exact count a single dropped recording would
produce, so a floor of 7 would blind the only numeric guard on this run to the
regression class it exists to catch."

**Corrections to the roadmap's R1 section, found by building it:**
- **The tokens are NOT all `ParsekLog.Verbose`.** All four `BackgroundRecorder`
  population tokens (`Child recording created (debris, TTL=` :1177,
  `(controlled, no TTL):` :1185, `Debris TTL expired, ending recording:` :1307,
  `Sample rate changed: pid=` :1966) are `ParsekLog.Info`, as is the foreground
  `ProcessBreakupEvent: debris child created:`. The rest of that table is MIXED,
  not uniformly Verbose: `starting hysteresis timer` is Verbose at both sites, and
  the `Part event:` family is ~39 Verbose sites plus exactly one Info
  (`FlightRecorder.cs:3862`) once `BackgroundRecorder.PartEventPolling.cs` is
  counted too. Check the level per token, never per family.
- **The target list named the wrong specs.** It listed B11/B12/B13/B14, which
  already carry `{8,8}` pins AND eight-token contracts (B11 even requires
  `terminalState=Destroyed`, gating debris terminals), and omitted B5/B6/B7,
  which were vacuous. The systemic-vacuity table added in the same PR is the
  correct list; the Targets line predates it.

**Still open.** D5 `staging-debris-ttl` and D2 `proximity-cadence-bg` are
UNCOVERED and produced on every B-lane run. Their tokens exist
(`Debris TTL expired, ending recording:` `BackgroundRecorder.cs:1307`;
`Sample rate changed: pid=` `:1966`, both Info) but neither was claimed here
because neither is structurally guaranteed the way creation is:
`DebrisTTLSeconds = 60.0` is short relative to a booster's fall, which makes TTL
expiry LIKELY but not certain - a booster destroyed by reentry before the 60 s
elapses ends its recording through a different reason. Claiming on "likely" is
what the roadmap's own rule forbids. Cheapest close: grep an archived B-lane
KSP.log for both tokens, then claim in a follow-up. `B1-pad-hop` ({1,6}) and
`BDOCK-1` ({2,20}) also still carry main-recording-only floors, but neither flies
the Kerbal X so neither inherits this evidence: B1's breakup-child count is
documented as genuinely per-run variable, and BDOCK-1's window spans two trees and
is commented "never tightened". Both want their own measurement, not this one.

**2026-09-10, the archive grep this paragraph asked for (wave package A2): D5
`staging-debris-ttl` is NOT reachable on B1-pad-hop or BDOCK-1, and stays OPEN.**
`Debris TTL expired, ending recording:` appears in 0 of the 17 archived BDOCK-1 KSP.logs
and 0 of the 8 B1-pad-hop logs under `../logs`, and in only 6 of 508 archives
program-wide: GS-7 in 4 of its 10 (`2026-09-08_1457` / `_1525` / `_1631` / `_1700`,
absent from the six later GS-7 runs) and B29 in 2 of its 4 (`2026-08-27_1854` /
`_1937`). It is intermittent everywhere it appears, so the claim rule forbids it on any
of those lanes; it needs a subject where TTL expiry is structurally guaranteed (roadmap
Tier A item 5, "debris TTL / promotion sized as two lanes"). The D2 half is different:
`Sample rate changed: pid=` is in 17 of 17 archived BDOCK-1 logs (18 to 39 lines each),
so `proximity-cadence-bg` IS reachable there. The archives are ground truth for TOKENS
only; the D2 claim waits for this wave's own BDOCK-1 reading and armed run (wave ruling
G1), and the B1 / BDOCK-1 count windows wait for theirs.
Rule, unchanged: one token per claimed class, and never loosen a token to keep a
claim.

**2026-09-10, the readings (wave package A2).** B1-pad-hop read count 1 on both
`2026-09-10_1759` and `_1807` (LANDED, zero breakup-child lines), so its window is
`{1, 1}`. BDOCK-1 read 19 on `2026-09-10_1815`, attributed per type, so its min is 19
(max kept: the reading landed the fallback merge-dialog shape, see
BDOCK1-STATION-COMMIT-READOPT-LIMBO-FALLBACK-DIALOG), and the 5.0 Hz
`Sample rate changed` band (15 lines) plus the debris-creation line are now REQUIRED
there. D2 `proximity-cadence-bg` is claimed only after the armed run and its negative
controls. D5 stays OPEN as above.

**2026-09-11, armed and claimed (wave package A2).** BDOCK-1's armed run
`2026-09-10_2215` read count 19 with the 5.0 Hz token 15 times; the offline negative
control over its log and the live one, `2026-09-10_2305`, each red on exactly that token
inverted to 6.0 Hz (drift 15 / 0), so D2 `proximity-cadence-bg` is CLAIMED on BDOCK-1.
B1's armed re-flight `2026-09-10_2135` passed at count 1. Its live negative control
`2026-09-11_0252` (2026-09-11 closing round, count inverted in place to `{2,2}`) red
PARSEK-FAIL(expectation) on exactly `recordings.count 1 < min 2`, with the mission
MISSION-OK and count 1, so it is valid. D5 `staging-debris-ttl` stays OPEN.

**Recommendation (2026-09-11, see docs/dev/research/wave-0910-open-decisions-2026-09-11.md section 11):**
read before authoring.
- `staging-debris-ttl`: fly an uncommitted GS-7 variant with round 1's cut twice. Round 1's
  cut sits before the core discard, the shape that printed the token on `2026-09-08_1130`.
  - 2 of 2 TTL expiries -> author GS-10 gating the TTL-closed terminals.
  - Anything less -> record "no deterministic flight producer, covered headlessly" and stop.
- `staging-debris-promotion` comes after that. It is NOT CI-1's promotion (a TTL cancel on a
  parent-anchored Relative exit). It needs a kx opt-in phase that sets kRPC `active_vessel`
  to a dropped booster inside its 60 s TTL, about 150 lines of Python.

The token is now at `BackgroundRecorder.cs:1432` (cited as `:1307` above). Roadmap "Priority
register (2026-09-11)" item C4; no decision needed.

~~**Fix (D5, revised 2026-09-11).** The stability pair first; author GS-10 only on 2 of 2.~~

**DONE 2026-09-23 (priority register C4, branch `d5-debris`): both D5 cells CLAIMED.**
- `staging-debris-ttl`: the kx machine gained the opt-in `impactCutAtLastBoosterDrop` (round 1's
  cut as an explicit second key on `impactProfile`). Stability reading 2 of 2 on the uncommitted
  variant: `2026-09-23_1941` and `_1947`, three `Debris TTL expired` closes each. GS-10 authored
  and armed (`_1959` PASS, three closes again), negative control offline over `_1959`. The claim
  token is one pid chain from `Debris expiry set` to the same recording finalized with the same
  terminal; the terminal is a word class (`_1941` measured a booster already `Landed` at expiry).
- `staging-debris-promotion`: the opt-in `promoteDebrisVesselName` switches kRPC's active vessel
  to the nearest dropped booster and observes the recorder live again. GS-11's first flight
  `_1953` produced `decision=PromoteTrackedRecording` and `Promoted recording` 54 s inside the
  TTL; armed re-flight `_2004` PASS; negative control offline over both logs. It also produced two report-only
  findings: D5-PROMOTED-DEBRIS-TTL-NOT-CANCELLED and D5-PROMOTED-DEBRIS-MAXDIST-RELATIVE-FRAME.
- D5 is 11 of 12 (`dock-merge-same-tree` left); coverage 198 of 250.
- `dock-merge-same-tree`: CLAIMED 2026-09-24 on `SD-1-same-tree-redock` (the second-dock save's
  docked pair undocked and re-docked in one flight). Its first flight found and the same PR fixed
  TARGET-SIDE-DOCK-DROPS-SAME-TREE-PARENT. D5 is 12 of 12; coverage 202 of 250 with #1793.

**R2. Two registry cells cannot be honestly claimed as written. Decide before anyone
claims against them.**
`harness/coverage/registry.toml` D1 `stop-on-switch` described a decision that does
not exist (RESOLVED 2026-09-15, register B3: the cell is renamed
`switch-backgrounds-recording` and claimed on CI-1): `FlightRecorder.VesselSwitchDecision`
is `{None, ContinueOnEva,
ChainToVessel, DockMerge, UndockSwitch, TransitionToBackground, PromoteFromBackground}`
with no Stop member (always-tree mode removed it). D3 `surface-body-fixed` does not
name a `ReferenceFrame` member either: the enum has exactly `Absolute`, `Relative`,
`OrbitalCheckpoint`, and the only production symbol carrying that meaning is
`TrackSection.bodyFixedFrames`, a Relative-section sub-surface which therefore overlaps
`parent-anchored-debris`.
Build: delete each cell or redefine it against a real symbol, with the rationale in the
registry comment. The coverage denominator moves, so do it before the next snapshot.
The `stop-on-switch` call was owned by D1-SUB-2-POINT-DROP-UNREACHABLE-IN-TREE-MODE
(roadmap "Priority register (2026-09-11)" item B3) and was RULED YES on 2026-09-15: the
cell is renamed `switch-backgrounds-recording` and claimed on CI-1. This paragraph keeps
only the `surface-body-fixed` half.

**R3. Run S1.5 and S4.1 unattended; their operator-tier premise looks stale.**
Both are `tier = "operator"` (excluded from every cadence, never run) on the stated
rationale that the verbs are `RequiresFlight` and "run unattended from the gloops
SPACECENTER host every verb only DEFERS to a TIMEOUT"
(`S1.5-rewind-loop.toml:3-8`, `S4.1-rewind-merge.toml:3-9`). Contradicting evidence,
all measured: both use `saveTemplate = "fixtures/saves/gloops-airshow"`; that fixture
carries `activeVessel = 1` with 2 VESSEL nodes so
`TestCommandLoadGame.DecideLoadRoute` returns `Focusable`, which boots to FLIGHT;
`S1.4-injected-playback` pins `category=GhostPlayback scene=FLIGHT` and
`H5-invariants-corpus` pins `category=RecordingInvariants scene=FLIGHT` on that exact
template; S0.5 / S0.6 / EVA-1 drive `RequiresFlight` verbs to green PASSes on it; and
S1.5's other stated blocker, the TimeJump completion-decider fix on branch
`autotest-integration-fixes`, MERGED as PR #1322 (commit `eb94607dd`).
Build: `python harness/run.py --id S1.5-rewind-loop` then `--id S4.1-rewind-merge`,
operator-scheduled, not on a cadence. Do NOT relax either spec's asserts to force a
pass; re-tier only on an honest green.
Why it matters: 15 of 16 D9 cells have no live proof (7 are claimed only by these two
never-run specs; the one with live proof is `reconciliation-bundle` via H6). Two boots
either turn 7 nominal cells real plus D6 `time-jump` and D8 `epoch-isolation`, or name
the real blocker, which nobody currently has.
Known residual risk to check first: `RewindInvoker.CanInvoke`'s five preconditions
include a deep parse of the RP quicksave, and `rewind-b9` writes that quicksave
synthetically; that path has never executed live.

**R4. Drive the D1 finalization family. Five specs, five boots, no code.**
`IncompleteBallistic` (8 tests), `FinalizeBackfill` (7), `RecordingFinalization` (3),
`FinalizeLimbo` (2), `Bug289` (2) are all FLIGHT-scene, all `AllowBatchExecution`
default-true, and nothing drives any of them. Closes D1 `scene-exit-finalization`,
`ballistic-extrapolation`, `finalization-cache`.
Build: five specs on the `harness/scenarios/H5-invariants-corpus.toml` template over
`gloops-airshow`. One category per spec is REQUIRED, not a choice:
`hlib.SINGLE_BATCH_SELECTOR_RULE` (`hlib.py:691`, enforced at `:2158-2190`) rejects a
second `RunTests` step and rejects a multi-category selector, for
`driver.autorun.tests` as well as `driver.steps`.
Proof: pin the WHOLE `BATCH_COMPLETE` tally from the first green run, never
`passed=[1-9][0-9]*`. These categories self-skip on fixture conditions and a
`failed=0` pin over an all-skipped batch is the vacuity defect that was already found
and closed once. Each spec must say in prose that it gates the DECISION layer in a
live KSP process, not a flown situation (the `M1-mission-loop-unit` precedent).
Independent of R3 and R5; buildable in parallel with both.

**~~R5. `RunTests` cannot reach 68 already-written tests. Add an `isolated` argument.~~**
DONE 2026-07-27. This was the largest single unlock in the roadmap. `InGameTestRunner` has a second
batch entry point, `PrepareBatchExecutionIncludingFlightRestore`, which also admits
`test.RestoreBatchFlightBaselineAfterExecution` and restores a flight baseline after
each test. `RunAllIncludingFlightRestore` / `RunCategoryIncludingFlightRestore` are
public and fully implemented (`InGameTestRunner.cs:389,420`) and are called from
exactly two INTERACTIVE places: `TestRunnerShortcut.cs:395,463` (the Ctrl+Shift+T
window) and `UI/TestRunnerUI.cs:249,375`. Neither unattended path calls them: the
seam's `RunTests` (`ParsekTestCommandAddon.cs:1494,1496`) and the autorun dispatcher
(`TestRunnerShortcut.cs:725,739,789`) both call only `RunAll()` / `RunCategory(cat)`.
Measured over `[InGameTest(...)]` argument lists: 68 tests carry
`AllowBatchExecution = false` AND `RestoreBatchFlightBaselineAfterExecution = true`,
i.e. their authors already decided a quickload-baseline restore makes them batch-safe.
By category: Logistics 38, AutoRecord 10, Rewind 6, Coalescer 2, MergeDialog 2,
QuickloadResume 2, SceneExitMerge 2, LogisticsGrapple 1, MapRender 1, TrackingStation
1, GhostPlayback 1, RevertFlow 1, PlaybackControl 1.
Twenty-six of those sit in D1-D9 categories nothing drives, and they are the ONLY
producer of D1 `auto-record-first-mod-switch` / `commit-scene-exit` /
`commit-revert-merge`, D5 `controlled-decoupled-child` / `crash-coalescing`, and D9
`rewind-to-launch`. No fixture, no mission profile and no existing verb produces them.
~~Build: `RunTests` gains an `isolated` arg routing to `RunCategoryIncludingFlightRestore`;
mirror it in the autorun selector; add the hlib spec-validation companion; land one
shakedown spec.~~ All four shipped. The autorun mirror is a separate env var,
`PARSEK_AUTORUN_ISOLATED=1`, NOT a selector prefix: the selector string is consumed
verbatim as the `category=` token both the runner stamps and
`hlib._batch_probe_categories` builds its anti-vacuity probe family from, so a prefix
would desynchronize those two copies of one name, every probe would miss on a token
mismatch, and a contract that rejects all probes is what the gate reads as SAFE. Full
argument in `design-autotest-autorun-hooks.md` "H1 - Isolated batches".

Proof, CORRECTED: the shakedown spec `H21-scene-exit-merge-isolated` pins
`total=2 passed=2 failed=0 skipped=0 category=SceneExitMerge scene=FLIGHT`. The
non-isolated form does NOT yield `total=0` as this entry originally claimed -
`PrepareBatchExecution` sets `Status = Skipped` on the tests it filters rather than
dropping them, and `BATCH_COMPLETE`'s `total` is `allTests.Count(Status != NotRun)`, so
it yields `total=2 passed=0 failed=0 skipped=2`. `total` is therefore identical on both
paths and the discriminator is the passed/skipped split. That makes the proof stronger,
not weaker: `passed=0, failed=0, total==skipped` is precisely the one-parameter vacuity
family the anti-vacuity gate enumerates, so the gate already guarantees the pin rejects
the non-isolated line, and `IsolatedBatchWiringGroupTests` asserts that rejection
explicitly rather than inheriting it.

Two things the build turned up that this entry did not anticipate:

- The hlib companion was LOAD-BEARING, not a nicety. `InGameTestDecl` did not carry
  `RestoreBatchFlightBaselineAfterExecution` at all and `derive_batch_tally` hardcoded
  the ordinary filter, so `CommittedBatchTallySourceSyncTests` would have REJECTED a
  correct isolated pin, deriving `executable = 0`.
- The FIXTURE was the expensive trap. Both `SceneExitMerge` cells stage the active
  vessel and wait for it to leave PRELAUNCH and clear 80 m on a 30 s deadline. The
  H7-H20 fixture `gloops-airshow` has a 1-part `mk1-capsule` with ZERO `ModuleEngines`,
  so on it both self-skip and print `total=2 passed=0 failed=0 skipped=2` - the SAME
  line the non-isolated failure produces. The spec uses `b2-lko-craft` (73-part stock
  launcher, 8 engines, PRELAUNCH) and a new gate asserts the PRELAUNCH + non-zero-engine
  property statically.

FLOWN: H21 PASSED on attempt 1, 2026-07-27, 101 s wall (29.6 s of it the batch),
matching its pinned tally token for token. Both questions the derivation could not
answer came back favourable: the launcher clears 80 m inside the 30 s deadline, and
the post-test baseline quickload returns the vessel to FLIGHT in PRELAUNCH so test A's
situation guard does not fire. Coverage 96 -> 97 covered, the new cell being D1
`commit-scene-exit`.

REMAINING (follow-on, not R5): the other 12 unlocked categories are now ordinary
spec-authoring work. `AutoRecord` (10) is the one to size carefully - ten
launch-and-restore cycles in one boot - but H21 measured a restore cycle at well
under 15 s, so the earlier fear of a 10-test isolated batch being unaffordable looks
overstated.

**R6-R8. Drive the reachable in-game categories.** 539 `[InGameTest]` declarations
exist in 97 categories; specs drive 8 categories / 125 declarations; 414 declarations
in 89 categories run only when a human presses Ctrl+Shift+T. 82 of those 89 categories
are fully reachable today on existing fixtures (FLIGHT / SPACECENTER / scene-agnostic
only); 7 involve TRACKSTATION or MAINMENU and have no seam route.
Ordering and per-category cell mapping are in the roadmap doc. The cheapest whole
dimension is D13 (11 of 11 uncovered, NOT capability-blocked): 29 tests already exist
and self-site off `FlightGlobals.ActiveVessel` - 26 `Scene = FLIGHT` plus 3
scene-agnostic (`SpawnHealth`), so all 29 run in a FLIGHT batch (`SpawnRotation` 10,
`TerrainClearance` 6, `SpawnHealth` 3, `SpawnTerminalOrbit` 3, `SpawnCollision` 2,
`Spawner` 2, `EvaSpawnPosition` 2, `Pipeline-Terrain` 1), and `gloops-airshow` routes
to FLIGHT.
Correction to carry: `Logistics` is 47 tests but 38 carry
`AllowBatchExecution = false`, so only 9 are batch-reachable before R5 lands.

**R9-R14. Machinery items** (each self-contained in the roadmap doc). TWO OF THE SIX
ARE NOW CLOSED; the R9-R14 bullets below are kept intact with the closures marked,
because the counts around them were measured against the six-item shape. One
NON-R bullet (the CL-2 stage-B calibration reading) is appended after R9 as
supporting measurement for R9's remaining scope - it is not a seventh machinery
item and must not be counted as one:

- **R9** structural save-content expectations plus landing the three inert `route` /
  `rewind` / `loop` expectation blocks - **HARNESS HALF SHIPPED-REPORT-ONLY
  2026-07-31** (branch `claude/r9-save-parse-verifier-tshhzv`), then **ARMED ON S4.1
  THE SAME DAY** (branch `r9-arm-s41`): the M-C2 save-parse verifier
  (`harness/lib/saveparse.py` + the `saveParse` chain row) evaluates
  `[expectations.rewind]` and the new `[expectations.recordings.structure]` block
  over the produced save's ParsekScenario surfaces. The gating PROMOTION is DONE for
  S4.1 - it is the first and only committed spec carrying `gating = true`, and
  `test_no_committed_spec_arms_gating` became an explicit allowlist pinning exactly
  `{"S4.1-rewind-merge.toml"}` so a second spec arming still needs a deliberate edit.
  Three live runs did it: `2026-07-31_1628` (report-only reading, PASS, all readings
  inside the declared `max = 0` windows), `2026-07-31_1635` (armed, PASS,
  `armedBlocks=["rewind"]`), and a NEGATIVE CONTROL that temporarily flipped
  `supersedeRows` to `min = 1` and reddened `2026-07-31_1637`
  `PARSEK-FAIL(save-structure)` with `mismatches=["rewind.supersedeRows 0 < min 1"]`
  - the gate is seen to fail, not merely assumed to work. STILL OPEN inside R9: CL-2
  stage B's windows (calibration numbers recorded below), `route`/`loop` (still
  reserved - zero declarers), and the analyzer-PR half. See the roadmap R9 entry.

  ANSWERED BY THE READING RUN: the merge REAPS `rp_b9_root`. `rewindPoints` measured
  0 on all three runs, and the produced save carries no `REWIND_POINTS` node at all
  (the empty-staging-list-writes-no-parent quirk). This was a genuine open question -
  it is recorded, deliberately NOT pinned as a window: one observation is not a
  window, and a reap count wants a second scenario before it gates.

- **CL-2 stage-B calibration, measured off the STAGE-A run** (CL-2 itself IS stage A;
  stage B is unwritten - it was written 2026-08-03 as `CL-3-refly-crew-tombstone`; see the roadmap's Stage B bullet) [MEASURED 2026-07-31, run
  `2026-07-31_1641_CL-2-pod-impact-ledger` (PASS, 168 s)]. CL-2 declares NO M-C2
  block (`blocks: []`), so its `saveParse` row is pure measurement - which is exactly
  what makes it usable to size stage B's windows. The observed block, verbatim:

  ```
  "rewind":   { "supersedeRows": 0, "tombstones": 0,
                "rewindPoints": 0, "rewindRetirements": 0 }
  "recordings": { "structure": {
      "trees": 1, "committedTrees": 1, "recordings": 1,
      "terminalStates": { "Destroyed": 1 },
      "branchPoints": {}, "duplicateRecordingIds": [] } }
  ```

  READ THESE CORRECTLY. This is stage A - the fatal pod hop and its ledger rows, with
  NO rewind anywhere in the run. So the `structure` numbers are the PRE-REWIND corpus
  baseline stage B starts from (one committed tree, one Destroyed recording), and the
  `rewind` numbers are all zero because nothing rewound. Stage B rewinds ACROSS
  CL-1's crew loss, so its `[expectations.rewind]` is expected to move to
  `supersedeRows >= 1` / `tombstones >= 1` and its `structure` counts to grow by the
  re-fly fork. Pinning stage B's windows straight off this block would assert the
  absence of the very thing stage B exists to exercise: author stage B report-only
  first, read ITS facets, then arm - the same three-run promotion S4.1 just went
  through. Stage B scope: the R12 residue block in `docs/dev/autotest-roadmap.md`
  (closed by CL-3 for the two tombstone cells; `stand-ins` by CL-4 on 2026-09-09;
  `tombstone-rep-penalty` was a product change, SHIPPED 2026-09-10 on branch
  `tombstone-rep-penalty` and claimed by CL-4 the same day).
- **R10** runtime-handle plumbing so a live tree / vessel / route id can reach a verb -
  ~~OPEN (`run.py:1157` substitutes exactly one token, `${runSave}`, and no response
  payload is ever captured)~~ **CLOSED 2026-09-08** (PR #1653, branch
  `r10-runtime-handles`) by three pieces: the harness captures a seam reply's payload
  fields into a per-run store and substitutes `${<label-or-stepId>.<field>}` into later
  step args and into `[driver.missionParams]` (static faults `INVALID(spec-invalid)`
  pre-launch, a runtime miss the new NON-retryable `INVALID(driver-unresolved-handle)`
  at the step - the literal token never reaches the wire); `mlib.seam_handle_from_payload`
  plus the R1 machine's `R1_RESOLVE` phase compute the same thing inside a live flight;
  and the new `ListHandles` seam verb (31 -> 32 implemented, reserved unchanged at 5;
  required closed `kind=` in `rewindpoints|committed|active`, capped payloads with
  `count` + `truncated`) is the enumeration those references read. First consumer
  `RH-1-live-rp-handle-rewind` - `ListHandles kind=rewindpoints` labelled `handles`, then
  `InvokeRewind rp=${handles.rp0} slot=1`. The NOTE below still stands as history: R12
  solved the SPECIFIC instance that blocked it worst without solving R10 -
  `SimulateStockSwitchClick` takes `vessel=` (a stable NAME) precisely because a TOML
  author cannot know the pid a launch will mint, the same stable-addressing dodge
  `InvokeRewind` used; that was a per-verb workaround, and the general mechanism now
  exists (a pid-addressed switch reads `${<step>.bg<i>pid}` /
  `${<step>.rec<i>spawnedPid}`). Contracts: `design-autotest-harness-core.md` ->
  "Runtime handles: payload capture and `${step.field}` substitution (R10)",
  `design-autotest-command-seam.md` -> `#### ListHandles`.
- **R11** a CAREER fixture with a flyable craft - ~~`FORGE-career-pad`; all three
  career-family fixtures currently have ZERO VESSEL nodes~~ **CLOSED 2026-07-28** by
  `harness/fixtures/saves/career-pad-craft`, built by construction rather than by a
  forge flight.
- **R12** `SimulateStockSwitchClick` plus a `scene=` argument on `LoadGame` -
  **SHIPPED 2026-07-30**, and it grew a third capability on the way:
  `ExitToSpaceCenter`, the live FLIGHT -> SPACECENTER transition, without which
  nothing could LEAVE flight either. Live-proven by `H23-tracking-station`,
  `S0.7-exit-auto-commit` and `S0.8-switch-click-segment`. The seam is 21 implemented
  verbs / 10 reserved. What it did NOT close is listed in the roadmap's R12 block -
  `site=ts` / `site=ksc`, the dialog cases, and unloaded targets. The CL-1 spec
  extension's stage A shipped 2026-07-30 as `CL-2-pod-impact-ledger`; its tombstone
  stage B ~~remains~~ SHIPPED 2026-08-03 as `CL-3-refly-crew-tombstone` (D9
  `tombstones` and D12 `dead-crew-strip` armed; `stand-ins` followed on
  `CL-4-refly-crew-standin`, 2026-09-09; `tombstone-rep-penalty` was a product change,
  shipped 2026-09-10 and claimed by CL-4).
- **R13** widening `SINGLE_BATCH_SELECTOR_RULE` to N categories with N pinned
  tallies - OPEN.
- **R14** provisioning `modded-compat` for D17 - ~~OPEN~~ **CLOSED 2026-08-04**
  (branch `modded-compat-lane`): `automation/modded-compat` provisioned live
  (exit=0, VERIFY drift=0; the profile gained the missing audio-silencing
  settings deltas on the first run's inspection, now pinned by
  `RealProfileFileTests.test_both_profiles_pin_the_unattended_settings`), and
  `MC-1-waterfall-compat` + `MC-2-restock-compat` flew the WaterfallCompat /
  ReStockCompat categories green there on attempt 1 (7/8 and 8/9 executed; the
  2 skips are the by-design inverse gates). D17 `waterfall-swe-fallback` +
  `restock` and D7 `engine-fx-waterfall-fallback` claimed. RESIDUE:
  `persistent-rotation` and `remotetech-commnet` stay source-blocked (GT-8 /
  not in the profile); `better-time-warp` and `making-history` have the
  instance but no committed spec (2026-09-10: `better-time-warp` now has a
  reading lane, `MC-3-better-time-warp`, READ green on outcome (A) on
  `2026-09-10_2025` and pinned from it, armed re-flight `_2208` PASS and negative control
  `_2213` valid, D17 `better-time-warp` CLAIMED 2026-09-11; `making-history`
  was definition-blocked and is DEFINED 2026-09-15 as alt-site launch capture on
  stock-minimal, still unflown - see D17-MAKING-HISTORY-NEEDS-A-DEFINITION); the FX-fingerprint A/B diff ran REPORT-ONLY
  and surfaced a corpus limitation filed as **T48 under TODO — Compatibility**
  (the synthetic corpus is trajectory-only for all but a handful of
  recordings, so a save-based A/B exercises ~1 engine key; a dedicated
  engine-showcase fixture with real vessel snapshots is the follow-up).

**Baseline caveat: three of these are already in flight.** Every count above was
measured at `1591aa59f` and EXCLUDES work open in review at the time of writing. PR
#1358 (`ingame-test-wiring`) wires 14 in-game categories as H7-H20, covering R4's
`IncompleteBallistic` / `FinalizeBackfill` / `RecordingFinalization`, R6's
`TrajectoryMath` / `Pipeline-Anchor` / `SwitchSegment` and R8's `SpawnRotation` /
`EvaSpawnPosition`, and moves the scenario count 38 -> 52; PR #1357
(`rewind-loop-lane`) re-tiers S1.5 and S4.1 to `nightly` on the same premise R3
argues; PR #1359 (`eva4-failopen`) fixes the EVA-4 fail-open. Re-measure with
`hlib.compute_coverage` / `hlib.parse_ingame_test_declarations` before acting on R3,
R4, R6 or R8 - do not treat a merged H7-H20 category as still-undriven work.

**Doc hygiene found while measuring, deliberately NOT edited (concurrent sessions own
those files).**
1. `docs/dev/autotest-status.md` contradicts itself on EVA-2. The EVA table row says
   "STILL pending-fixture: `eva2-lko-crewed` does not exist yet" while the section
   header above it says all four EVA scenarios are LIVE-PROVEN, Operator item 2 says
   both EVA fixtures were forged headlessly and committed, the fixture exists at
   `harness/fixtures/saves/eva2-lko-crewed/` with 7 VESSEL nodes, the spec reads
   `tier = "daily"`, and `harness/coverage/duration.json` carries a measured 57 s run.
   Fix: correct the two stale rows to match the rest of the file. ~~RESOLVED~~ - verified
   2026-09-07: the contradicting row no longer exists in `autotest-status.md`.
2. `harness/fixtures/saves/bdock-station-craft/` is an orphan: no spec LOADS it (no
   `saveTemplate` points at it). It IS named in a provenance comment at
   `BDOCK-1-station-interceptor.toml:97`, whose own `saveTemplate` is
   `bdock-station-pad`, and by `harness/tools/harvest_bdock_station.py` plus the
   design doc. Decide keep or delete; if delete, drop that comment reference with it.
   (2026-09-07: `harness/lib/test_saveparse.py` and `harness/fixtures/shared-ships.toml`
   now enumerate it too, so a delete edits both; still no `saveTemplate` loads it.
   Operator call still open; default KEEP.) DECIDED 2026-09-08: KEEP - it is the
   clean operator-build base `bdock-station-pad` was stamped from; no change needed.
3. ~~`S1.5-rewind-loop.toml:3-8` and `S4.1-rewind-merge.toml:3-9` state a "gloops
   SPACECENTER host" premise that the `LoadRoute` contract contradicts. Correct the
   comment or replace it with an R3 measurement.~~ RESOLVED: both headers were rewritten
   2026-07-26 (re-tiered `nightly`) and R3 closed by flight 2026-07-29.

---

## Dev - Logistics in-game tests: auto-spawn unloaded vessel (no manual second craft)

The 7 logistics FLIGHT in-game tests (origin-debit / pickup / multi-stop delivery,
`InGameTests/Logistics*RuntimeTests.cs`) need a live FLIGHT active vessel. The
LOADED-path tests use the ActiveVessel directly - a fueled PRELAUNCH pad rocket
satisfies them after `WaitForActiveVesselUnpack` (they check `loaded && !packed` +
an LF tank; no test rejects PRELAUNCH on `vessel.situation`, so no relaxation was
needed). The UNLOADED-path tests need a SEPARATE on-rails (unloaded) vessel with
LiquidFuel and used to SKIP whenever the save had none, forcing the player to
hand-place a second vessel.

Maintainer-chosen design: "use my pad rocket + auto-spawn the rest". New shared
fixture `InGameTests/Helpers/UnloadedFuelVesselFixture.cs`:
`EnsureUnloadedLiquidFuelVessel(minStoredLf, minFreeCapacity, result)` (coroutine)
(a) reuses any suitable pre-existing unloaded vessel (fast path, behavior-identical
for saves that already have one); else (b) snapshots the ActiveVessel via
`VesselSpawner.TryBackupSnapshot`, rewrites its LiquidFuel RESOURCE amounts via the
pure `AdjustSnapshotLiquidFuel` (>= minStoredLf stored, >= minFreeCapacity free,
flowState forced True) and spawns a FRESH-identity copy (preserveIdentity:false ->
regenerated pid, no collision) into a high (~250 km) parking ORBIT far from the
active vessel via `VesselSpawner.SpawnAtPosition(..., orbitOverride)` so KSP keeps
it on-rails / unloaded; (c) waits a bounded number of frames for the spawn to
register in `FlightGlobals.Vessels` AND settle unloaded, resolving by the returned
pid; (d) on any failure leaves `result.Vessel == null` so the caller falls back to
the existing `InGameAssert.Skip` (never worse than before). Cleanup: a SPAWNED
vessel is removed via `Vessel.Die()` + protoVessels drop in the test's finally
(`UnloadedFuelVesselFixture.Cleanup`); the batch baseline restore is the backstop.
Rewired tests: `OriginDebit_UnloadedOriginVessel_WritesProtoSnapshot`,
`OriginDebit_UnloadedDebit_SurvivesKspSaveRoundTrip`,
`MultiStop_UnloadedEndpoint_DeliversAtBothDocks`,
`PickupDebit_UnloadedEndpointVessel_WritesProtoSnapshot` (the per-suite
`TryFindUnloaded*` finders were folded into the fixture). The
inventory-pickup tests are unchanged (no unloaded variant; an unloaded inventory
fixture would need a stored cargo part the pad rocket may lack). Pure piece unit-
tested in `Source/Parsek.Tests/UnloadedFuelVesselFixtureTests.cs`. Test-infra only
(no user-facing CHANGELOG line).

**LIVE validation DONE (2026-07-10 sweep, `logs/2026-07-10_1935_ingame-test-sweep`):**
the full FLIGHT Run All + Isolated pass ran every rewired Logistics test green except
`Escrow_CompetingRouteSeesReservation_Holds`, which failed on a STALE ASSERTION, not
production: the test still pinned the pre-M6 physical hold token prefix (`source:`)
while the gate correctly emits the M6 escrow-legibility token
(`source-reserved:<pid>:<name>:<resource>:<reservingRoute>`, PR #1233 - the test was
written on the M4b branch in parallel and never updated). Fixed on branch
`fix-escrow-ingame-token`: the assertion now pins the M6 escrow contract (the
`source-reserved:` prefix - this scenario is escrow-caused by construction - plus the
pid and the reserving route A as the final token segment, with the two test route ids
given prefixes distinct within `RouteIds.Short`'s 8 chars so the pin can tell A from
B; both previously truncated to the same `ingame-e`). Test-only; re-verify with one
isolated re-run of the test in FLIGHT.

## TODO - Missions feature completion milestones (M-MIS roadmap; investigated 2026-06-10)

The single ordered list of what remains to call the Missions feature (`docs/parsek-missions-design.md`, shipped core) COMPLETE. Ordered by necessity / priority: each milestone was code-investigated on 2026-06-10 (implemented-already? viable? what exactly remains?) and the findings are recorded inline. Detailed history for the completed milestones lives in the `done/todo-and-known-bugs-v7.md` archive (cross-referenced); this list is the planning surface.

### Reuse mandate (applies to every solver-flavored milestone below)

Do NOT re-implement intercept / window math from scratch. The 2026-05-28 prior-art survey (recorded in `docs/dev/done/plans/reaim-interplanetary-transfers.md` + the prior-art note in the phase-lock entry below) already settled the sourcing:

- **`Reaim/UvLambert.cs`** is OUR owned, unit-tested (Curtis Algorithm 5.2) full-3D universal-variables Lambert solver. Extend it; do not replace it.
- **`Reaim/ITransferSolver.cs`** is the deliberate swap seam. The sanctioned fallback if UvLambert robustness proves insufficient (multi-rev, near-180-deg singularity) is porting **MechJebLib's Gooding solver** (permissive license: public domain / Unlicense; ~577 lines + `V3`/`Statics` deps) behind that seam - a port, not a rewrite.
- **`Reaim/TransferWindowMath.cs`** already carries the KerbalAlarmClock-derived (MIT, attributed) phase-angle + synodic math. TransferWindowPlanner2's porkchop grid was evaluated and deliberately NOT needed (the congruent-window model uses recorded tof + synodic spacing). KSTS / Principia: surveyed, not applicable.
- The launch-side zero-drift near-coincidence primitive (`MissionPeriodicity.NextJointNearCoincidenceUT` / `TryBuildRelaunchSchedule`) and `Reaim/DestinationArrivalSolver.SolveArrivalWindow` (WIRED since the M-MIS-4 post-M4c follow-up, branch `mmis4-solve-arrival-window`: hold-aware sampling + the joint-hold lattice feasibility scan, consumed by `ArrivalHoldPlanner.ComputeJointArrivalHold` for the D8 landing+station dual) are the in-repo multi-constraint window search. New milestones REUSE these, never re-derive them.

### M-MIS-6 - Multi-moon destinations: the looped "Jool-5" mission, window-alignment cut [BUILT, needs the in-game looped Jool playtest]

- **Investigated 2026-06-10 (answers the open uncertainty):** today a Jool-5 recording loops on the FAITHFUL path only: `ReaimClassifier` supports the Kerbin->Jool transfer (Jool is a direct Sun child) but `DestinationConstraintExtractor` fails closed at 2+ SOI-entered moons, so nothing aligns the moons; each moon-relative block self-anchors to the LIVE moon while the Jool-centric inter-moon arcs replay inertially, so every encounter seam renders disconnected (the Mun-desync mechanism, once per moon). What makes it tractable WITHOUT new math: (a) all encounters shift TOGETHER under one arrival hold, so alignment needs the moons' joint CONFIGURATION to recur, not each moon independently; (b) stock Laythe:Vall:Tylo are a near-exact 1:2:4 resonance (period ratios off by ~1e-5 from rounded SMAs), so the inner-three configuration recurs every Tylo period (~211,926 s) to well within SOI tolerance - a per-loop hold in `[0, T_config)` aligns an inner-three tour exactly like the shipped `W_N` destination-rotation hold (substitute T_config for T_rot); (c) the stock major moons are tidally locked, so landing-rotation constraints collapse into orbital phase (the tidal-lock collapse `MissionPeriodicity` already implements); (d) Bop/Pol are incommensurate with the inner three - a full 5-moon tight alignment is effectively non-recurring, so those legs get Loose tolerance via the near-coincidence search or the mission fails closed to faithful (a VALID outcome, surfaced in the UI, never silent).
- **Requirements:** (1) ~~short design note first~~ DONE - `docs/dev/design-mission-multimoon-alignment.md` (decisions D1-D8; the "2+-moon mini star systems" deferred item, `docs/parsek-missions-design.md` sect. 14.4); (2) ~~REUSE the SolveArrivalWindow wiring + generalize the per-loop hold~~ DONE; (3) ~~failing synthetic multi-moon test BEFORE any knob math~~ DONE (11 fixtures verified failing pre-implementation); (4) intra-SOI re-aim (per-leg Lambert re-solves inside the destination system) is explicitly the SECOND cut, tracked as M-MIS-7 - only justified if this hold-based model proves insufficient in playtest.
- **BUILT (branch `claude/mmis6-multi-moon-window-7fcpyh`, stacked on `mmis4-solve-arrival-window`; design `docs/dev/design-mission-multimoon-alignment.md`):** `DestinationConstraintExtractor` now EMITS the 2+-moon set (Supported, all MoonConfigs in `Constraints`, constrained-moon landing rotations in the new `MoonRotations` field; the `MaxConstrainedMoons` reject + constant are retired, and station-bearing Jool-class shapes fall to the station+moon reject). `ArrivalHoldPlanner.ComputeMultiMoonConfigHold` owns the shape: participants = moon Orbitals (SOI tolerance, never dropped) + moon/target rotations (mode ladder; Drop removes them; a tidally locked moon's rotation collapses into its orbital period for free), T_config = k*P_anchor via `MissionPeriodicity.TryFindNextScheduleK` with the smallest-duty anchor (`SelectAnchorConstraintIndex` rationale - Vall for stock, k=2, T_config ~= T_Tylo ~= 211,924s), slack-clamped anchor budget (64), engage double-gated on the scan + the hold-aware `SolveArrivalWindow` window-1 pick (the M-MIS-4 wiring, `holdAlignPeriodSeconds = T_config`, `maxWholeHoldPeriods = 0`). The clock is UNCHANGED: the config hold rides the shipped single-period per-loop path via `LoopUnit.ArrivalAlignPeriodSeconds = T_config` (no new LoopUnit/persisted fields). HONEST FINITE HORIZON (the design's correction to the investigation's recurrence claim): the resonance drifts ~0.6s/2.2s per T_config on the Vall-anchored lattice, so alignment holds for ~40 consecutive synodic windows under Loose (a Tylo-anchored lattice would give only ~8 - why the anchor is duty-selected), then leaves tolerance for centuries; the count is computed (`DestinationArrivalSolver.CountAlignedWindowPrefix`, reporting-only) and logged in the `ARRIVAL HOLD kind=config` line (`alignedWindows=`). EVERY decline ambers (never silent - the old silent no-station Jool-class None is gone): non-recurring configs (Bop/Pol, non-locked moon rotations, Jool-landing rotation under Loose/Tight), slack-starved holds, destination-side loiter cuts (L8), degenerate window spacing. `DestinationLoiterTrim` gained the `ConstrainedMoonCount >= 2` exclusion (the rotation-only trim would misalign the configuration). Tests: `MultiMoonAlignmentTests` (stock-value synthetic Jool system; engage + per-loop all-encounters-within-SOI sweep + amber polarity + byte-identity pins) + `Build_ReaimJoolMultiMoonTour_EngagesConfigHold` (builder E2E) + 3 revised pre-M-MIS-6 pins (extractor emission, station+moon reason ownership, never-silent decline).
- **MERGE GATE - AUTOMATED (2026-07-08):** `JoolConfigHoldInGameTest` (in-game, Category "Missions", SPACECENTER, batch-safe) is the merge gate. It drives the REAL `ArrivalHoldPlanner.ComputeArrivalHold` (through the REAL `DestinationConstraintExtractor` + `DestinationArrivalSolver` + `MissionPeriodicity` chain) against the LIVE Jool body graph via `FlightGlobalsBodyInfo.Instance` - which is exactly what headless could not do (the `MultiMoonAlignmentTests` xUnit fixtures pin the stock periods/SOI/velocities as constants; only an in-game run proves the SHIPPED ephemerides lock 1:2:4 and engage). Test A: the resonant inner three (Laythe/Vall/Tylo, live periods) engages the config hold, T_config is a whole multiple of the live anchor period and lands within one live Tylo period, and the single-period per-loop hold re-aligns every moon encounter within its live SOI tolerance across the horizon. Test B: adding live incommensurate Bop fails the whole set closed to faithful with an amber naming the shape. Skips cleanly on a non-stock pack / rescaled resonance (probes the live 1:2:4 lock first). Runbook: one Ctrl+Shift+T Run All in any stock save.
- **M-MIS-7 go/no-go:** ~~observational evidence from a real looped Jool tour remains wanted~~ **PARTIALLY IN, 2026-08-20.** A driven lane (`V17M`, run `2026-08-20_1841`) supplied the faithful-outcome half on a MOON-TO-MOON (not multi-moon-tour) subject: the re-aim classifier defers the shape and phase-lock declines cross-parent, so the loop replays faithful on the raw cadence - see the MEASURED block under M-MIS-7 below. What is still wanted from normal play is the OTHER half: a multi-moon tour whose encounter seams render connected across aligned windows. The measured half is evidence for M-MIS-7's *(a)* consumer only, and it declines rather than engages.
- **Viability:** ~~moderate~~ built - the resonant-inner-three + tidally-locked case maps onto shipped primitives; the general (Bop/Pol, non-resonant packs) case intentionally fails closed with amber (the design records the align-the-resonant-subset alternative as deferred to M-MIS-7 evidence).

### M-MIS-7 - Intra-SOI re-aim and multi-hop targets (Jool-like systems second cut; Ike-class targets) [GATED: on M-MIS-6 playtest evidence. **THE go/no-go OBSERVATION IS IN, MEASURED 2026-08-20 - see MEASURED below**]

- **MEASURED 2026-08-20 (`V17M-laythe-vall-player-loop`, run `2026-08-20_1841`) - THE ANSWER FOR TODAY'S CODE:** **Parsek neither RE-AIMS nor PHASE-LOCKS a moon-to-moon hop; it replays it faithfully on the raw cadence.** This is the first time the question has been asked of a real cross-parent subject (`fixtures/saves/vall-transfer-recorded`, produced by `B26-laythe-vall-transfer` flight 3), and the answer is a THIRD outcome neither pre-registered hypothesis named. The classifier WAS reached - the single-hop guard did NOT fire - and it declined on the PARKING-ORBIT / MID-COURSE structural check, `Source/Parsek/Reaim/ReaimClassifier.cs:270-277`: `[ReaimDiag] member#0 segs=13 startBody=Laythe supported=False reason='transfer departs from a heliocentric parking orbit or mid-course correction (deferred); staying faithful'`. Phase-lock declined too (`PhaseLock SKIPPED ... support=UnsupportedCrossParent`), and the render side agreed (`factory chain ... reaimed=False faithfulFallback=False`).
  - **THE DECLINE IS THE FLIGHT PROFILE'S, NOT THE MOON PAIR'S.** The only way the harness can fly a moon-to-moon hop today is the parent-relay mode, which is TWO-BURN by construction (escape, coast, then plan the transfer from the parent frame). That middle coast is a real parent-frame orbit sitting immediately before the transfer run on a different orbit, so `sunPredecessor` is true. Note the reason string says "heliocentric" but the code tests `bodyName == commonAncestor` - the check is frame-generic and here the common ancestor is JOOL.
  - **AND THE RE-ADMITTING EXCEPTION MISSED BY ONE CONJUNCT OF THREE**, which makes this a DURATION result rather than a structural one. `IsHeliocentricParkingDeparture`: near-circular PASSED (ecc 0.0126 <= 0.1), co-orbital with the launch body PASSED (4.15% <= 10%), closed-park FAILED - the coast is 43,183.50 s against a 49,717.82 s period = **0.8686 revolutions**, so `wholeRevs = 0`, `ReaimLoiterCompressor.DetectRuns` emits no run, and the `!found` early return fires. **6,534 s - 15.13% - short of one revolution.**
  - **WHAT REMAINS UNTESTED, HONESTLY:** the SUPPORTED path needs a DIRECT single-burn ejection that leaves the moon already on the sibling transfer, and **no currently flyable profile produces that shape** - MechJeb's `OperationInterplanetaryTransfer` plans it but refuses a moon-parked origin (MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN above), and the parent-relay mode that can fly the hop is two-burn by construction. So consumer (a) below is measured-declined for the shapes we can produce, and consumer (b) (the single-hop guard) was never even reached. A profile that let the post-escape coast close one full revolution would take the `DepartedFromHeliocentricPark` path instead - a DIFFERENT code path answering a DIFFERENT question, recorded as an observation and not as a plan.
  - Full derivation: `docs/dev/research/same-parent-reaim-jool-system.md` section 11.3, and `V17M`'s spec header section 0.
- **What it is:** the recursive "mini star system" model - re-solving transfer legs INSIDE a destination system instead of only the heliocentric leg. Two consumers: (a) **moon-to-moon legs of a multi-moon tour** when the M-MIS-6 hold-based joint-configuration model is insufficient (non-resonant moon packs, Bop/Pol legs, long inter-moon loiters): per-leg Lambert re-solves in the gas giant's frame + per-leg holds at each moon-SOI seam; (b) **multi-hop TARGETS** - a target that is not a direct child of the common ancestor (Ike via Duna; rejected today by the `ReaimClassifier` single-hop guard, ReaimClassifier.cs:124-130): re-aim the heliocentric leg to the parent, then the in-SOI hop to the moon is the same intra-SOI machinery.
- **Requirements:** REUSE everything - `UvLambert` is body-agnostic (mu is a parameter), so the same `ITransferSolver` seam serves Jool-centric solves; the per-loop hold clock primitives generalize per leg. This is a genuine new subsystem (per-leg seams, recursive window scheduling): budget a full design note + the failing-test-first discipline, and do NOT build it speculatively - M-MIS-6's playtest decides whether it is needed at all.
- **Viability:** hard; deliberately last among the solver milestones.

### M-MIS-10 - Scenario verification sweep: believed-supported archetypes, never explicitly verified [not sequenced - run incrementally alongside any milestone]

A 2026-06-10 online sweep of what KSP players actually fly (stock career contract types: satellite/relay, rescue, tourism, asteroid redirect, station resupply / crew rotation; the classic community challenges: Jool 5, Elcano, K-Prize, grand tours, Eve return; and the automation prior art Parsek overlaps with: Routine Mission Manager, KSTS, FMRS, MKS supply chains) found NO missing alignment subsystem beyond M-MIS-1..9 - but it surfaced a set of archetypes the recorder/missions stack should support TODAY that have no explicit test or playtest. Each needs a cheap in-game verify (file a todo entry where it breaks):

- **Constellation deployment** (resonant-orbit carrier releasing N relay sats, the CommNet career staple): an N-fork controlled-decouple tree where every branch ends in a perpetual-orbit terminal. Verify the fork-tree records, the Missions window renders N branches, selection/trim behaves, and N real satellites materialize at recording end.
- **Reusable booster flyback** (FMRS-class profile - the recorder's home turf): booster = controlled-decoupled child flown back to a landing. Verify the branch loops with the main mission and the booster's landed terminal spawns/recovers correctly.
- **Launch from a NON-Kerbin body** (Eve return ascent, Mun surface -> orbit, Laythe spaceplane): `Rotation(B)` / `launchBodyName` handling is generic by construction through the zero-drift scheduler, but every test and playtest to date launches from Kerbin. Verify phase-lock + pad anchoring for an off-Kerbin launch site (also exercises rewind-from-surface there).
- **Claw couples** (asteroid / derelict grabs): verify a claw `OnPartCouple` records as a Dock-equivalent branch point, and that a claw-coupled asteroid (PotatoRoid part) survives ghost-visual building and the snapshot part-name path.
- **Long surface expeditions** (Elcano-class rover circumnavigation, days of driving): no alignment problem (surface sections are rotation-locked and render correctly at any UT) but a recording-size / optimizer / polyline-budget STRESS case; measure before declaring supported.
- **Round-trip resupply with vehicle reuse** (the Routine Mission Manager marquee profile: outbound dock, return, recover): the Missions side (whole-tree span loop incl. the return leg) should already work; the delivery-AND-recovery-per-cycle economics are logistics-roadmap territory - verify the rendering half here, leave the ledger half to logistics M1-M6.
- **Suborbital tourist hop** (career tourism staple): atmospheric-only -> unconstrained free loop; should be the trivial case - one verify run.

#### Verification sweep run 1 - automated pass + operator runbook (2026-07-06)

**Environment.** KSP 1.12.5. Parsek 0.10.3, origin/main @ `d5068e679` (PR #1235). Deployed DLL `sha256 aa4a5887bbd9146a39f923fe2209564c262077f8a36c1c10f5c11d7b1010a55e`, byte-verified equal to the worktree build (`Source/Parsek/bin/Debug/Parsek.dll`). Headless xUnit suite on this commit: 16842 passed / 0 failed / 1 skipped (25 s).

**Scope honesty note - read before trusting the table.** This run was performed by a CLI agent that CANNOT pilot KSP or observe on-screen rendering. Every M-MIS-10 acceptance criterion is an in-game OBSERVATION (loop cycles across FLIGHT / Space Center / Tracking Station, ghost-icon-rides-its-own-orbit-line, non-orbital legs not gliding below terrain, camera hand-offs at stage boundaries, re-aim plane fidelity, line jitter on pan). NONE of those were observed here. The table asserts NO in-game PASS/FAIL; it records only (a) the automated verification that WAS run and (b) the automated-coverage status of the machinery each archetype exercises. The observational cells are OPERATOR-REQUIRED and NOT YET OBSERVED - the per-archetype runbook below is what an operator runs to fill them in. The KSP.log currently in the install is save `s15` / Parsek V0.10.0 (a logistics-branch session), NOT an archetype run and NOT this build, so it is not archetype evidence; no collect-logs snapshot was fabricated from it.

**Per-archetype status** (the 7 archetypes above; the task working matrix was the first 5). Result column values: `AUTO-PARTIAL` = machinery has headless/in-game coverage but no dedicated end-to-end test of this shape; `AUTO-NONE` = no meaningful automated coverage; observational verify is `PENDING-OPERATOR` in all rows.

| # | Archetype | Machinery automated-coverage | Dedicated end-to-end test | Observational verify | Log-snapshot label |
|---|-----------|------------------------------|---------------------------|----------------------|--------------------|
| 1 | Constellation deploy (N-fork decouple -> N orbit terminals) | AUTO-PARTIAL: controlled-decouple + fork (`DecoupledSubtreeAudioStopTests`, `ControlledChildParentAnchoredPlaybackTests`, `RewindForkSegmentPhaseTests`, `ParentAnchoredChildSpineInGameTest`) + terminal spawn (`SupersedeCommitTests`, `PostSpawnTerminalStateTests`) | NO | PENDING-OPERATOR | none yet |
| 2 | Booster flyback (decoupled child flown to landing) | AUTO-PARTIAL: stage split + landed terminal (`BoosterStagingSplitTriggerTests`, `LandedGhostClearance_*` in-game, `MergeLandedReFlyCreatesImmutableSupersede`); reusable synthetic `Booster Drop`+`Booster Drop SRB` pair | NO | PENDING-OPERATOR | none yet |
| 3 | Off-Kerbin launch (Mun/Eve/Laythe pad + phase-lock) | AUTO-PARTIAL (2026-07-07, coverage run 2 - was AUTO-NONE): dedicated headless fixtures run the REAL `ExtractConstraints` + `TryBuildRelaunchSchedule` for a Mun PAD launch (`MissionPeriodicityTests.Extract_MunPad*` / `Extract_MunLaunchKerbinReturn_*`, `MissionZeroDriftScheduleTests.BuildSchedule_MunPad*` / `SelectAnchor_MunPad*`) incl. the Mun-launch + Kerbin-return cross-parent decline; in-game `RealSave_OffKerbinLaunchMission_PadAnchorsToLaunchBodyRotation` (Missions category) validates a committed off-home-pad mission against the live body graph + builder wiring, skipping cleanly when the save has none. No real off-Kerbin launch has been FLOWN + committed yet (rewind-from-surface off Kerbin still unexercised) | NO | PENDING-OPERATOR (HIGH RISK) | none yet |
| 4 | Claw couples (PotatoRoid grab as Dock-equivalent) | AUTO-STRONG since the claw producer (branch `logistics-claw-producer`, 2026-07-08): xUnit `ClawProducerTests` (classifier truth table, kind admission, empty-grapple skip, mid-run grab tree, codec + hash pins, PotatoRoid part-name pin) + in-game `LogisticsGrapple` category incl. the isolated-tier `GrappleCaptureInGameTest` automated gate (real `Part.Couple`/`Part.Undock` cycle on spawned live claw + PotatoRoid parts: Grapple stamping, EVA-suppression silence, window capture + undock completion, asteroid ghost-visual geometry, structural-grab admission verdict; one Ctrl+Shift+T Run All + Isolated in any FLIGHT scene; the gate self-discards the ephemeral auto-record session in setup, so no pre-run operator action is needed); plus coverage run 2 (2026-07-07): `ClawCoupleRecordingTests` pins the Dock-equivalent branch point, asteroid partner resolution + route eligibility, breakup-scan rejection of the raw asteroid AND the post-grab merged ship, and the PotatoRoid snapshot part-name path (+ `VesselSnapshotBuilder.ClawedAsteroidShip` generator), and in-game `ClawCouple` category (`ClawCoupleInGameTest`) verifies PotatoRoid/GrapplingDevice PartLoader resolution incl. the underscore->dot leg and that a synthesized pod+claw+PotatoRoid snapshot survives ghost-visual building | NO | PENDING-OPERATOR narrowed to the stock 0.06 m contact-capture FSM + a full gameplay route cycle (collect opportunistically) | none yet |
| 5 | Elcano / endurance rover (long surface traverse) | AUTO-PARTIAL: surface-relative render + clearance (`Pipeline_Terrain_RoverClearance_StaysConstant`, `LandedGhostClearance_*` x5, `HorizonRotationNearSurface`); no long-recording size / optimizer / polyline-budget stress | NO | PENDING-OPERATOR | none yet |
| 6 | Round-trip resupply (render half) | AUTO-PARTIAL: whole-tree span loop (`MissionLoopUnitBuilderTests`, `MissionCompositionTests`) + dock composition (`MissionDockCompositionRuntimeTest`) | NO | PENDING-OPERATOR | none yet |
| 7 | Suborbital tourist hop (atmospheric-only free loop) | AUTO-PARTIAL: atmospheric polyline (`GhostTrajectoryPolylineBuildTests`) + free-loop span clock (`MissionPeriodicityTests`) | NO | PENDING-OPERATOR | none yet |

**Two highest-risk UNVERIFIED cells** (an operator run should prioritize them): #4 Claw couples (automated halves landed with the claw producer 2026-07-07, but the REAL contact capture at 0.06 m, the release split, and the PotatoRoid ghost-visual build have still never run live) and #3 off-Kerbin launch (pad anchoring + synodic / pad-aligned phase-lock + rewind-from-surface off Kerbin never run in-game). These are pre-existing VERIFICATION GAPS, not observed regressions - do not read them as bugs until an operator run shows a break.

**Coverage run 2 (2026-07-07, branch `claude/m-mis-10-coverage-gaps-q2j54e`) - automated tests for the two highest-risk cells.** Both #3 and #4 flipped AUTO-NONE -> AUTO-PARTIAL (see the table); the OBSERVATIONAL cells stay PENDING-OPERATOR and the runbook labels (`mmis10-offkerbin`, `mmis10-claw`) are unchanged. Findings from the investigation (none are observed breaks):

- **Claw couple routing CONFIRMED shared with dock (no defect):** KSP's claw (`ModuleGrappleNode`, via `Part.Couple`) fires the same `GameEvents.onPartCouple` Parsek subscribes to (`ParsekFlight.cs:1200`), and `ParsekFlight.OnPartCouple` has no docking-port / module-type filter, so a claw grab takes the identical tree dock-merge path (`HandleTreeDockMerge` -> `CreateMergeBranch(BranchPointType.Dock, ...)` -> `BuildMergeBranchData`). The sweep's "records as a Dock-equivalent branch point" claim holds by construction; now pinned in `ClawCoupleRecordingTests`.
- **Cosmetic gap, filed for awareness (not fixed):** `BranchPoint.cs:49` lists `"CLAW"` as an intended `MergeCause` value, but `ParsekFlight.GetMergeCauseForBranchType` (`ParsekFlight.cs:5143`) only ever emits `"DOCK"` / `"BOARD"` - a claw grab records `MergeCause="DOCK"`. Purely cosmetic today (nothing branches on a CLAW cause); differentiating it later is a conscious contract change against the pins in `ClawCoupleRecordingTests`. (Update, claw-producer merge: the CONNECTION KIND half of this finding is superseded - the live path now stamps `RouteConnectionKind.Grapple` via `ConnectionProducerClassifier`; the `ClawCoupleRecordingTests` DockingPort pin exercises the `BuildMergeBranchData` default-parameter fallback, which is unchanged. The `MergeCause="DOCK"` half still holds.)
- **PotatoRoid ghost MESH contribution is prefab-dependent (reported, not asserted):** stock asteroids build their procedural mesh at runtime via `ModuleAsteroid`, so the PotatoRoid prefab may contribute no static mesh to a ghost. `ClawCoupleInGameTest.ClawedAsteroidSnapshot_SurvivesGhostVisualBuild` hard-asserts the part RESOLVES (`skippedPrefab == 0`) and the build survives, and logs whether the asteroid contributed a mesh - the operator run should eyeball whether a grabbed-asteroid ghost looks acceptable without the rock.
- **Periodicity/scheduler Kerbin-assumption audit came back clean:** the extraction + zero-drift scheduler production path has NO home-body hardcoding - `LaunchBodyName` derives purely from the earliest recorded surface/orbit body (`MissionPeriodicity.cs:414`), `FlightGlobalsBodyInfo` reads all periods live off `CelestialBody`, and every `"Kerbin"` literal in production is a codec/deserialization fallback, a UI day-length constant, or a KSC-specific classifier. One deliberate design-scoped gate noted: logistics route origin proof requires a NAMED Kerbin launch site (`RouteAnalysisEngine.IsKscOriginRecording`, `RouteAnalysisEngine.cs:835-840`, mirrored in `RouteBuilder`), so an off-Kerbin PAD-origin supply route classifies as undocked-start (M1 workflow gate) rather than KSC-origin - logistics-roadmap territory, not a missions-path bug.

**Operator runbook** (each archetype: fly-or-reuse -> commit -> configure looped Mission in the Missions tab -> observe a few cycles in FLIGHT + Space Center + Tracking Station; `python scripts/collect-logs.py <label>` immediately after each run; then grep the collected `KSP.log`):

- Common per-cycle checks (all archetypes): mission loops as a UNIT on the shared span clock; relaunch cadence is sane for the shape (atmospheric = continuous free loop; interplanetary = SYNODIC cadence via window index `k` + continuous arrival hold + `PadAlignLaunch`); self-overlap (period < span) staggers instances; re-aim (if the shape has a transfer) resolves OR cleanly declines to faithful, never a broken / off-plane arc; the ghost icon rides its OWN orbit line; non-orbital legs (surface / atmospheric / descent) draw and RETIRE cleanly (no sub-surface glide, no blink-out at hand-offs, no doubled / jittering line on pan); watch-mode camera hands off between stages without losing the vessel. ACCEPTED RESIDUAL (note if seen, do NOT file as new): body-fixed burn arcs rendering ROTATED under a station / arrival hold - cosmetic-under-hold only.
- Ctrl+Shift+T in-game test runner: run before/after each archetype to confirm the integrated build is green in the live scene. Relevant categories: `Reaim`, `Loop`, `MapRender`, `Watch`, `Missions`/`MissionPhasing`, `ParentAnchored`, `Descent`. Results auto-export to `parsek-test-results.txt` at the KSP root (collect-logs.py grabs it).
- (1) Constellation: build an N>=3 payload carrier with N controlled decouplers into distinct orbits; verify fork-tree records N branches, Missions window renders N branches, selection/trim behaves, and N real satellites materialize at recording end. Label `mmis10-constellation`. Grep: `[Parsek][*][TerminalSpawn]`, `[Parsek][*][Fork]`, `needsSpawn=`, any `WARN`/`EXCEPTION`.
- (2) Booster flyback: two-stage craft, controlled-decouple the booster and fly it back to a landing (reuse the `Booster Drop` synthetic to eyeball the branch first). Verify the booster branch loops with the main mission and the landed terminal spawns/recovers. Label `mmis10-flyback`. Grep: `[Parsek][*][TerminalSpawn]`, `landed`, `recover`, `ParentAnchor`.
- (3) Off-Kerbin launch: launch from a non-Kerbin surface (Mun pad-equivalent is simplest). Verify pad anchoring + phase-lock relaunch and rewind-from-surface there. Label `mmis10-offkerbin`. Grep: `launchBody`, `PadAlign`, `PhaseAnchorUT`, `[Parsek][*][Relaunch]`.
- (4) Claw couples: the event pipeline, window stamping, asteroid ghost-visual build, and admission verdict are AUTOMATED (`GrappleCaptureInGameTest`, one Ctrl+Shift+T Run All + Isolated in any FLIGHT scene with a live vessel; auto-recording is handled by the test's own setup). The remaining operator observation is the stock contact capture itself: grab a PotatoRoid asteroid (or a derelict) with the Advanced Grabbing Unit in real play and verify the recorded branch matches the automated fixture's shape. Label `mmis10-claw`. Grep: `OnPartCouple producer classified`, `Route proof dock window captured` with `kind=Grapple`, `PotatoRoid`, `[Parsek][*][GhostVisual]`, part-name resolve failures.
- (5) Elcano rover: a long surface traverse (hours of driving / large sample count). Verify the recording size / optimizer / map polyline budget hold at scale and surface render stays glued to terrain. Label `mmis10-rover`. Grep: `[Parsek][*][Optimizer]`, `polyline`, `budget`, `Points=`.

**Merge-blocker read for in-flight Missions / logistics PRs:** none. The integrated `origin/main` (#1235) headless suite is fully green and the deployed DLL is byte-verified; the findings here are pre-existing coverage GAPS, not regressions, so nothing in this sweep blocks the open PRs. FAIL-triggered focused todo entries are opened only when an operator run actually observes a break (template: exact shape/config, repro steps, observed vs expected, log signature, file:line if localized).

### Explicitly out of scope (faithful replay is the accepted, UI-surfaced behavior; revisit only on playtest demand)

- **Gravity-assist / multi-heliocentric-leg transfers** - `ReaimClassifier` rejects more than one heliocentric leg (ReaimClassifier.cs:141-146); re-aiming a chained assist is a different problem class (the assist geometry constrains every leg jointly).
- **Atmo-direct / aerocapture arrival alignment** - no captured destination orbit means no boundary to insert an arrival hold at; the body-fixed entry/descent already self-anchors to the live rotation and lands at the correct geographic site on loop, so only the approach-to-entry seam misaligns.
- **Porkchop-style dv-optimal window planning** - evaluated in the 2026-05-28 survey and deliberately not needed by the congruent-window model (recorded tof + synodic spacing; M-MIS-3 adds geometry-aware tof centering, still not a porkchop grid).
- **Grand tours / multi-destination single missions** (land-on-every-body challenge flights) - joint faithful recurrence across many transited bodies is effectively never; the accepted loop behavior is faithful replay plus whatever per-leg alignment M-MIS-6/7 provide. No whole-tour alignment is planned.
- **Crew rotation / tourism as DELIVERABLES** - the mission loop renders a crew ferry fine today; counting kerbals as route cargo (crew manifests, rotation credit) is logistics-roadmap territory (`docs/parsek-logistics-supply-routes-design.md` section 19, added by PR #1113), not a Missions milestone.
- **Off-world construction launches** (Extraplanetary Launchpads-class mods: vessels rolled out from a base instead of KSC) - modded compatibility tier; revisit only on demand.

---

## TODO - Looped re-aim interplanetary transfer: no continuous encounter into the destination SOI; line dead-ends in open space (investigated 2026-06-15, NOT fixed - regression-sensitive, deferred)

**Symptom (playtest 2026-06-15; looped 'Duna One' mission re-aimed while flying a fresh Duna mission; log `logs/2026-06-15_1906_duna-mission-investigation`, main @a4ff95b7c V0.10.0, save s15):** the re-aimed ghost's interplanetary transfer LINE is not rendered as a proper encounter. It heads toward Duna's orbit but dead-ends in open heliocentric space, never bending into Duna's SOI; viewed mid-cruise the arc's far end sits at where Duna WILL BE at arrival (empty now), and the recorded Duna-capture hyperbola is a detached segment across a gap. The instrumented form of the same defect is a ~62 deg ghost-transform teleport at BOTH SOI handoffs (`[ReaimSeam] SEAM member=30`: Kerbin->Sun jump=87.76 Mm = 1.043x Kerbin SOI, `KSP.log:21896`; Sun->Duna jump=49.19 Mm = 1.027x Duna SOI, `KSP.log:32175`). Re-aim ENGAGED cleanly (did not decline to faithful), the Lambert solved a sane prograde transfer, and the ghost ICON does reach Duna's SOI - so the orbit is correct; the defect is the stitch / encounter rendering, not a solver failure.

**Root cause (HIGH confidence; SYSTEMATIC + design-deferred, NOT inherent):** re-aim substitutes ONLY the heliocentric coast with a FRESH center-to-center Lambert (`Reaim/ReaimTransferSynthesizer.cs`: r1 = launch-body center, r2 = target-body center, recorded tof reused) and replays the recorded Kerbin-escape + Duna-capture hyperbolae VERBATIM at their original asymptotes. Two superimposed sources:
1. ENDPOINT (dominant, ~96% of the jump): Lambert endpoints are at planet CENTERS but the transfer renders FULL-SPAN (`Reaim/ReaimPlaybackResolver.cs:232-247` passes NaN render bounds). At the seam UT the synth arc is at the body center while the recorded leg ends at the SOI boundary, so each jump is ~1 SOI radius. (An earlier pass that DID trim the launch side to the SOI-exit UT was REVERTED - it opened a gap right after launch SOI exit where the orbit ghost was destroyed and the transfer line restarted displaced by the launch body's own motion. See the comment at `ReaimPlaybackResolver.cs:232-243`.)
2. ORIENTATION / SHAPE (~4% + a 2.5% sma / different ecc gap): the fresh Lambert has zero v-infinity awareness of the recorded asymptotes and reuses the recorded tof (geom tof differs by ~330,610 s here, `devFromGeom` in the `re-aimed transfer ready` line), so over a fractional 0.5835 synodic the asymptote directions and orbit shape differ from the verbatim recorded legs.

The original design (`docs/dev/done/plans/reaim-interplanetary-transfers.md:252-279, 359-361`) accepted the orientation residual as "the accepted small seam" and shipped only PadAlignLaunch; SOI-handoff continuity ("option 3: re-plan the whole patched-conic chain") was explicitly DEFERRED. The same-body 45 deg / 120 s seam-bridge (`GhostTrajectoryPolylineRenderer.cs` `IsBridgeAdjacentConic`) cannot cover a cross-SOI body-change seam by construction.

**Fix direction (the ONLY geometrically sound one; large effort):** the design's deferred "option 3" - synthesize the WHOLE patched-conic chain from one solve so the escape hyperbola's SOI-exit STATE matches the heliocentric departure v1 and the capture hyperbola's SOI-entry STATE matches the arrival v2, instead of splicing a fresh heliocentric arc onto verbatim recorded SOI legs. All three legs then meet at the same SOI-sphere position with continuous velocity, giving a real encounter into the SOI. Immutability-safe (in-memory loop-only on copied structs) and MIT-clean (reuses the already-solved v1/v2; no new solver). SEQUENCE AFTER the in-flight reaim branches land (`reaim-lambert-reliability`, `reaim-eccentric-tof`, `reaim-dest-loiter-retimer` / PR #1155, `fix-soi-trajectory-seam-coverage`) to avoid stacked re-aim rewrites.

**Rejected shortcuts (adversarially verified 2026-06-15, workflow wf_2ead60c5-a19; all NOT VIABLE except E as a stopgap):**
- (A) rigid-rotate the recorded transfer to the new epoch: geometrically false (escape is Kerbin-frame, capture is Duna-frame, only the middle leg is heliocentric; one Sun rotation cannot rotate all three) and misses the dominant endpoint gap.
- (B) anchored / shooting solve to SOI-boundary endpoints + recorded asymptotes: over-determined (a single-rev Lambert has only tof free once r1, r2 are fixed; one scalar cannot match two 3D asymptote directions) and reopens the reverted trim regression.
- (C) render-only rotate the recorded legs onto the solved asymptotes: corrects only the ~4% angular residual, leaves the ~1-SOI-radius endpoint gap, and needs the reverted trim.
- (D) cross-SOI render-only seam bridge: cannot move the ICON / proto-orbit (they ride the ghost transform, not the polyline), and a 62 deg cross-body connector is the wild-spiral / planet-intersection case the bridge was gated against.
- (E) accept the gap + clip the line / suppress the icon across the handoff: VIABLE only as a labeled STOPGAP - it does NOT make the line accurate (a tidy gap is still no encounter, the user's actual complaint) and re-litigates the reverted trim.

**Do NOT do (regression guards):** do not rewrite / finalize / load-time-modify recorded data (.prec / OrbitSegments) on any path - re-aim stays in-memory loop-only; do not vendor a GPL solver (Parsek is MIT); do not auto-extend the heliocentric draw / icon window over the SOI escape / capture window (tried + reverted, puts the ghost behind the planet); do not trim the full-span render without also fixing the capture leg (the reverted gap regression); revert-on-regression - prior re-aim rewrites went net-negative, the current single honest kink is a known baseline; pin the requirement against this one concrete case before writing code.

**Validation must be the ENCOUNTER, not the seam number:** re-run the looped Duna One re-aim playtest, collect a fresh log, and confirm the LINE visibly enters Duna's SOI in-game (a real encounter) and the ICON follows it through both handoffs - NOT merely that the `[ReaimSeam]` jump dropped below one SOI radius. Also confirm: re-aim still ENGAGED, the synth geometry is still a sane ellipse, and no new "gap-between-orbit-segments" / orbit-ghost-destroyed warnings appear at the launch SOI exit (the reverted-trim regression must not return).

**INSTRUMENT ADDED (encounter geometry) - the measurement this entry's validation clause asks for now exists, report-only.** The clause above says validation must be THE ENCOUNTER, not the seam number, and until now nothing measured the encounter: the parity oracle measures each segment against its OWN reference (blind to the gap BETWEEN segments) and explicitly skips re-aimed members (`MapRenderProbe.ComputeFaithfulOrbitParity`'s `reaimed-or-foreign-seed` skip), `loop-seam-teleport` measures the ghost TRANSFORM rather than the drawn line and is exempt at rebind frames by clock-delta, `[ReaimSeam] dist[target]` measures the transform (which this entry records as ALREADY reaching the SOI), and `xfer-vs-target@soi` measures the SOLVE (recorded as already correct). The new `seam-endpoint-outside-soi` lens takes the missing one: at the member's next recorded cross-body seam it samples the CURRENTLY RENDERED conic at that seam instant and measures `|renderedPos(seamUT) - destBody.pos(seamUT)| / destBody.sphereOfInfluence`, both terms propagated to the seam UT through `getTruePositionAtUT` (never a current-anchored position - that would disagree by each body's own displacement since the recorded instant). Correct encounter <= 1.0; raises above 1.005, a threshold calibrated between the S1.8 measured healthy seam continuity (1.2e-4 / 1.5e-4 of the crossed sphere) and this entry's measured 1.027 / 1.043. Pure core `Source/Parsek/MapRender/SeamEndpointOracle.cs` (xUnit `SeamEndpointOracleTests`), Unity capture `MapRenderProbe.ComputeSeamEndpointGeometry`, raised once-per-onset from `TrySampleAndEmitSeamEndpoint`, `mapRenderTracing`-gated, REPORT-ONLY (`hlib.ANOMALY_REASONS_RAISED_UNGATED`). THREE THINGS IT DOES NOT DO, stated so a silence is not over-read. (1) It measures the CURRENT leg's own endpoint, not the inter-segment jump: the 1.043 figure above is `|escape-end - transfer-start|` over the ORIGIN body's sphere, a different quantity in BOTH terms. The departure seam produces NO reading at all rather than a healthy one - `toBody` there is the Sun, whose sphere is not a finite encounter target, so the sample lands in `skip.no-usable-ratio`. That half of the defect is not a raise class awaiting demonstration; it is one the capture cannot construct, and the sizing of 1.005 does not depend on it (the binding term is the SMALLEST measured defect, 1.027, which V4's census proved reachable). The arrival seam is the one it sees, and that is the dominant symptom (the transfer dead-ending short of Duna). (2) A FAITHFUL loop replay of an interplanetary transfer raises it by design - the destination has moved on in inertial space by the loop shift - so read the line's `seed=` / `loopShift=` fields before calling a raise a defect. (3) **A RE-AIMED MEMBER WHOSE ARRIVAL THE PRODUCER RE-TIMED IS REFUSED, NOT MEASURED (added 2026-08-09).** The lens samples at the RECORDED seam instant mapped onto the rendered clock; the clock mapping relocates the instant, it cannot discover that the producer moved the arrival. On the F2 parking-departure path the tof search is centred on the geometric Hohmann time with the recorded tof deliberately unused (`ReaimTofSearch.BuildParkingCandidateTofs`; the s15 fixture's recorded tof is ~1.44x Hohmann), the render span is trimmed to `[RecordedDepartureUT, RecordedDepartureUT + usedTof]`, and the recorded capture leg is `ShiftInTime`'d back to meet it - so the drawn arc reaches the destination DAYS before the recorded seam. Measuring there is WRONG, not merely uncalibrated, and the resulting miss is orders of magnitude past the 1.005 tolerance: it would raise `seam-endpoint-outside-soi` on a CORRECT arc, indistinguishable in kind from the 1.027 reading the instrument exists to find. `ComputeSeamEndpointGeometry` therefore refuses the sample (`skip.reaimed-seam-instant-unknown`) when the Director's driven seed is re-aimed AND the seed's own arc end disagrees with the recorded seam by more than a second. The refusal is deliberately narrow - a re-aimed member the producer did NOT re-time (the DIRECT path, where candidate step 0 IS the recorded tof and the render span falls back to the recorded arrival) compares equal and still measures, which is why V4's census reading is unaffected. "Unknown" rather than "re-timed": the seed's end is not a usable substitute instant either, because the seed is whichever arc the Director drives THAT frame and on a two-burn departure that can be the re-phased heliocentric PARK, whose end is the departure burn. NOT FLOWN EITHER WAY - no committed lane exercises the parking-departure path, so this population has produced neither a raise nor a skip in the field, and the refusal is a reasoned decision awaiting a measurement. Registered as the third promotion blocker in `hlib.ANOMALY_REASONS_RAISED_UNGATED`'s comment block. **RE-FLOWN AFTER THE CHANGE, because a sampler edit invalidates the census it was measured against:** V4 `2026-08-09_1158` (PASS attempt 1, wall 76 s) and V7M `2026-08-09_1159` (PASS attempt 1, wall 55 s) both re-read `seam-endpoint summary evaluated=1 outsideSoi=0`, unchanged, with the new `skip.reaimed-seam-instant-unknown` bucket appearing ZERO times in either log and zero anomalies of any reason. That is the predicted result and it is what makes the guard's narrowness a measurement rather than an argument: V4's sample is a re-aim owner on the DIRECT path, where the render span falls back to the recorded arrival and the seed's end IS the recorded seam, so the guard compares equal and the lens still measures - the pinned `evaluated=[1-9]` on both lanes is intact. **ANTI-VACUITY ACCOUNTING ADDED (2026-08-09), because the first reading run proved the instrument could not report on itself.** The lens flew on five re-flights and raised ZERO times, and nothing in the log could distinguish sound geometry from a lens that never evaluated a seam - the re-flight agent had to DERIVE the answer offline from covering-segment lines plus the fixtures' `.prec` chains. It was the second: at every frame where the lens resolved a ghost, the drive clock sat in the recording's LAST `OrbitSegment`, so the capture bailed on `no-cross-body-successor` before measuring anything - these fixtures' recordings simply END in the destination park, with no cross-body successor left ahead of the clock. The fix mirrors the faithful-parity lens's own per-pass accounting: one Verbose `[Parsek][VERBOSE][MapRenderTrace]` line per probe pass, `seam-endpoint summary evaluated=<n> outsideSoi=<n>` followed by zero-or-more ` skip.<reason>=<n>` (same field grammar as `faithful-parity summary ...`, same 5 s rate-limited key shape). `evaluated` counts destination-approach checks the lens ACTUALLY PERFORMED; every other ghost-frame lands in exactly one `skip.` bucket (the twelve capture reasons - `no-rendered-orbit`, `no-recId`, `no-recording-or-segments`, `no-covering-segment`, `no-covering-body`, `body-mismatch`, `no-cross-body-successor`, `seam-ut-not-finite`, `seam-behind-clock`, `to-body-unresolved`, `reaimed-seam-instant-unknown`, `propagation-threw` - plus `no-usable-ratio` for a sample the oracle could not turn into a ratio, which is EITHER a non-finite / non-positive SOI radius, e.g. the Sun as a destination, OR a non-finite endpoint distance from degenerate rendered elements; that bucket was called `no-soi-measurement` and documented with only the first cause until 2026-08-09, which would have read a broken rendered conic as "the destination had no finite sphere"), so `evaluated + sum(skips)` is the number of ghost-frames the lens was handed. The summary is emitted ONLY when `evaluated > 0 || skip reasons > 0`, which is what keeps it out of the `probe frame summary` trap: that line primes its shared 5 s key on a ghostless frame ~2 ms after the tracer flips, and the short lanes (V4/V6M/V6T/V7M/V7T, 2.2-3.8 s) never get a second emit. Both counters are only touched from inside the per-ghost capture, so passing the guard means at least one ghost genuinely reached the lens. Pure guard + formatter `SeamEndpointOracle.ShouldEmitPassSummary` / `FormatPassSummary` (xUnit `SeamEndpointOracleTests`); the counting itself lives in the Unity sampler.

**FIRST REAL-GEOMETRY CENSUS, 2026-08-09 - AND IT FALSIFIED THE OFFLINE DERIVATION ON TWO OF THE FIVE LANES.** The paragraph immediately above records the pre-census belief: every lens-reaching frame sat in the recording's LAST `OrbitSegment`, so the capture bailed `no-cross-body-successor`, because "these fixtures' recordings simply END in the destination park". That was assembled BY HAND from covering-segment lines plus the fixtures' `.prec` chains, precisely because the lens could not report on itself. **It was wrong for V4 and V7M.** The five lanes re-flown with the census on (runs `2026-08-09_1015` / `_1016` / `_1017` / `_1018` / `_1020`), each line verbatim:

| lane | `seam-endpoint summary` | reading |
|---|---|---|
| V4-player-loop-workflow | `evaluated=1 outsideSoi=0` | **EXERCISED** - Sun->Duna arrival seam |
| V6M-mun-player-loop | `evaluated=0 outsideSoi=0 skip.no-cross-body-successor=1` | structural zero |
| V6T-mun-ts-arrival | `evaluated=0 outsideSoi=0 skip.no-cross-body-successor=1` | structural zero |
| V7M-minmus-player-loop | `evaluated=1 outsideSoi=0` | **EXERCISED** - Kerbin->Minmus arrival seam |
| V7T-minmus-ts-arrival | `evaluated=0 outsideSoi=0 skip.no-cross-body-successor=1` | structural zero, byte-identical in all three collected logs of that lane's two runs - but read the verdict note below before counting those as three corroborations |

ZERO raises on any lane; V7T's `icon-off-orbit` red is its own documented finding, and the encounter lens moved no verdict anywhere - the report-only registration behaves as designed. **WHAT THE TWO POSITIVE READINGS ARE.** V4's evaluated frame (frame 6464, currentUT 28,781,000, driveUT 9,127,924.233; `shadow ... segIdx=4 treatment=StockConic body=Sun`, `Cached body-frame bounds ... rawBodyFrameUT=4742953.22-9128108.76 segIndices=6-11`) covers the SUN heliocentric transfer leg ending 9,128,108.76, whose successor is the Duna approach - so the lens measured THE ARRIVAL SEAM OF AN INTERPLANETARY TRANSFER, the exact geometry class this entry's 1.027 defect lived in, and read it INSIDE the sphere. It did so on a frame where the faithful-parity sibling stood down (`skip.reaimed-or-foreign-seed=1`, Director seed `epoch+shift=NaN`) - the "deliberately NOT re-aim-gated" design property paying off in the field, on the very population the parity oracle cannot see. V7M's (frame 7777, currentUT 1,345,035, driveUT 267,563.741) covers a Kerbin leg ending 267,741.32 with the Minmus approach next: a faithful, phase-locked, same-parent arrival, also inside the sphere - first evidence that the same-parent road reads healthy rather than raising, though it is one lane and one frame, not a general result. Both reproduced BIT-IDENTICALLY on **three consecutive flights** each (`_1015`/`_1028`/`_1036`, `_1018`/`_1031`/`_1036`), so `seam-endpoint summary evaluated=[1-9]` is now REQUIRED on those two specs; all five carry the presence form `evaluated=\d+ outsideSoi=\d+`. THE REPEATED `_1036` IS NOT A TYPO and the two lanes did not share a run: run ids are scenario-qualified, and the tier flew them back to back inside one minute - `2026-08-09_1036_V4-player-loop-workflow` 10:36:03-10:36:54, then `2026-08-09_1036_V7M-minmus-player-loop` 10:36:54-10:37:49, each PASS attempt 1. Both lanes were then flown a FOURTH time after the capture gained the re-timed-arrival refusal (`_1158` V4, `_1159` V7M) and read `evaluated=1 outsideSoi=0` again, which is what establishes the refusal cost neither lane its measurement. The three structural zeros are a property of the destination-park dwell those lanes share, not of the instrument.

**THE CAPTURE'S VALUE-CORRECTNESS IS STILL ONLY HALF COVERED (filed 2026-08-09).** The oracle's arithmetic has 20+ xUnit cells and the two `evaluated=[1-9]` pins prove the capture path EXECUTES, but neither says the computed distance is the right one. Partly closed in this branch: the capture's three list/clock DECISIONS were factored out of `ComputeSeamEndpointGeometry` into pure helpers - `FindCoveringOrbitSegmentIndex` (the half-open-except-last covering rule, whose INDEX the successor walk needs), `FindCrossBodySuccessorIndex` (walk through same-body cuts, stop at the body change), `ResolveSeamUTOnRenderedClock` (the three-way baked / raw / assumed-live mapping - flipping those arms moves the sample by a whole loop shift and nothing else would notice) and `IsRecordedSeamInstantUsable` - and each is now driven headlessly in `SeamEndpointOracleTests`. STILL UNCOVERED: the `getTruePositionAtUT` pair and the destination-body lookup, which need a live `Orbit` / `CelestialBody`. That is the same "green but blind" hole `MapRenderProbe.OrbitRelativePositionYup` names as a standing obligation and the codebase answers eight times over for the sibling captures (`ComputeFaithfulOrbitParity` / `ComputeSynthesizedConicParity` are driven from live fixtures by eight InGameTests files, `ReaimedLoopSynthesizedOracleInGameTest` being the template - the seam variant needs only a two-body segment chain on top, a fixture increment rather than a missing capability). Deliberately not built in this pass. Note what the existing pins do and do not guard: killing the covering scan or the successor walk would drop `evaluated` to 0 and red V4 + V7M, but corrupting the propagation pair yields a WRONG distance and reds nothing, because `outsideSoi` is deliberately unpinned and the raise is report-only.

**TWO NAMED NEXT STEPS, both filed rather than done.**
1. **THE CENSUS DOES NOT CARRY THE RATIO.** `outsideSoi=0` proves every evaluation came in at or under 1.005; the ratio itself prints only on a raise, so the MARGIN is unmeasured - we know the arc reaches the sphere, not by how much, and cannot yet tell 1.2e-4 (S1.8-healthy) from 1.004 (one nudge below raising). Fix is a census-format change - carry the pass's max ratio, e.g. `seam-endpoint summary evaluated=<n> outsideSoi=<n> maxRatio=<r>` - and it would make the HEALTHY population pinnable for the first time, which is what turns this instrument from a tripwire into a measurement.
2. **MAKING V6M REACH ITS SEAM IS A DWELL PROBLEM, NOT A BRACKET PROBLEM.** THE ARITHMETIC, from the runs' own `Cached body-frame bounds` lines. *Mun fixture (V6M/V6T), cycle-1 `loopShift=280,142.53`*: Kerbin leg raw `[230.03, 16,404.98]` segIndices 0-7, Mun leg raw `[16,404.98, 20,939.02]` segIndices 8-10 -> cross-body seam raw **16,404.98** = live **296,547.51**. V6M's four cycle-1 jumps are 296,370 / 296,490 / 296,690 / 300,977 -> drive 16,227.47 / 16,347.47 / 16,547.47 / 20,834.8, so **the first TWO are ALREADY PRE-SEAM** (by 177.5 s and 57.5 s) with the Mun successor ahead of them. The lane still reads `evaluated=0` because that pre-seam epoch survives about one frame: the jumps fire back-to-back with no dwell, and the only probe frame at 296,370 is the ghost's CREATION frame, which the probe's own guard (`offOrbit != null && !iconSuppressedNow && !suppressionLifted`, in `MapRenderProbe.Sample`'s proto-orbit-line block) rejects - NO census bucket is emitted at that epoch at all, which is how we know the lens was never OFFERED it rather than offered it and skipping. Minimal honest change: hold at 296,370 (or 296,490) for one or two extra probe frames before jumping on. NOT done here, deliberately: V6M is green, armed, and carries a pinned `BATCH_COMPLETE` tally, and adding dwell ticks is NEW dwell structure rather than a re-aim of an existing bracket. *Minmus fixture, cycle-1 `loopShift=1,077,471.26`*: Kerbin leg raw `[230.05, 267,741.32]` segIndices 0-5, Minmus leg raw `[267,741.32, 277,001.13]` segIndices 6-9 -> seam raw **267,741.32** = live **1,345,212.58**; V7M's jumps 1,345,035 / 1,345,155 / 1,345,355 sit at -177.6 s / -57.6 s / +142.4 s. Identical bracket geometry to V6M's, and V7M's first bracket is exactly what buys its measurement - which is the cleanest possible demonstration that the difference between the two lanes is dwell, not design. **DO NOT "FIX" V6T OR V7T THIS WAY.** Both park a single TS observation ~142 s PAST their seam on purpose; that just-inside-the-SOI epoch IS the claim those lanes make, and moving it pre-seam to feed this instrument would change the subject of the scenario to buy a measurement V6M can supply for the cost of a dwell.

**References:** full investigation + ranked options doc at the umbrella root `reaim-seam-investigation.md`; log snapshot `logs/2026-06-15_1906_duna-mission-investigation/`; engaged params `KSP.log:13201` (`ENGAGED re-aim Kerbin->Duna via Sun; D0=142619013 synodic=19653075 tof=6854613`).

**ATTEMPTED + REVERTED (2026-06-17) - option 3 (whole-chain synthesis). STILL OPEN; do not re-attempt without the preconditions below.** The deferred "option 3" was built behind a default-OFF flag (P0/P1 foundation #1169, P2-P4 chain synthesis #1170) and then REVERTED (#1171 reverts #1169; #1170 closed). Plan/design docs at the umbrella root: `reaim-fix-plan.md`. Why it was abandoned:
- **It regressed the flag-ON render and never solved the bug.** First playtest (Kerbal X #2): synth escape/capture legs were garbage hyperbolae (`[ReaimSeam] chain legs: escape ecc=12.9, capture ecc=7.77`; sane is ~1.05-1.3), so departure+arrival misaligned, Duna-SOI arrival render broke, icon teleported. Log `logs/2026-06-16_2351_reaim-chain-kerbalx2-regression/`.
- **Root cause (confirmed):** `ReaimTransferSynthesizer.BuildBodyRelativeLeg` paired the transfer POSITION at the SOI crossing with the Lambert ENDPOINT velocity v1/v2 (at the planet center), an inconsistent state vector. Fixed (use `transfer.getOrbitalVelocityAtUT(crossingUT)`) + added the plan's never-built periapsis/ecc fail-closed gate (`IsSaneLegConic`), so flag-ON fails closed to baseline instead of rendering garbage.
- **But it still could not be validated:** every available test mission is a fail-closed case. Duna One threads Ike (capture fails closed). Kerbal X #2 is a heliocentric-parking (two-burn) departure (escape fails closed) AND its re-aim render is independently broken by the #1166-engages-but-#1167-Increment-2-not-built span-greater-than-synodic gap, not by this work. Validating option 3 needs a CLEAN DIRECT Kerbin->Duna recording: no parking-orbit loiter, no moon (Ike) encounter. No such mission exists in the test save.
- **Structural doubt:** a center-to-center Lambert solve carries no information about the real ejection/capture periapsis, so the synth body-relative legs inherit whatever periapsis the SOI-crossing sample implies; the gate fails the bad ones closed, but a "consistent but wrong-altitude" leg can still pass. The premise may need rework (e.g. SOI-edge-to-SOI-edge solve, or seed the ejection from the recorded parking periapsis) rather than center-to-center.

**Do NOT re-attempt option 3 without (a) a clean direct no-park no-moon Kerbin->Duna looped recording to validate against, and (b) resolving the center-to-center-periapsis structural doubt.** Confirm a validatable test case exists BEFORE building (this arc was built end-to-end before that check, which was the core process mistake).

**Precondition (a) DISCHARGED (2026-08-06, duna-direct-lane branch): the clean direct recording EXISTS as a committed fixture.** The B17 lane produced it end to end: the DD1 Duna Direct Probe (authored by construction, `harness/tools/build_dd1_craft.py`, drift-gated), the PAD-ALIGN group of the shared B5 machine (pre-launch seam TimeJump to the ejection window computed from mlib's committed stock heliocentric ephemeris + ASAP ejection), `FORGE-b17-duna-pad` and `B17-duna-direct-orbit` (nightly, ARMED). MEASURED on the green flights (run ledger on the status doc row): a confirmed 54-day pad epoch jump, launch-to-ejection-node 1,791 s (under one park orbit - the no-loiter constraint as a number), the full Kerbin->Sun->Duna traverse with every forbidden Ike/Mun/Minmus token silent, capture into a 0.002-ecc ~300 km Duna park wholly inside Ike's orbital shell, and the tree committed in-flight with `terminalOrbitBody=Duna`. The produced save is the committed `harness/fixtures/saves/duna-direct-recorded` fixture (recording sidecars included via the harvester's new `--keep-parsek` mode; pinned by the `RECORDED_FIXTURES` sweep), so the option-3 validation lane can consume the recording WITHOUT re-flying the mission. NOTE for that lane: the committed recording is not yet LOOPED - looping is a playback-time property the consumer arms on the committed recording. Constraint (b) - the center-to-center periapsis structural doubt - REMAINS OPEN and is NOT touched by this lane; do not re-attempt option 3 without resolving it.

**Arrival-validation lane FLOWN (2026-08-06, loop-arrival-lane branch): the fixture's loop REPLAYS and both modes' arrival behavior is now MEASURED + ARMED.** Tier 1 (`S1.8-soi-crossing-playback`, daily, live-proven): recorded-segment position resolution is continuous across both flown SOI seams - measured gaps 10,146.3 m (Kerbin->Sun) and 7,284.0 m (Sun->Duna) against a 25 km pin, versus the ~50,000-90,000 KM discontinuity the defect class would show; playback lands inside Duna's SOI at arrival. Tier 2 (`V2-loop-arrival-dwell`, operator, live-proven + armed): mission-level loop armed on the committed DD1 tree via the promoted MissionConfig seam verb, map camera dwelt THROUGH both recorded handoff instants and the parked tail with mapRenderTracing armed. MEASURED, re-aim mode: the classifier ENGAGES for this member (plan.Supported=True - the property Duna One and Kerbal X #2 both lacked; `ENGAGED re-aim Kerbin->Duna via Sun` D0=24396028.988 tof=4385155.5, synth ecc=0.2624, phase-locked one synodic ahead of the arm-time anchor), the engaged replay crosses both handoffs with a CLEAN gated anomaly sweep at map scale (allowedAnomalies=[]; no icon-teleport, no icon-off-orbit raise), the ghost renders on the flown Kerbin escape hyperbola (sma=-5,357,267 with the one-synodic loop shift applied), and the proto orbit hands into the DUNA frame at the arrival dwell - now a hard-required armed token. The EXPECTED red did NOT reproduce on the map surface: the 2026-06-15 `[ReaimSeam]` 62-deg transform-teleport instrument lives on the FLIGHT-scene engine path, which a map/TS dwell never drives - so V2 bounds the defect's MAP-visible surface (none, at planet-view scale) and stands as the armed regression FLOOR for the option-3 work, while the defect's reproduction and the encounter validation above remain FLIGHT-scene playtest work. MEASURED, faithful mode: UNREACHABLE at flight time on this fixture - the classifier auto-engages any supported member and no product force-faithful switch existed, so the faithful-vs-re-aim A/B the lane brief wanted needed a product knob first. **That knob now EXISTS:** `ParsekSettings.forceFaithfulLoopPlayback` (Settings > Looping, "Force faithful loop playback (no re-aim)"; default OFF, GameParameters-only persistence mirroring `transitedBodyRotationModeIndex`, and SetSetting-whitelisted so a scenario can flip it from the command seam). With it on, a re-aim-SUPPORTED mission builds a unit byte-identical to a classifier decline (no plan, no schedule, no loiter cuts / launch hold / descent members; faithful anchor `Math.Max(LoopAnchorUT, spanEndUT)`), and the builder logs a grep-stable `[Reaim] MissionLoopUnit: mission='...' FORCED FAITHFUL (re-aim supported X->Y but forceFaithfulLoopPlayback is on)` - the per-member classify verdicts still emit unconditionally, so the log proves the member IS supported rather than silently declined. Headless pair over the flown duna-direct fixture: `MissionLoopUnitBuilderTests.ForceFaithfulOff_TheFlownDunaDirectMissionEngagesReaim` / `ForceFaithfulOn_TheSameMissionStaysFaithfulWithNoReaimState`. The faithful-mode DWELL FLIGHT itself is still to be flown (do not fake it by degrading the member). Full six-finding run ledger on the V2 row in `docs/dev/autotest-status.md`.

**FLIGHT-SCENE lane FLOWN (2026-08-07, flight-arrival-lane branch): the defect's actual surface is now instrumented, its visible envelope is bounded, and the missing product knob exists.** SHIPPED: (1) `forceFaithfulLoopPlayback` (Settings > Looping; SetSetting-whitelisted; gated at the builder's plan-consumption point so ReaimDiag still proves the member supported - the faithful-vs-re-aim A/B is UNBLOCKED); (2) the Tier-C `loop-seam-teleport` instrument (GhostRenderTrace.EmitAnomaly + the pure IsLoopSeamTeleport predicate, floor 1,000 km between the measured populations - healthy recorded seams 7-10 km vs this defect's 49-88 THOUSAND km - raised from the mode-agnostic ParsekFlight.TrackLoopSeamTeleport, faithful AND re-aimed members, ghostRenderTracing-gated, harness-sweep-gated at birth); (3) the V2-filed probe pid->recId mislabel fixed (GhostMapPresence.RebindGhostRecordingId). MEASURED, five flights across three specs (V3F/V3R/V3C, run ledgers on their status rows): (a) THE ZONE GATE - the flight engine hides a far loop ghost BEFORE positioning (GuardSkip hidden-by-zone; the hide precedes PositionFromOrbit), so a parked observer can NEVER drive the seam surface; the 2026-06-15 forensics fired precisely because the player flew ALONGSIDE. This bounds the defect's user-visible envelope: map clean (V2), recorded data clean (S1.8), parked flight scene never renders it (V3F/V3R) - it is visible ONLY while flying near the re-aimed ghost. (b) THE CYCLE FLOOR - an engaged unit's phase anchor floors at the recording's span END, so the replay cycle always starts >= one synodic after the recorded flight; a companion must fly the ghost's OWN later window, never the recording's. (c) REACHABILITY PROVEN - the cycle-aligned companion (V3C run 2: B17 pad-align mission + injected looped recording, both on the D0=24,396,029 window) drove the [ReaimSeam] dist[] trace x48 from an unattended run, the first time ever; the remaining gap is that the B17 window/correction tuning (cycle-1) MISSED the Duna encounter at cycle 2 (Duna e=0.051 makes window quality cycle-dependent), so the handoff instants were crossed out-of-zone and THE ~62-DEG TELEPORT REMAINS UNREPRODUCED-BUT-UNREFUTED: nothing measured contradicts the 2026-06-15 forensics; no unattended run has yet watched the seam instant from inside the zone. NEXT STEP (advanced on the v3c-encounter-tuning branch, 2026-08-08, runs 3-5; five-agent review corrected the ledger): the wider correction budget (800 vs the measured 522.1 m/s cycle-2 cost) CLOSED the encounter - every branch flight reached Duna. The review then established the hard wall: the flight engine's zone gate is 120 km (GhostVisualRangeMeters; the earlier ~55.8 Mm reading was the smallest HIDDEN sample, an upper bound only), and the Sun->Duna seam sits AT Duna's SOI radius (47.9 Mm) - so the arrival seam is UNOBSERVABLE from any Duna park, and the post-park bracket approach is dead by construction (its cycle-1 targets were also 7,190 s short: the span clock rides phaseAnchor 24,306,768.36 post-PadAlignLaunch-shift, true cycle-1 seam 48,434,260.29). What stands: co-flight reaches the engine surface (dist[] x56 with the live craft inside the ghost's 120 km corridor mid-coast), the Ike mode is arrival-geometry variance (aim-periapsis lever: >= ~4,300 km alt or <= ~1,600 km alt vs the ~1,734-4,026 km shell), and a refused-backward-TimeJump forbidden token now makes the non-gating post-mission-step trap self-detecting. THE SEAM-INSTANT OBSERVATION now needs a CO-LOCATION design - hold the observer inside 120 km of the ghost AT the seam instant (a to-the-second co-departure, or a temporary zone-relax debug aid in the MapRenderWarpControl mold, product-side, ships-off) - then fly BOTH modes through the seam in-zone, pin faithful as baseline and the re-aim teleport as the GS-3 tolerate-plus-require regression target. **WATCH MODE IS NOT AN ALTERNATIVE TO THAT DESIGN - MEASURED, not argued (V4-player-loop-workflow, run `2026-08-08_1135`, PASS attempt 1).** The obvious cheap idea once `EnterWatchMode` was drivable was to let watch mode carry the observer to the ghost instead of building co-location. It cannot, and the disqualifying half is the CAMERA half: watch mode only re-aims the flight camera at a ghost (`WatchModeController` never moves, spawns, or re-anchors the observing vessel), so a granted watch changes nothing about where the observer IS, and its 300 km entry / 305 km exit cutoff is nowhere near the 47.9 Mm the seam sits at. NOTE THE DIRECTION OF THAT COMPARISON, which an earlier draft of this paragraph had INVERTED: 300 km > 120 km, so watch mode reaches FURTHER than the zone gate, not less - the two are separate thresholds on the same distance, and in the 120-300 km band a watch can be GRANTED on a ghost the engine has already stopped positioning. That widens the band where a watch is grantable and leaves the conclusion untouched: a camera re-aim capped at 300 km is not a route to a 47.9 Mm seam instant, so watch mode is disqualified and the co-location design still has to be built. What V4 measured on the way to that verdict is still useful to it: at the ghost's parked Duna tail the range term failed with a printed number - `GuardSkip reason=hidden-by-zone_distance=643913m`, i.e. 643.9 km on a shared ~1,038 km-radius orbit (this fixture's parked DD1 sits at alt 718,363 m over Duna: SMA 1,038,214.95, ecc 0.00127) whose maximum separation is ~2,077 km. A co-phased observer is therefore ~2x away from a granted watch at the ARRIVAL TAIL (not at the seam), which is the one place a phase-controlled fixture could buy watchability cheaply. V4 also QUALIFIED the zone arithmetic above: its one `hidden-by-zone` sample at the arrival-seam bracket read `distance=869479m` - 7.2x the 120 km gate, not the ~400x the 47.9 Mm SOI-radius argument predicts. ONE candidate for that survives and is worth resolving before the co-location design is sized: the icon driver's replay window starts AT the seam (`bounds=[28781184.5,28813228.5]`), so a pre-bounds ghost resolves to a clamped in-bounds position near Duna rather than to the SOI edge. The other candidate raised at the time - "the hide line prints `renderDistance`, cached separately from the `lastDistance` the watch conjunction reads" - is REFUTED: `ResolvePlaybackDistance` and `ResolvePlaybackActiveVesselDistance` (`GhostPlaybackEngine.cs:5693-5727`) are textually identical on the production path (the resolver overrides exist only for InGameTests) and a single `CachePlaybackDistances` call writes both fields immediately before the hide emit, so renderDistance == lastDistance in production - which STRENGTHENS the 643.9 km reading above, since `IsGhostWithinVisualRange` provably compared 643,913 against 300,000. CONSTRAINT (b) - the center-to-center periapsis structural doubt - REMAINS OPEN and is a DESIGN question; the faithful-mode SOI-sphere seam states that doubt needs are exactly what the completed companion flight will measure, so option 3 stays sequenced BEHIND that flight. Do NOT attempt option 3 until both are in hand.

**THE CO-LOCATION DECISION (2026-09-07, v3c-seam-instant; full record in `autotest-roadmap.md`, section "The re-aim seam instant (V3C): the co-location decision"): NEITHER named option, because the premise was wrong.** Re-reading the V3C run-3 attempt-1 log (`2026-08-08_0747`, collected as `logs/2026-08-08_1102_V3C-flight-arrival-companion`) frame by frame shows (1) the "co-flight corridor" never existed - every `[ReaimSeam] dist[]` sample sits inside the 5 s window of a `beyond visual range ... but exempt during warp (orbital ghost)` line, with its nearest `engine-frame-iter` reading `zone=Beyond`, at 55.9-163.6 Mm render distance, so the trace fired under the #171 warp exemption (`GhostPlaybackLogic.ShouldApplyWarpZoneHideExemption`: Beyond zone, warp > 1x, orbit segments), and the 120 km zone gate binds ONLY at 1x; (2) the arrival seam (28,781,184.52, confirmed by the run's own icon bounds) was crossed at 100,000x (UT slope 99,820 game-s per real second; the Checkpoint rate-change lines lag) with the loop member skipped as GuardSkip `unit-member-warp-hidden` - the flight-view warp hide above `WarpThresholds.GhostHide` (50x), `suppressGhosts = !ctx.mapViewEnabled && warp > 50x` - which returns before `PositionFromOrbit` while the member still had a state (`engine-frame-iter hs=T [out:vis=F zone=Beyond]` through the seam second), so neither `[ReaimSeam]` nor `TrackLoopSeamTeleport` (both inside `PositionFromOrbit`) could run; (3) `MapView.MapIsEnabled` makes that gate false at any warp (#290), so a flight-scene observer with the map OPEN under rails warp keeps the ghost positioned through the seam at any distance. So: the "parked observer can NEVER drive the seam surface" claim above holds at 1x only (V3F/V3R dwelt the map camera at 1x; the `dwellRampFactors` rails stair V1/V24W declare would lift it there too, no product change), the co-departure option needs ~13 s launch timing and ~0.03 m/s velocity agreement (two orders of magnitude past the machine's 1,500 s margin and 2.0 m/s burn cutoff), and a zone-relax aid would have changed nothing at the observed seam frame. V3C now opens the map before its mission phase and REQUIRES the `[ReaimSeam] SEAM member=0 seg#A->B body=Duna` line (anti-vacuity); the Tier-C raise is predicted iff the seam frame rides <= 10,000x (one 50 Hz tick = 200 s, threshold ~17.7 Mm vs the ~47 Mm jump; ~177 Mm at 100,000x, the rate every crossing on record rode). The seam-instant measurement this entry's validation clause has waited on since 2026-06-15 is therefore one flight away, with no code change. **FLOWN THE SAME EVENING, THE SEAM IS MEASURED (2026-09-07):** V3C reading run `2026-09-07_1906` (PASS attempt 1) logged `[ReaimSeam] SEAM member=0 seg#3->4 body=Duna jump=47431188m` - a 47.43 Mm transform discontinuity at the Sun->Duna handoff, 0.990 x Duna's SOI radius - and the armed re-flight `2026-09-07_1932` repeated it at 47,523,736 m (0.992 x SOI); the negative control `2026-09-07_1955` (PARSEK-FAIL(expectation)) proved the defect-class pin discriminates. This is the 2026-06-15 teleport reproduced unattended for the first time, on the FLIGHT-scene engine path, with the map open. The Tier-C `loop-seam-teleport` instrument stayed silent on both flights for the rate reason its threshold encodes (the seam frame rode 100,000x, threshold ~177 Mm), so the rate-independent measurement is the SEAM line's `jump=`, which V3C now REQUIRES in the defect class (>= 1,000 km): the lane is the GS-3 regression target that reds when the option-3 fix lands, at which point its token is inverted to the healthy class (`jump=[0-9]{1,6}m`, the 7-10 km S1.8 seams). The validation clause's companion-flight measurement is therefore IN HAND; constraint (b) above is unchanged by it.

**THE INSTRUMENT'S FIRST RAISE, 2026-08-11 (branch `eve-loop-lanes`, V8-eve-player-loop on the new `eve-orbit-recorded` fixture) - and it reached this defect class through a route nobody predicted.** The `seam-endpoint-outside-soi` lens raised for the first time in its existence, reproduced on all five bracketed runs (`2026-08-11_0807`/`_0810`/`_0814`/`_0818`/`_0819`) with every DISCRIMINATING field identical (ratio=4.6216, endpointDist=393342797m, soi=85109365m, seamUT=15673183.4, seed=no-seed, rendOrbit) - the one field that varies run-to-run is the `loopShift=` context (14,686,847.5-14,687,035.5, a 188 s spread; do NOT grep the full line as a verbatim marker). The _0807 line: `reason=seam-endpoint-outside-soi fromBody=Sun toBody=Eve seamUT=15673183.4 endpointDist=393342797m soi=85109365m ratio=4.6216 tol=1.0050 recordedSeamUT=15673183.4 clock=raw seed=no-seed loopShift=14687035.5 rendOrbit=[sma=11769252918 ecc=0.1552 body=Sun] lineActive=True`. THE ROUTE: the unit is ENGAGED (re-aim supported, Kerbin->Eve via Sun) but the cycle-0 window's synth DECLINED - the plane-tilt achievability gate refused ALL 27 tof candidates (`tilt-correction ... incAch=1.2358 targetInc=2.1000 tol=0.50 state=declined reason=unreachable-plane` x27; resolver verdict `synth failed across 27 tof candidates (recordedTof=3743340.79 geomTof=3679663.04 ...) (tilt correction: unreachable target plane ...) - faithful this window`) - so the window fell back to FAITHFUL and the recorded transfer, rendered one synodic late, misses the moved Eve by 4.62 SOI radii. Three things this measurement settles or reframes. (1) The instrument's first promotion blocker ("the raise has never fired; its detection path is unproven") is DISCHARGED - the detection path works, five-for-five. (2) Caveat (2) above ("a FAITHFUL loop replay raises it by design ... read seed=/loopShift= before calling a raise a defect") needs a sharper edge than it was written with: this faithful render was NOT user-chosen - `forceFaithfulLoopPlayback` is off, the mission is ENGAGED, and the degradation to a faithful window is silent per-window fallback. The player-facing outcome is exactly this entry's symptom (the looped ghost's transfer dead-ends in open space, no encounter into the destination SOI), so the "benign faithful population" and the defect population are not disjoint: a tilt-declined window puts an engaged mission INTO the missing-encounter class. Whether the right fix is a better plane treatment in the synth (so Eve windows stop declining), an explicit surfaced mode indicator, or option 3 remains a design call - NOT attempted here per this entry's own preconditions. (3) Eve's 2.1-deg inclination is the population `ReaimTransferSynthesizer`'s comments bracket between Duna's always-safe 0.06 and Moho's failing 7 - the decline is now MEASURED at one departure geometry (incAch constant 1.2358 across all 27 candidates). Whether other Eve windows (other k, other D0 phases) decline too is unmeasured; the lane observes cycle 0 only. INSTRUMENT CAVEAT measured alongside: the census line and the raise are sampled on different frames, and the 5 s shared-key rate limiter means the surviving `seam-endpoint summary evaluated=1 outsideSoi=0` line can coexist with a raise in the same run - count raises off the `phase=Anomaly` line, never off the census, when both appear.

**DIAGNOSIS SETTLED, 2026-08-11 (branch `reaim-inclined-targets`, Phase 0 - tests + docs, no behavior change).** The first raise above is now understood end to end, and the arithmetic behind it is pinned in-repo rather than read off a log. (1) THE GATE CANNOT SEE r2. Both inputs to the achievability gate are candidate-invariant: r1 is fixed at departureUT, and `nTarget = normalize(r2 x v2Target)` is the target orbit's PLANE normal, which a Kepler orbit carries unchanged wherever the target sits on it. So `incAch` is forced to be identical for all 27 tof candidates - the logged `incAch CONSTANT at 1.2358` is arithmetic, not a numerical curiosity, and no widening of the tof search can ever rescue this window. (2) THE TILT IS LOAD-BEARING FOR EVE, NOT ERROR. `UvLambert` returns v1 in span(r1, r2), so the solved conic's plane IS plane(r1, r2) - the only plane a single center-to-center conic through both endpoints can use - and at this window it exceeds the 2.6-deg bound for every candidate. Eve is ~271 Mm out of the departure plane at arrival, so the flatten the correction would have applied misses Eve by ~288 Mm = 3.4x its 85.1 Mm SOI (modelled 270.7-289.6 Mm across the band). The recorded flight reached Eve by a MID-COURSE PLANE CHANGE (fixture Sun legs: 0.0021 deg flat, then 2.0627 on Eve's own plane, then 2.0689 on approach), which no single conic can imitate. (3) THEREFORE THE DEFECT IS THE DISPOSITION, NOT THE GATE ARITHMETIC. As a correction-safety question the gate answered correctly (flattening here would miss); what is wrong is that "cannot safely correct" is implemented as "kill the candidate", when the un-corrected conic already in hand is sane, prograde, and passes through Eve's center at arrival by construction. FIX PLAN: `docs/dev/done/plans/reaim-inclined-target-tilt-retention.md` (Design A, tilt retention - skip the correction, keep the conic, let the downstream encounter check remain the arbiter). Phase 0 landed the measurement as headless cells: `ReaimTransferSynthesizerTests` (E1: incAch = 1.2358 and invariant across all 27 candidates, the gate refusing every one, the flatten miss > 3x SOI, plane(r1,r2) inc > bound for every candidate, transfer angle 174.99 deg, and - the strongest of them - the modelled per-candidate transfer-plane inclinations reproducing run `2026-08-11_0818`'s 27-line `inc-before` sequence term by term to four decimals, 3.3128-87.4793, in log order; E3: the fixture's broken-plane Sun chain, which kills Design D and scopes Design C out) and `UvLambertTests` (E2: the solve converges prograde and reaches r2 at step 0 and both band edges - there is no solver defect here). Nothing about the product changed in Phase 0.

**THE BENIGN POPULATION MEASURED THE SAME DAY (V8F-eve-loop-faithful, runs `2026-08-11_0853`/`_0854`/`_0857`) - and it settles the gating question in the negative.** The deliberate-faithful A/B lane (forceFaithfulLoopPlayback on, same fixture) raises FIVE times per run, reproducing to four decimals: four Sun->Eve arrival seams - one per self-overlap instance of the forced-faithful unit (overlapCadence = span/20; the ENGAGED unit does not overlap), ratios 52.6957 / 47.5095 / 138.2108 / 203.2006, growing with each instance's later clock - plus a Kerbin->Mun TRANSIT seam at 4.8024: a moon encounter recorded inside the span does not recur at shifted epochs, a benign shape caveat (2) had not catalogued. V8F pins `outsideSoi=[1-9]` as REQUIRED - the first lane where a raise is the designed reading. THE CALIBRATION FACT: the benign ratios (4.80-203.2) STRADDLE the defect reading (4.6216 above), so ratio alone CANNOT separate the classes - any future promotion of this token must discriminate on unit mode / seed provenance (engaged-with-declined-window vs deliberate-faithful vs re-aimed), which the raise line today carries only as context fields. Recorded on the `hlib.ANOMALY_REASONS_RAISED_UNGATED` blocker prose in the same commit.

**PHASE 1 LANDED, 2026-08-11 (branch `reaim-inclined-targets`) - the disposition is now RETAIN, and the V8 lane is expected to red until Phase 3 re-pins it.** `ReaimTransferSynthesizer.TrySynthesizeTransfer`'s tilt seam routes through a new pure `DecideTiltDisposition(incBefore, bound, planeGateSafe)` returning `Noop | Fire | Retain`. The unreachable-plane arm - previously `return false`, which killed the candidate and (when every candidate died) dropped the whole window to the faithful recorded transfer - now RETAINS: the correction is skipped, the un-corrected conic is KEPT, and control falls through to exactly the validation a Noop conic takes (the sane + direction guards already run upstream, then `CalculatePatch` + the proximity encounter check). The encounter check remains the arbiter - a retained conic that never enters the target SOI declines exactly as it did before - and no downstream guard was bypassed or reordered. There is deliberately NO near-90 special case: the measured Eve band tops out at 87.4793 deg, ~2.5 deg under the line where `IsRetrogradeTransfer` flips, and a candidate that crosses it is declined by the unchanged DIRECTION guard upstream (`IsExcessiveTiltTransfer`'s `inc <= 90` clause makes the tilt seam stand down entirely past 90, so a retain can never smuggle a retrograde conic through). NEW LOG LINE, the existing field grammar with a new state token: `tilt-correction inc-before=... bound=... targetInc=... incAch=... inc-after=NaN state=retained reason=unreachable-plane`; the `fired` / `noop` / `declined` emissions are byte-identical to before. COUNTERS: new `RetainedTiltCount`; `UnreachablePlaneDeclineCount` KEPT and still incremented on this branch, with its meaning WIDENED from "unreachable-plane declines" to "unreachable-plane GATE HITS, retained or declined" - renaming it or stopping the increment would silently change what the in-game Duna NEVER-UNREACHABLE invariant (`ReaimEndToEndInGameTest.cs:~384-417 (the Duna NEVER-UNREACHABLE invariant; anchor updated post-review)`) measures, and that invariant still relies on it staying ZERO for Duna, whose gate passes at every r1 phase so the retain arm is unreachable for it by arithmetic. `DeclinedCorrectionCount` is NOT incremented on a retain (a retain is not a decline; the candidate survives). ALSO SHIPPED (supervisor disposition on the plan's open question 1): a new grep-stable Warn at the resolver's all-candidates-failed site, `reaim window fell back faithful: member=<id> window=<k> candidates=<n> reason=<failReason>`, ADJACENT to the existing Verbose `... - faithful this window` line, whose text and level are untouched (lane contracts pin it) - this fix removes the MEASURED route into the faithful fallback, not the class (Lambert non-convergence and the other fail-closed branches still reach it), so the remainder is now loud instead of inferred from a Verbose line nobody grepped. **THE V8 LANE WILL RED ON THE NEXT FLIGHT OF THIS DLL, BY DESIGN:** its armed revision pins the OLD failure trio (`tilt correction: unreachable target plane`, `faithful this window`, `reason=seam-endpoint-outside-soi fromBody=Sun toBody=Eve`) as REQUIRED tokens - which is exactly the GS-3-style regression floor doing its job, forcing a re-read when the disposition changes. The recorded next steps are the plan's Phase 2 (in-game cells + BATCH tally re-pin) and Phase 3 (V8/V8T re-pin choreography: reading -> armed -> negative control; V8F unchanged, since forced-faithful bypasses the synth). Do NOT read that red as a defect without checking this note first.

**LANE-PROVEN, 2026-08-11 (branch `reaim-inclined-targets`, Phase 3 of the tilt-retention plan).** The fix's route into this entry's defect class is CLOSED BY MEASUREMENT at lane grade: V8-eve-player-loop's regression floor red BY DESIGN on the fixed DLL (run `_1242`: exactly the three pre-fix required tokens mismatched, nothing else) and the same log measured the healthy state - `re-aimed transfer ready` with `devFromRecorded=0s` (the step-0 candidate, i.e. the recorded tof, won), `tilt-correction ... state=retained` at the modeled inc-before=18.5369, and the seam-endpoint census flipped from the ratio=4.6216 raise to `evaluated=1 outsideSoi=0` - the replayed Kerbin->Eve transfer now arrives inside the sphere at the recorded seam instant. The lane is re-pinned to REQUIRE that healthy state with the old failure trio + the new fallback Warn inverted into forbidden (readings `_1244`/`_1245`, control `_1246`), V8T re-pinned to the now-genuinely `reaimed=True` TS chain (`_1247` baseline red, `_1252`/`_1253` green), and V8F measured byte-identical (`_1250`) - the forced-faithful population, including its five benign raises, is untouched. THE REMAINDER OF THIS ENTRY STAYS OPEN: the Duna 1.027/1.043 SOI-handoff endpoint residual is orthogonal to the tilt disposition (option 3 territory; precondition (b) still open), and the deliberate-faithful benign population still raises by design.

## TS-LOADGAME-RECORDING-ACTIVE-RACE - the scene-entry recorder re-arms after a StopRecording/DiscardTree pair and REJECTS the next `LoadGame` (SECOND SIGHTING 2026-08-09; V5's re-kill mitigation narrows the window, it does not close it)

Re-checked 2026-10-24 on main b26cdc0 (release-readiness pass): both producers are fixed in code.
HARNESS-BOOT-DISCARD-RACES-COMMITTED-SPAWNED-RESTORE (2026-10-03) makes the seam `DiscardTree` arm
`SuppressCommittedSpawnedRestoreForActiveVessel`, which blocks V7T's re-arm
(`ResumeCommittedActiveRecording` after the second `DiscardTree`);
LOADGAME-COMPLETES-BEFORE-ONFLIGHTREADY (2026-10-09) completes a FLIGHT `LoadGame` only after
onFlightReady and the unpack, which removes V5's case. No V5 / V6T / V7T / V8T / V18T flight is
recorded since 2026-10-09; close on the next green TS lane. Harness only, not a release item.

**What happens.** A `seam`-driver spec that re-enters a second scene mid-run must kill the live recorder first - `TestCommandDispatcher` refuses `LoadGame` with `msg=recording-active` by design, so the load never silently discards a live recording (the ONE exception, added 2026-09-09, is RF-3/A1's `allowLiveRecorder=refly`, which admits the load only while a re-fly session marker is live). The TS lanes therefore issue `StopRecording` + `DiscardTree` immediately before the load. On some scene-entry orderings a scene-entry recorder RE-ARMS after that pair and before the load lands, and the load is rejected. The run is driver-INVALID: the second half of the declared sequence never executes.

**Sighting on V18T, 2026-09-22 (`2026-09-22_1832` attempt 1).** Step 15 `LoadGame` REJECTED, log `reject id=0016 cmd=LoadGame reason=recording-active`, after the step-13/14 `StopRecording` + `DiscardTree` pair both answered OK; attempt 2 (`_1834_a2`) executed the same step OK. Recorded, not re-diagnosed.

**Sighting 1 (2026-08-08, V5-ts-loop-arrival run 1 attempt 1).** Measured: promotion-recorder start 50.294 -> our StopRecording 50.599 -> a SECOND start 50.842 -> `reject LoadGame reason=recording-active`. Attempt 2 on the identical spec saw no second start. The mitigation adopted then was a re-kill pair placed immediately before the load, described in V5's write-up (and inherited verbatim into V6T / V7T) as "the only placement that makes the outcome deterministic".

**Sighting 2 (2026-08-09, V7T-minmus-ts-arrival, seam-endpoint census run `2026-08-09_1020` attempt 1) - AND IT HAD THE RE-KILL PAIR.** Artifacts, all committed: `harness/results/2026-08-09_1020_V7T-minmus-ts-arrival.json` reads `verdict=INVALID`, `subkind=driver-verdict-mismatch`, wall 45 s, `driverValidity {status: FAIL}`, driver step `{id: 0013, cmd: LoadGame, expect: OK, verdict: REJECTED, met: false}` with every other step met; `harness/results/2026-08-09_102005_harness.log` reads `[Classify] verdict=INVALID ... reason=driver stage failed` then `[Retry] ... attempt=2 reason=INVALID`. The timeline in `harness/results/2026-08-09_1020_V7T-minmus-ts-arrival_shots/KSP.log` is V5's mechanism verbatim, one step further along:

```
13:20:39.913  exec id=0011 cmd=StopRecording  verdict=OK      <- the re-kill pair
13:20:40.171  exec id=0012 cmd=DiscardTree    verdict=OK
13:20:40.179  [Flight] ResumeCommittedActiveRecording: resumed committed recording ... pid=2708531065
13:20:40.182  [PhysicsPatch] Active recorder attached
13:20:40.406  [TestCommands] reject id=0013 cmd=LoadGame reason=recording-active
```

227 ms between the pair's `DiscardTree` and the re-arm. Attempt 2 executed the same step OK on the same fixture and same spec. **So the re-kill pair narrows the window; it does not close it, and "deterministic" is retracted.** Note the re-arm reason differs from V5's: this one is `ResumeCommittedActiveRecording` (the committed-tree resume path), not the fresh scene-entry promotion - the same outcome from an adjacent producer, which is a hint about where a real fix belongs.

**Why this is filed rather than folded into V7T's finding.** V6T's spec header states the contract explicitly: a recurrence on this fixture "is a corroboration of V5's finding, to be recorded, not re-diagnosed". The record nearly did not happen - the census write-up characterised all three V7T attempts as `PARSEK-FAIL(anomaly)` on `icon-off-orbit`, which is the harness's own mission-vs-Parsek orthogonality inverted: a driver-INVALID has to surface AS a driver flake, or the next stage-13 rejection reads as new. `harness/coverage/flake.json` carries the consequence the prose could not explain - `V7T-minmus-ts-arrival {numerator: 1, quarantined: true, rate: 0.25, total: 4}` (the numerator is INVALID + KILLED).

**Impact and non-impact.** No product behavior is wrong here in the sense of a wrong render or a wrong ledger - the dispatcher's refusal is a deliberate guard doing its job. What is wrong is that a recorder re-arms unbidden inside a window a spec explicitly cleared, which makes any TS-re-entry lane nondeterministic at ~1-in-4 on the evidence so far. It is also material to a deferred decision: V7T's arming is deferred until the lane "can go green on its own terms", and this is evidence the lane cannot reliably reach its anomaly evaluation at all.

**Not yet established** (do not re-diagnose from scratch next time - extend this list): whether the two re-arm producers share a trigger; whether the window is bounded by a frame count or by scene-settle timing; and whether a dispatcher-side fix (retry `LoadGame` once on `recording-active`, or have `DiscardTree` set a short re-arm inhibit) is preferable to a producer-side one. Nothing here should be treated as a spec-authoring problem: three specs already do the documented mitigation and one of them still lost.

**Sighting 3 (2026-08-11, V8T-eve-ts-arrival reading run `2026-08-11_0835` attempt 1, branch `eve-loop-lanes`) - a THIRD lane, same shape, mitigation present.** The Eve TS lane's first outing died identically: every step met through the re-kill pair, then the TS `LoadGame` (step 13) `verdict=REJECTED`, run `INVALID(driver-verdict-mismatch)`, retry-once absorbed it (`_0836` attempt 2 PASS, every step met, clean sweep). Artifacts: `harness/results/2026-08-11_0835_V8T-eve-ts-arrival.json` + the collected `logs/2026-08-11_1136_V8T-eve-ts-arrival/` folder. This corroborates the ~1-in-4 nondeterminism estimate on a THIRD fixture (eve-orbit-recorded) and a third spec carrying the documented mitigation; recorded per V6T's contract (a corroboration, not a re-diagnosis).

## TODO - D11 `loiter-compression` is UNCOVERED and ~~CANNOT be covered by any committed fixture~~ NOW REACHABLE (first non-zero measurement 2026-08-11, branch `eve-loop-lanes`; the cell itself stays UNCLAIMED until a lane pins a cut token)

**SUPERSEDING MEASUREMENT (2026-08-11, branch `eve-loop-lanes`).** The
`eve-orbit-recorded` fixture (harvested from B16-eve-orbit's re-fly
`2026-08-11_0718`) is exactly the launch-side-loiter shape option (a) below
describes - B15/B16 never adopted `padAlignEjection`, so the recorded flight
waits out the Kerbin->Eve ejection window in LKO for 11,827,993 game-s - and
arming a loop on it produced the corpus's FIRST non-zero cut, on every one of
six V8-eve-player-loop runs: `loiterCuts=1 cutSeconds=11819849` with
`cut#0 start=2492 len=11819849 end=11822341` and
`compressedSpan=3944147/15763996` (first: run `2026-08-11_0802`). Option (a)'s
honestly-stated autowarp cost was paid INSIDE the B16 mission itself (MechJeb's
NodeExecutor autowarp carries the wait inside B16's measured 1,843 s wall), so
the fixture bought the loiter without any new flight shape. The cell is still
NOT claimed: per CLAIM-IS-NOT-GATE it belongs to the V8 arming commit that pins
a cut token, and `dest-trim` (option (b)) remains unexercised - only the
launch-side cut has ever fired. The paragraphs below are the original
2026-08-09 analysis, kept because its structural halves (the moons' phase-lock
unreachability, the duna fixture's sub-one-rev park, option (b)'s two gaps,
option (c)'s empty-cut) remain true and load-bearing.

## Post-cutover map/TS render backlog (next version)

The map/TS render cutover is COMPLETE (see the DONE entry above): the modular Director pipeline is the single render path; this file has no open map/TS render bugs (the render entries above are all RESOLVED/CLOSED). This is the consolidated list of what remains in this area for a future version. NOTHING here blocks the current release.

**1. Re-aim destination phase-lock for looped INTERPLANETARY missions (the one substantial piece).** Re-aim aligns the launch body but not the destination body's rotation/phase across the loop shift, so a looped interplanetary arrival drifts. "Duna One" is closed; the generalization (non-synchronous moons, destination loiter, 2+ moons, atmo-direct entry) is the deferred Phase 4 - see the "DUNA ONE CLOSED ... Phase-4 GENERALIZATION still deferred" entry immediately below. WARNING: this sits on the re-aim seam, a known high-cost area - build the failing multi-moon test and measure before any knob math, and treat "the faithful render is good enough" as a valid outcome (do not stack speculative fixes on a working baseline).

**2. Robustness / needs-in-game confirms (deferred during the cutover, non-blocking).**
- Tracer second cut: the decision-side inc/LAN/argPe-vs-transform reconciliation layer (the reconciler core exists; see the "In progress ... tracer SECOND CUT" entry at the top of this file).
- Sec 15.1 proto re-seed latency and Sec 15.2 per-scene patched-conic divergence (deferred, need in-game characterization; details in `docs/dev/plans/maprender-rewrite-status.md`).
- Phase 7b make-before-break swap-settle: the proto-vessel swap timing on scene/treatment swaps (a brief reseed window, ~0.5s).
- Tracking-Station render-delay confirm: the 1-2s TS proto-vessel gap fix (the same-body intra-block carry) is believed working; wants a TS playtest to confirm.

**3. Minor polish (low value, all currently suppressed or sub-visual).**
- icon-off-orbit residual ~1-3 deg on looped re-aim (the core ~96.5 deg bug is fixed; this is the leftover).
- no-fresh-seed create-frame transient: 1 frame, the proto is suppressed that frame so it is invisible; the fix touches the re-aim seam, so it was skipped during the closeout.
- polyline-orbit-overlap grace transient: the OrbitLineGrace debounce, cosmetic.
- cosmetic test-name cleanup: a couple of in-game test methods still read "...LiveGate..." after the `mapRenderDirectorDrive` gate was dropped.

**4. Architectural / cleanliness (nice-to-have, not needed for function).**
- Modularize the no-conic fallback as a proper Director "fallback treatment" instead of the kept patch-level path in `GhostOrbitLinePatch` (the icon floor + `ghostsWithSuppressedIcon` + `IsIconSuppressed`). The current kept fallback is correct and working; this is purity, not function.
- Standalone ghost-mod readiness for the render side (the `IPlaybackTrajectory` boundary).

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Status (2026-11-06):** the release layout already fits CKAN (one `Parsek-vX.Y.Z.zip` asset per GitHub
release, `GameData/Parsek/` at the zip root, the KSP-AVC `Parsek.version` inside it), so the netkan is a
`$kref: '#/ckan/github/vl3c/Parsek'` + `$vref: '#/ckan/ksp-avc'` file with no install stanza, depending on
the CKAN ids `Harmony2`, `ClickThroughBlocker` and `ToolbarController`. Packaging cleanups done: the zip
ships `GameData/Parsek/LICENSE.txt`, and Module Manager is no longer listed as a dependency (Parsek ships
no Module Manager patches; the harness profiles still install it for their tech-tree fixtures). The version
file's `URL` now names `releases/latest/download/Parsek.version` (owner pick, 2026-11-06), so each release
uploads that file as a second asset and the netkan `$kref` needs `asset_match` for the zip. Remaining:
validate the netkan against the latest release, then the owner submits it from his account (author
consent) when he picks the moment. Plan: project file `release/ckan-publishing-plan-2026-11-06.md`.

**Priority:** Nice-to-have. Owner is going through the plan step by step (2026-11-06).

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1` section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary `v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen without unpacking the authoritative files first.

Remaining high-value work should stay measurement-gated and follow `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- any further snapshot-side work now has to clear a higher bar: `.prec` and `_ghost.craft` are already roughly equal buckets after compression, and `_vessel.craft` is small, so "focus on snapshots next" only applies if a future corpus shifts the split back toward snapshots
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if a future rebaseline against a larger / more vessel-heavy corpus shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay covered by sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work — measurement-gated guidance for future shrink work rather than active tasks

---

## TODO — Ghost Visuals

### ~~T25. Fairing internal truss structure after jettison~~

Root cause (decompiled KSP 1.12.5 + the stock `AutoTruss.mu` / `fairingSize1.cfg`): the "truss" is not procedural. Every stock fairing base carries a fixed AutoTruss model (`Truss1..6`, `Cap1..6` under a `Hider` transform scaled (0.0005, 0.1, 0.0005)), each piece a stock `ModuleStructuralNode` that `CheckDisplay` shows only when its `visibilityState` (the "Truss Structure" toggle) is on and a part is attached at one of its interstage nodes, and hides again from `DecoupleAction`. The "placeholder 2000x10x2000 scale" read in March was the children's local scale (2000, 10, 2000), which `Hider`'s scale cancels to 1; stock never rescales these objects, so the ghost's mirrored clone was always the right size. The two home-made lattices replaced something that was never broken.

The same investigation found the ghost shell itself was an approximation: a generated cone with the base's texture, a made-up nose added even to open-top interstage fairings, no panel split, no wall thickness, max 24 sides.

**Fix:** `GhostVisualBuilder.Fairings.cs`. (1) Structure: `ParseFairingStructureNodes` reads the stock inputs (config fields from the prefab PART config, `visibilityState` / `spawnState` / `showMesh` and the `attN` rows from the snapshot), `ComputeStructureNodeVisible` transcribes `CheckDisplay`, and `GhostPlaybackLogic.RefreshFairingStructureVisibility` re-applies it against `logicalPartIds` after decouple / destroy events and on spawn / loop reuse (stock keeps the truss shown when the stage leaves from the fairing's child side; the ghost hides it, matching the next recording's snapshot). Also fixes a ghost recorded after jettison showing all twelve pieces. (2) Shell: in FLIGHT the shell is built by stock `ProceduralFairings.FairingPanel.SetupPanelArray` + `Spawn` + `BuildMesh` with the snapshot XSECTIONs and the prefab module as a read-only geometry host (the player's `nArcs` is the panel count argument), with stock's flight materials and variant overrides; the shell meshes are destroyed with the ghost. Outside FLIGHT stock `BuildMesh` skips the nose-cone ring, so the generated cone stays there. Panels still vanish on jettison instead of tumbling away (owner OK, 2026-11-07). No recording data was added.

**Status:** Fixed, pending the snapshot lane flight for visual confirmation.

**Priority:** Low - cosmetic

---

## TODO — Compatibility

### T48. FX-fingerprint A/B: the fixture half is delivered, the A/B itself has never been re-run (filed 2026-08-04 as "coverage is starved by the trajectory-only synthetic corpus", branch `modded-compat-lane`) [**FIXTURE HALF DELIVERED 2026-08-28** by PR #1563 (`part-showcase-lane`): the `part-showcase` preset injects 243 ghost-snapshot-bearing single-part rows - 47 stock engine families + 5 stock RCS, plus 36 install-conditional ReStock+ rows (17 engine / 12 RCS) auto-appended by `IsRestockPlusInstalled` - each built through `VesselSnapshotBuilder.AddPart` + `WithGhostVisualSnapshot`, which is the input `GhostVisualBuilder` feeds to `GhostFxFingerprint.LogEngineInfos` / `LogRcsInfos` right after the engine/RCS FX build. S1.9 ARMED the census at `spawned = { min = 243 }` / `requireBalanced = true`, measured 243/243/0 twice, with `MeshSpawned` gated on an engine (LV-T30) and an RCS (RV-105) row - so the FX build that T48 said could "never happen" demonstrably happens now. **RESIDUAL, AND IT IS THE WHOLE A/B**: no committed spec injects the preset on a modded install - S1.9 is pinned `instanceProfile = "stock-minimal"` and both `MC-1-waterfall-compat` / `MC-2-restock-compat` declare `injectedRecordings = "none"` - so `hlib.diff_fx_fingerprints` has NOT run over a stock-vs-modded pair since 2026-08-04. Also unmeasured: whether the ~30 s one-shot showcase window (the loop flag is stripped at load) leaves enough dwell for every family to emit its line on the modded side]

The first stock-minimal vs modded-compat `[FxFingerprint]` A/B (R14, report-only)
worked mechanically but measured almost nothing: of the 342 recordings the
all-synthetic corpus injects on a modded install, ~330 carry NO vessel snapshot
(`Spawn suppressed: ... no vessel snapshot=330`), so no ghost visual - and no
engine/RCS FX build - can ever happen for them. The engine/RCS "Part Showcase"
rows loaded but did not spawn in the run window, leaving exactly ONE
fingerprinted key per side (`solidBooster.sm.v2` from the Reentry East ghost's
snapshot). That one key already shows the expected divergence (ReStock FX models
`ReStock/FX/restock-fx-srb-{core,smoke}-1` on modded vs the stock prefabs), so
the pipeline is sound - the corpus is the bottleneck. Fix: ~~a dedicated
engine-showcase fixture save whose recordings carry REAL vessel snapshots
(one per engine/RCS part family, positioned inside spawn range of the pad
host)~~ **[DONE 2026-08-28 - the `part-showcase` preset, PR #1563; see the header]**,
then re-run `hlib.diff_fx_fingerprints` over the pair **[STILL OWED - no committed
spec injects the preset on a modded install]**; only after that
is a gate worth discussing. Extraction/diff tooling landed with R14
(`hlib.parse_fx_fingerprint_lines` / `diff_fx_fingerprints` /
`format_fx_fingerprint_diff` + `harness/tools/fx_fingerprint_diff.py`).

### ~~T42b. GUI mirror: what the fidelity instrument measured and did NOT fix~~

**CLOSED 2026-10-31 (owner: the mirror is left as it is).** The five differences below are accepted
page-vs-game limits, none a product defect.

`harness/tools/gui_mirror_fidelity.py` (design: `docs/dev/design-gui-mirror.md` section 11)
photographs the generated mirror page in a headless browser and compares it against the
census PNG inside the Parsek window rects. The classes it found were fixed in the same PR;
these are the ones left, each with the number that says how big it is. None of them is a
defect in the product - they are all differences between the page and the game.

1. **A label that WRAPS in the game is drawn on one line.** KSP's label styles word-wrap and
   the page sets `white-space:pre`, so the Logistics route cell that reads `> Route: KSC` /
   `-> Duna` over two lines in the frame is one line on the page. It shows as a `dy` of
   about 10 px on those rows and nothing else. Fix: wrap where the dump's own rect is more
   than one line tall, which IS derivable (the rect height over the line height) - but it
   moves every multi-line cell on the page, so it wants its own pass with the instrument
   re-run beside it rather than a guess bolted onto this one.
2. **KSP's own font metrics are not Arial's.** The size was calibrated against measured ink
   (13 px Arial renders three corpus runs at 136.6 / 144.5 / 86.8 px against a measured
   136 / 144 / 87) and the corpus width ratio sits at 1.000 at the median, but the tails are
   real: a long single line still ends a character or two early or late. A real fix means
   shipping KSP's font metrics, which is a bigger thing than this page.
3. **A toggle's tick is a CSS checkmark, not KSP's skin texture.** Right state, right box,
   drawn shape. The texture is in no census artifact, so there is nothing to derive it
   from; this is as close as the page gets without shipping the game's atlas.
4. **A control hidden behind another Parsek window is still measured.** The dump records
   every window's controls, including the ones another window covers, and neither the page
   nor the game publishes its stacking order. Of the 222 measured captures, 170 carry more
   than one Parsek window and 61 have two whose rects actually OVERLAP; 46 of the 115
   remaining frame-only controls (40%) and 32 of the 63 remaining clipped runs (51%) sit
   fully inside two window rects at once, which makes them readings about which window won
   rather than about the rendering. (The counting rule matters and is worth stating: a
   control counts when its rect is fully contained in two or more window rects. A looser
   rule - the rect INTERSECTS a second window - gives a slightly higher clipped count, 34
   rather than 32, and the difference is entirely controls that straddle an edge.) Fix:
   record which root a control came from and skip it when a later root covers it.
5. **A capture with a stock modal is not measured at all** (6 of 230). A `PopupDialog` is a
   centred uGUI canvas that overdraws the window rects in the FRAME and appears in no
   control tree, so every rect under it would read as a difference the page could not have
   avoided. The page shows the photograph for those, which is the honest rendering.

**RE-MEASURED 2026-09-22 over the wave-5 corpus** (32 shots dirs, 314 captures,
305 measured, 9 skipped; the earlier reading was 19 dirs and 222 measured).
Nothing above was fixed and nothing regressed: text ink `dx` p50 / p95 held at
2 / 5 px, `dy` at 1 / 4, width ratio at 1.000 / 1.111, fill delta p95 at 0,
every slider thumb still resolves on both sides at a p50 offset of 1 px, and the
window luminance score p50 moved 9.04 -> 9.04. The per-capture rates of the
residual are flat too (frame-only 0.52 -> 0.57 per capture, clipped 0.28 ->
0.30), which is the eleven new lanes photographing states nothing had measured
before rather than the page getting worse.

Three NEW tails the wave-5 lanes brought in, each a handful of controls and each
worth a look before the next pass quotes a worst-case number:

6. **`button|button|text` worst `dx` 85 px and worst width ratio 195.** One
   control out of 3882 in that class, whose p50 is 2 px and p95 5 px. A ratio of
   195 is an ink box the measurement found almost none of on the frame side, so
   read it as a measurement artifact until it is located; the class is otherwise
   the healthiest in the corpus.
7. **`slider|horizontalscrollbar|notext` worst thumb-run delta 336 px**, over 4
   nodes. A HORIZONTAL scroll bar is new to the corpus - 130 of the 142 sliders
   the earlier reading measured were vertical - so `slider_thumb_run`'s
   orientation handling has its first real horizontal cases and one of them
   resolves a run the frame does not.
8. **`layoutgroup|box|notext` worst fill delta 34**, over 426 nodes with a p95 of
   0. A container whose sampled colour the page paints differently than the frame
   in one place, which is the class the container-sampling rule exists for and
   worth confirming is not that rule slipping.

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort
