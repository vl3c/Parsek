# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.
- `done/todo-and-known-bugs-v5.md` — the v0.9.1 / v0.9.2 cycle: Re-Fly Phase D wrap-up, debris-rendering PR stack through PR 3c and the always-shadow follow-up, Phase 11.5 storage and observability follow-ons, the multi-debris explosion-audio fix, and the carrying-over numbered items #570-#640. Archived 2026-05-10.
- `done/todo-and-known-bugs-v6.md` - the v0.9.2 / v0.9.3 bug-closure wave and the first half of the v0.10.0 cycle: Re-Fly supersede / anchor-propagation / co-bubble-retirement closures, the watch-mode W-cycle + chain-seam fixes, the schema generation-3 reset, the Missions window (tab + looping + periodicity + zero-drift reschedule), re-aim interplanetary transfers, the Map/TS render-tracer MVP (PR #1005), and the debris-rendering / switch-fly auto-record closures. Archived 2026-06-05.
- `done/todo-and-known-bugs-v7.md` - the v0.10.1 / v0.10.2 / v0.10.3 finish-up: logistics milestones M1-M6 (non-KSC origin, mod resources / harvest, pickup, multi-stop / multi-origin / round-trip, inter-body, legibility) + the claw producer; missions M-MIS-1..6 / 8 / 9 and the re-aim / periodicity / phasing solver stack; the Map/TS render rewrite cutover; the career-economy bug wave (BUG-A..H, the records-milestone recalc storm, contract-discard desync); and the ledger ground-truth audit closures. Archived 2026-07-09.

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## B11-CAPTURE-APPROACH-WARP: increase warp from Mun SOI entry up to the circularization node [OPERATOR NOTE 2026-07-30, watching a live V1-map-dwell-mun-orbit flight. TUNING TODO on the SHARED b5 capture machine - not picked up on the `v1-map-dwell` branch]

The stretch from Mun SOI entry to the capture (circularize-at-periapsis) node
runs slower than it needs to. Today capture mode warps TARGET-FLYBY to
`periapsis_ut - CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS` (900 s) off the orbit's own
periapsis clock, then PLAN-CAPTURE plans at `planWarpFactor` (2) and the
executor autowarps the rest; the operator's observation is that the whole
in-SOI approach up to the node should carry a higher warp so the pass reaches
the burn faster. Constraints when picking this up: the machine is SHARED
(B11/B12 fly it nightly, both LIVE-PROVEN, so a profile change owes a
confirmation flight per the standing rule), the periapsis-clock bound exists
because the B12 flight-3 rails stair sailed past the only capture point on the
pass (never re-derive that the altitude-trend way), and MechJeb's own ~600 s 1x
pre-ignition WARPALIGN hold is the executor's, not ours - the tunable stretch
is the coast INTO the lead window plus the plan-phase warp, not the hold.

---

## V1-REPLAY-LINE-BLINK: the replaying flown ghost's proto orbit line blinks off/on under 100x rails warp [FOUND 2026-07-30 by V1-map-dwell-mun-orbit's FIRST dwell (run `2026-07-30_2251` collected log; that mission red on an unrelated over-strict assertion, so the sweep never classified it). REPRODUCED the same day by the SECOND dwell (run `2026-07-30_1955`, collected log `2026-07-30_2322`): mission PASS, every other verifier green, anomaly sweep `hitCounts={line-blink: 2}` `unlistedReasons=[]` -> the scenario's first honest `PARSEK-FAIL(anomaly)`. Two independent replays, two raises each. REPORTED, NOT DIAGNOSED. The first real-geometry finding of the visual-validation program]

### What happens

During the V1 map dwell - the just-flown B11 tree replaying as a ghost after the
rewind, map view open, rails warp at 100x (ramp stair step, `Time warp rate
changed to 100.0x at UT=935.39`) - the flown MAIN recording's proto orbit line
(`recId=ee9ea1f9be6846e1893293594f568e58`, the root "Kerbal X" recording;
`pid=3388325605`) toggled inactive and back twice, and `MapRenderProbe` raised
the gated Tier-C `line-blink` both times:

```
phase=Anomaly surface=ProtoOrbitLine ... frame=87767 currentUT=1329.352 reason=line-blink lineActive=True prevActive=False lastToggleFrame=87763 sinceFrames=4 body=Kerbin
phase=Anomaly surface=ProtoOrbitLine ... frame=87807 currentUT=1996.390 reason=line-blink lineActive=True prevActive=False lastToggleFrame=87799 sinceFrames=8 body=Kerbin
```

The blink windows are 4 and 8 frames at 100x (~50-670 game seconds of replayed
ascent/early-transfer per toggle span), i.e. a visible flicker of the ghost's
orbit line exactly where a human watching the map would see it. The surrounding
Tier-B truth shows the line owned by `director-stockconic-visible` on the
re-activation frames.

DIAGNOSTIC HINT from the second dwell's decision lines: the toggles sit inside
OWNERSHIP HANDOFFS - the orbit-line decision cycles
`director-traced-path-suppress` -> `director-stockconic-visible` ->
`polyline-owns-phase` around the raises, so the first suspect is the
polyline/TracedPath <-> StockConic ownership transition going dark for a few
frames at high warp (phase-boundary handoff lag), not the map-orbit reseed
cadence itself. DETERMINISTIC: exactly 2 raises per dwell across all three
dwells flown 2026-07-30, and nothing else raised (zero unlisted reasons).

### Why this entry exists before a diagnosis

`line-blink` is in the GATED `hlib.ANOMALY_TOKENS` set, so the moment a V1
flight is otherwise green the sweep classifies `PARSEK-FAIL(anomaly)` against
`allowedAnomalies = []`. Per the scenario's own gating discipline that red is a
FINDING to file, not a reason to soften the spec - this entry is the filing.
The open call (it is the known-gate-0 shape): diagnose whether this is the
loop/warp reseed-lag blink class the anomaly taxonomy already names (the
warp-aware reseed exists precisely because high warp outruns the 0.5 s map-orbit
reseed cadence), fix it if it is a real render defect, or - if it proves benign
at bounded frequency - arm a measured `{ token = "line-blink", maxCount = N }`
budget on the V1 spec from green-run `hitCounts` evidence. Do NOT whitelist the
bare token; do NOT set a budget from this single run.

### Evidence

- `logs/2026-07-30_2251_V1-map-dwell-mun-orbit/KSP.log` (the full dwell: 131
  `probe frame summary` lines with `ghosts=1 sampled=1`, 13 `faithful-parity
  summary sampled=1 overTolerance=0` passes, and these 2 raises - the dwell
  itself worked end to end).
- The mission result for that attempt
  (`results/2026-07-30_1917_V1-map-dwell-mun-orbit_mission.json`) is
  MISSION-ASSERT-FAIL on `rewoundBeforeFlightStart` ONLY - a mission-side
  over-strict comparison fixed the same day (the row now carries the
  PlaybackScopeTracker 2.0 s activation tolerance) - with every flight, rewind
  and dwell phase reached and every other assertion met.

---

## ~~A DESTROYED VESSEL'S CREW NEVER GETS AN END STATE: the kerbal-death ledger row commits as `Unknown`, not `Dead`~~ [FOUND 2026-07-30 by the first `CL-2-pod-impact-ledger` flight. FIXED 2026-07-30, branch `crew-endstate-destroyed-gate` (PR #1395); live-proven run `2026-07-30_1830`]

The first flight in the repo that ever reaches the pending-tree AUTO-COMMIT out of a
destroyed crewed flight found that the whole crew-death end-state chain is inert on
that path. It is not a logging gap: the ledger row is genuinely built with the wrong
value, and the reservation semantics that hang off it invert.

### The measurement

Run `2026-07-30_1711_CL-2-pod-impact-ledger`, FULL PASS, the CL-1 profile (crewed pod,
no chute, terminal-velocity impact, Jebediah observed `Dead` on his own roster) plus
`SetSetting autoMerge=true` and `ExitToSpaceCenter`. The commit itself fired correctly:

```
:12611  Silent full-fidelity auto-commit (scene-exit): tree='Jumping Flea' recordings=1 spawnable=0
:12630  Committed tree 'Jumping Flea' (1 recordings). Total committed: 1 recordings, 1 trees
:12646  CreateKerbalAssignmentActions: 1 crew members from '5c0ed4fa57c841d3afd3ead9ab33640b'
:12826  OnSave: saving 1 committed tree(s)
```

But `PopulateCrewEndStates` occurs **ZERO times in the entire log**, and what the
commit then produced for a kerbal who died is a LIVE-crew reservation:

```
:12668  Reservation: 'Jebediah Kerman' endUT=Infinity (Unknown), recording '5c0ed4...'
:12679  PostWalk summary: reservations=1 permanent=0 temporary=1 slots=1 retired=0 slotsCreated=1
:12692  Stand-in generated: 'Dudeny Kerman' (Pilot) for slot 'Jebediah Kerman' depth 0
```

Persisted in the produced save as a `KERBAL_SLOTS` slot with a `CHAIN_ENTRY` and a
`CREW_REPLACEMENTS` entry mapping Jebediah to Dudeny. A dead kerbal acquired a
stand-in.

### Why that is wrong rather than merely surprising

`KerbalsModule.cs`'s own comment states the contract:

```
//   Dead      -> permanent, endUT = infinity
//   Unknown   -> open-ended temporary (conservative)
bool permanent = (endState == KerbalEndState.Dead);
```

and `CrewReservationManager.SeatMatch.cs` (`ShouldProcessCrewForReservation`) EXCLUDES
`Dead` from reservation processing while admitting `Missing`. So the intended outcome
is a PERMANENT reservation and no stand-in; the observed outcome is the temporary
branch, which is what a kerbal still ALIVE aboard a flying vessel gets.

### Root cause

`LedgerOrchestrator.NeedsCrewEndStatePopulation`:

```csharp
return rec != null
    && !rec.CrewEndStatesResolved
    && rec.CrewEndStates == null
    && (rec.VesselSnapshot != null
        || !string.IsNullOrEmpty(rec.EvaCrewName)
        || KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(rec));
```

A DESTROYED vessel has no `VesselSnapshot` (there is no end-of-recording vessel to
snapshot, and the commit line's `spawnable=0` is the same fact), is not an EVA, and is
not a ghost-only chain handoff, so the predicate is false and
`KerbalsModule.PopulateCrewEndStates` is never called. `rec.CrewEndStates` stays null,
and `LedgerOrchestrator.ExtractCrewFromRecording` then defaults each row:

```csharp
KerbalEndState endState = KerbalEndState.Unknown;
if (rec.CrewEndStates != null)
    rec.CrewEndStates.TryGetValue(name, out endState);
```

The irony is that the INFERENCE is correct and would have returned the right answer:
`KerbalsModule.InferCrewEndState` maps `TerminalState.Destroyed` to `Dead` in its first
branch, and the recording carries `terminalState = 4` (Destroyed) on disk. The crew
NAMES are also available - `ExtractCrewFromRecording` finds Jebediah through the
`GhostVisualSnapshot ?? VesselSnapshot` fallback, a fallback
`NeedsCrewEndStatePopulation` does not have. The gate is simply testing the wrong
snapshot.

### Fix (branch `crew-endstate-destroyed-gate`, PR #1395)

The gate now admits `rec.GhostVisualSnapshot != null` as a crew source, matching
the `GhostVisualSnapshot ?? VesselSnapshot` fallback that `PopulateCrewEndStates`
and `ExtractCrewFromRecording` already use internally - but ONLY for terminal
states whose inference does not consult the end-of-recording crew set (Destroyed,
Recovered). A blind admission was tried first and immediately red'd
`CreateKerbalAssignmentActions_GhostOnlyStableChainTip_DoesNotForceRecovered`:
for intact terminal states (Orbiting etc.) with no VesselSnapshot at all,
`InferCrewEndState` reads "absent from the end snapshot" as EVA'd-and-lost Dead
and would falsely kill live crew on ghost-only stable chain tips, so those stay
unresolved as before. The newly-taken path logs a grep-stable line
(`NeedsCrewEndStatePopulation: ... admitted via ghost-visual-only crew source`),
one-shot per recording (`CrewEndStatesResolved` is persisted): Info when the
ghost snapshot carries crew, Verbose for crewless destroyed debris so the first
recalc over a pre-fix save does not flood Info with one line per debris item.
Pre-fix saves self-heal: `MigrateKerbalAssignments` recomputes the old `Unknown`
rows into `Dead` on the next recalc. Behavioral unit coverage in
`LedgerOrchestratorTests` (gate decisions incl. Recovered and crewless-debris
cases, plus the CL-2-shaped destroyed-pod population and ledger-row tests).

Live-proven: run `2026-07-30_1830_CL-2-pod-impact-ledger`, PASS on attempt 1,
ledger oracle 0 hard divergences. The 1711 symptoms inverted exactly: gate line
fired for the destroyed recording, `PopulateCrewEndStates: ... crew=1 aboard=0
dead=1`, `Reservation: 'Jebediah Kerman' endUT=INDEFINITE (Dead)`, zero
`Stand-in generated` lines, produced save carries the KerbalAssignment row with
`endState = 1` (Dead), persisted CREW_END_STATES, and no CREW_REPLACEMENTS.

### Consequences, updated after the fix

- `CL-2-pod-impact-ledger` can now pin `PopulateCrewEndStates ... dead=1` and the
  new gate token; retire
  `test_cl2_crew_loss_ledger.py::test_the_crew_end_state_token_is_deliberately_not_required`
  when doing so (it exists to stop the token being required while the defect was
  live).
- CL stage B (the tombstone half) is UNBLOCKED: `KerbalRecoveryOnSupersedeTest`
  stops auto-skipping once a supersede subtree contains a
  `GameActionType.KerbalAssignment` action with `KerbalEndState.Dead`, which the
  fixed path now writes. Stage B scope: the R12 residue block in
  `docs/dev/autotest-roadmap.md`.
- The magnitude question is separate and still open: nothing in `Source/Parsek/` ever
  CONSTRUCTS a `ReputationPenaltySource.KerbalDeath` action, so the death's reputation
  hit is applied by STOCK (`Added -9.999828 (-10) reputation: 'VesselLoss'.`) and
  absorbed through the generic captured-award path. CL-2's ledger oracle passes with
  0 hard divergences modelling it that way.

---

## `dead-crew-strip` has no pinned definition, so the coverage cell is unfalsifiable [FOUND 2026-07-30 during the CL-2 scope fence. NOT RESOLVED]

`harness/coverage/registry.toml` carries no per-cell definitions beyond the D12 block
comment, which asserts that `dead-crew-strip` and `tombstone-rep-penalty` are "a RE-FLY
consequence of a death that already happened". For `tombstone-rep-penalty` that is
checkable - `SupersedeCommit.TryPairBundledRepPenalty` is the named producer. For
`dead-crew-strip` it is not: the only code in the repo actually NAMED for stripping
dead crew is SPAWN-time, not re-fly-time (`VesselSpawner.cs` "Individual dead crew are
already removed by `RespawnVessel.RemoveDeadCrewFromSnapshot`", and
`ShouldBlockSpawnForDeadCrew`), while the re-fly `Strip` is `PostLoadStripper.Strip`
(`RewindInvoker.cs`), which strips VESSELS, not crew. The nearest re-fly-time CREW
behaviour is `CrewReservationManager.RecomputeAfterTombstones()` plus the
tombstoned-roster cleanup in `SupersedeCommit.CommitTombstones`.

So whoever builds stage B must PIN what the cell means before claiming it, or the claim
is unfalsifiable. The strongest available definition is the one the in-game test that
owns the behaviour states: `InGameTests/KerbalRecoveryOnSupersedeTest` asserts that
after the merge, every eligible kerbal-death action in the supersede subtree is
tombstoned AND each previously-Dead kerbal is back in the roster as `Available` or
`Assigned`. Writing that into the registry comment (or splitting the cell) is a
registry-authoring task, not a flight.

`CL-2-pod-impact-ledger` claims NEITHER cell, and
`test_cl2_crew_loss_ledger.py::test_the_scope_fenced_re_fly_cells_stay_unclaimed` keeps
them - and D8/D9 `tombstones` - out of its claim set.

---

## ~~The ledger oracle's capture cross-check cannot be armed on a FLOWN scenario: the corroboration key is UT-valued~~ [FOUND 2026-07-30 by the first live capture, `CL-2-pod-impact-ledger`. FIXED 2026-07-31, branch `harness-hardening-2`]

### Fix

The windowed-UT option from the list below, chosen over the other two after studying
what a flown spec CAN declare: a manifest entry may now carry `utWindow = [lo, hi]`
(inclusive; `oracle.parse_manifest_entries` -> `ManifestEntry.ut_window`), and
`hlib.unmatched_captured_awards` matches a windowed entry to a captured award whose
UT falls inside the bounds instead of requiring exact `seq_key` equality. A window is
what a flown spec can honestly state - the mission's phase bounds ("the impact lands
between UT 100 and 140") are stable across runs even though the exact UT is not
(119.7 / 119.9 / 119.8 across the three runs below). Everything else about the match
is unchanged: facet + amount-within-tolerance + structured identity + optional
`stockReason`, one-to-one per (entry, pool) consumption, pinned-first candidate order
(within each group, entries additionally sort by acceptance WIDTH - exact key = 0,
window = hi-lo - so neither a window over an exact entry nor a wide window over a
narrow one can greedily strand the award the tighter entry names; the residual
pinned-wide-vs-unpinned-exact greedy limit is documented and pinned as fail-closed).
Fail-closed edges:
a null-UT captured award never window-matches; `lo > hi`, a malformed shape, and
`ut` + `utWindow` on one entry all reject at parse time.

**THE PRE-LAUNCH GATE NOW DELEGATES TO THE ORACLE (2026-07-31, second session).**
It started as a widening: the review filed `ut` and non-table entries as the same
burned-flight economics `utWindow` had already closed, and both were confirmed - six
shapes passing ADMIT and hard-failing post-flight as a manifest-parse-error
`PARSEK-FAIL(ledger)`. The first fix mirrored three ENTRY-SHAPE keys by hand and
justified leaving the rest as "value/semantic rules". **That boundary did not survive
its own review and was replaced.** Running the oracle with `captured=None` - exactly
pre-launch knowledge - rejects EVERY one of its rules deterministically, and two of
the supposedly-semantic ones (`seq` must be an int; `stockReason` a non-empty string
or array of them) are pure type/shape rules CL-2's manifest hand-writes on every
entry. The stated justification was also self-contradicting: it rested on the funds
fill-from-capture rule "genuinely" needing the log, while `oracle.py`'s own comment
says that path is unreachable against a real log.

`validate_ledger_expectations` therefore calls `oracle.parse_manifest_entries`
outright and re-prefixes `entry[N]` to `manifest[N]`, so there is no second
implementation to drift. **The single carve-out** is the funds fill-from-capture
ambiguity, skipped because it reads the captured pool (empty pre-launch), and
raising it would refuse a spec the oracle could accept at run time - the unsafe
direction. Twelve entry shapes now red pre-boot that previously cost a flight, and
an unbounded-integer `ut` (tomllib does not clamp to int64) no longer raises
`OverflowError` out of `validate_spec` - which is called with no `try/except` and
would have aborted the WHOLE batch rather than one spec; `oracle._is_finite_number`
fixes that on both sides at once. `WhatThePreLaunchGateMirrorsTests` sweeps both
implementations against each other in BOTH directions over the full entry-shape
space including the formerly-unmirrored rules, asserts neither side RAISES, pins the
shared reason key with a trailing colon (`.ut` is a substring of `.utWindow`, so a
bare match left the ordering claim unpinned), pins the carve-out explicitly, and
asserts every committed spec still passes. M-B2 independence is
untouched - the window is a matching hint, `compute_expected` never reads it, and a
captured amount is still never summed into EXPECTED. OPT-IN and verdict-neutral by
construction at the time it shipped: no committed spec declared a window; the CL-2
shape - the three measured capture lines corroborating through windows, near-window
misses, straddling awards, the measured UT spread as literals - is pinned in
`test_hlib.py::FlownScenarioUtWindowCorroborationTests`.

**ARMED 2026-07-31, same day, second session.** The operator action was then TAKEN
against the real game on `CL-2-pod-impact-ledger`, one flight per checklist step,
all PASS attempt 1:

```
2026-07-31_1630  baseline, spec UNCHANGED   3 awards UNEXPECTED (report-only)
                                            ut 12.5 / 19.1 / 119.9
2026-07-31_1638  utWindows declared, report ZERO unexpected; reportOnly 3 -> 0
2026-07-31_1645  captureCrossCheck = gate   PASS, hardDivergences=0 reportOnly=0
2026-07-31_1759  bounds corrected + re-flown PASS, crossCheck=gate archived,
                                            awards at ut 12.8 / 19.3 / 120.2
```

The windows are the mission's PHASE BOUNDS - `[0, 100]` for the two ascent
`Progression` awards, `[100, 400]` for the `VesselLoss` impact - and these three
flights are the argument for bounds over pins: the second `Progression` measured
19.1, then 19.0, then 19.1 across them. THE BOUNDS ARE ABSOLUTE PLANETARIUM UT, not
time-from-ignition: the matched value is `fixtureUT(9.06) + padDwell + T+`, and pad
dwell is wall-clock (game time runs 1:1, no warp), so a ceiling has to clear the
harness's own kRPC connect budget. The first draft's `[100, 140]` left ~20 s against
a documented 30 s budget - a latent false `PARSEK-FAIL(ledger)` that `should_retry`
would not have absorbed. Corrected before merge, and the widening was verified to
cost no discrimination: an extra `VesselLoss` at ut 150 and an out-of-phase
`Progression` both still red. TWO LIMITS ARMING DOES NOT CLOSE, recorded in the spec:
the check is one-directional (a capture yielding ZERO awards is unmatched-empty and
GREEN - now closed at the log level by two required stock award lines), and the two
`Progression` awards can collapse in `dedupe_captured_awards` if no UT-stamped
[Parsek] line separates them, after which one entry corroborates nothing. The expected totals did NOT move
(`funds=529600.0 science=100.0 rep=-7.999829000000001` before and after): CL-2's
entries are all `ut`-less so `_sort_key` ordering is untouched, and all three rep
entries are `repMode="applied"` so the nonlinear curve is not re-entered. The funds
milestone entry stays window-free (KSP logs no funds award, so it is never
capture-matched). The two whole-set cells became explicit ALLOWLISTS naming CL-2 with
that evidence (`test_only_the_armed_allowlist_arms_the_capture_cross_check`,
`test_only_the_armed_allowlist_declares_a_ut_window`), so a SECOND spec arming the
gate still reds until its own evidence is recorded. CL-2 is the first committed spec
in the suite to gate on the ledger capture.

`hlib`'s own note says `captureCrossCheck` "was WRITTEN as a hard gate, but it has never
once run with a working capture", that no L1 spec can capture anything, and that arming
"wants a REPUTATION-producing scenario". `CL-2-pod-impact-ledger` is that scenario and
is the first run in the repo with a LIVE capture. What it measured is that the
documented escalation path (fly it green, read `capturedRaw`, arm `gate`) does not
close for a flown scenario.

### The measurement

Runs `2026-07-30_1711` and `2026-07-30_1721_CL-2-pod-impact-ledger`, identical:
`manifest-capture stockLines=3 deduped=3 seamDeclared=4 seamRejected=0`. All three
stock reputation lines were captured cleanly. All three then reported UNEXPECTED
(report-only, `hard=False`) even though the spec's manifest declares all three at the
right amounts with the right `stockReason` keys:

```
manifest-capture: unexpected stock award ut=12.5  kind=stock-reputation-award reason=Progression
manifest-capture: unexpected stock award ut=19.0  kind=stock-reputation-award reason=Progression
manifest-capture: unexpected stock award ut=119.9 kind=stock-reputation-award reason=VesselLoss
```

### Cause

`hlib.unmatched_captured_awards` joins on `(seq_key, facet, amount)`, and `seq_key` is
`("ut", <ut>)` whenever the entry has a UT, else `("ord", <seq>)`. A captured award gets
its UT from the neighbouring `[Parsek]` line (12.5 / 19.0 / 119.9 here); a spec-declared
entry has no UT unless the author writes one. `("ord", 0)` never equals `("ut", 12.5)`,
so the join can never fire.

The only way to make it fire from a spec is to declare the exact game UT the award will
land on - which for a FLOWN scenario means pinning a golden trajectory value. It is also
genuinely unstable: the same impact was ut 119.7 on the archived B1 run of this exact
craft, 119.9 on CL-2 flight 1 and 119.8 on flight 2. (For a KSC-only scenario the awards
land at the save's static UT, so the problem is invisible there - consistent with the
join having been designed against the L1 shape and never exercised against a flight.)

### Options (windowed-UT applied - see Fix above)

Loosen the join to `(facet, amount [, stockReason])` with the seqKey as a TIE-BREAKER
rather than a requirement; or add a UT-window tolerance; or let an entry declare
`stockReason` alone as sufficient corroboration when the amount matches. Each changes
what "unexpected" means, so it wanted a deliberate design pass rather than a
scenario-author workaround. The windowed option won because it is the only one where
the SPEC still constrains WHEN the award may land (an amount-only or reason-only join
would corroborate a declared award appearing at any point in the flight, including
one fired by an unrelated later event). `captureCrossCheck = "report"` remains the
correct setting for a flown spec until its author declares windows from a green run's
`capturedRaw`.

NOTE, so this is not over-read: none of it weakens the oracle. The cross-check only
CLASSIFIES; a captured amount is never summed into EXPECTED, and the
seam-declared-vs-produced-save diff is untouched. CL-2's ledger verifier passed with 0
hard divergences on both flights.

---

## ~~ORBITSEGMENT-ANGLE-UNITS: OrbitSegment angular fields carried degrees from recorder producers and radians from the extrapolator~~ [FOUND 2026-07-29 by the PR #1378 test-coverage campaign. FIXED, branch `orbitsegment-angle-units`]

### What happened

The intended `OrbitSegment` contract is KSP-native units (documented in `ReaimOrbitSegmentConverter` and assumed by every `new Orbit(...)` consumer): `inclination` / `longitudeOfAscendingNode` / `argumentOfPeriapsis` in DEGREES, `meanAnomalyAtEpoch` in RADIANS. Every recorder-side producer (`FlightRecorder` / `BackgroundRecorder` `CreateOrbitSegmentFromVessel`, `PatchedConicSnapshot`, the finalizer reseeds, `ReFlyCanonicalization`) complied, but `BallisticExtrapolator` violated it in both directions: `TwoBodyOrbit.TryCreateFromSegment` read the degree fields straight into radians trig (so `TryPropagate` returned wrongly oriented positions for recorder-authored segments, poisoning the extrapolation seed states), and `CreateSegment` wrote its radians-internal elements back into the predicted `OrbitSegment`s it appends to recordings (so every degrees consumer - map render, playback, terminal-orbit capture - misoriented them). `GhostExtender.PropagateOrbital` had the same read-side bug: fed degree values by `RecordingEndpointResolver.TryGetOrbitEndpointCoordinates` and `VesselGhoster.ComputePropagatedPosition` while doing `Math.Cos(inc)` directly. Radius-vs-time is rotation-invariant (only sma / ecc / mEp / epoch matter, all unit-consistent), which is why the atmospheric-clip search and altitude profiles worked and the bug survived: only orientations and derived lat/lon were wrong.

### Fix

One unit contract, pinned by a doc comment on `OrbitSegment`: KSP-native degrees for inc/LAN/argPe, radians for mEp. `TwoBodyOrbit` stays radians-internal and converts at the boundary (`TryCreateFromSegment` deg->rad, `CreateSegment` rad->deg); `GhostExtender.PropagateOrbital` converts deg->rad at entry. No schema generation bump: the serialized meaning (degrees) is unchanged - the extrapolator was writing non-conforming values into an unchanged contract, and per the no-migration rule old recordings carrying radian-valued predicted segments are left as they are (they mis-render today and keep mis-rendering identically). Guarded by orientation-pinning tests in `BallisticExtrapolatorTests` (degree LAN places the epoch position on the rotated axis, degree inclination reaches the polar axis, `Extrapolate` output carries 90-degree - not pi/2 - inclination for a polar seed) and `GhostExtenderTests` (LAN=90 shifts longitude by exactly 90 degrees).

---

## BallisticExtrapolator frame mismatches (follow-up to ORBITSEGMENT-ANGLE-UNITS; needs in-game calibration)

Found during the units audit, deliberately NOT fixed there because world-frame sign/swizzle conventions must be calibrated in-game, never re-derived on paper. With the units fixed, `TwoBodyOrbit` state vectors are well-defined: KSP Zup-swizzled body-relative (the `Orbit.getRelativePositionAtUT` / `getOrbitalVelocityAtUT` frame). Three extrapolator consumers do not honor that frame:

1. `IncompleteBallisticSceneExitFinalizer.ResolveBodyFixedSurfaceCoordinates` computes `worldPos = body.position + position` with no `.xzy` unswizzle, so terrain-altitude sampling and the recorded terminal impact lat/lon interpret Zup vectors as Y-up world offsets (lat/lon wrong by an axis swap).
2. `TryBuildStartStateFromVessel` seeds `position = vessel.orbit.getPositionAtUT(commitUT)` - ABSOLUTE world position (includes `referenceBody.position`), not body-relative - mixed with a Zup-relative velocity. In practice this is the destroyed-vessel fallback path (the garbage state is what `SubSurfaceStart` classifies on), but a live vessel reaching it would extrapolate from a nonsense frame. The likely-correct seed is `getRelativePositionAtUT(commitUT)` + `getOrbitalVelocityAtUT(commitUT)` (both Zup body-relative); note the `SubSurfaceStart` destroyed-fingerprint classification depends on the current behavior, so any change must re-verify that path.
3. The `ParentFrameState` resolver in `TryBuildExtrapolationBodies` returns `bodyOrbit.getPositionAtUT(ut)` (absolute world) where SOI entry/exit logic compares against Zup parent-relative vessel states; should likely be `getRelativePositionAtUT(ut)`.
4. `SeedPredictedSegmentOrbitalFrameRotations` computes `orbitalFrameRotation` from `TryPropagate`'s Zup-frame vectors, while playback's `ParsekFlight.ComputeOrbitalRotation` resolves that rotation against `orbit.getPositionAtUT` world-frame positions - predicted-segment ghost attitude is off by the frame difference (cosmetic; found by the PR #1386 review).

Each is a behavioral change on live extrapolation paths; fix together with an in-game proof (a known-impact descent whose recorded terminal lat/lon can be compared against the actual crash site).

---

## AN ORBITAL EVA RECORDS NOTHING: `RefreshFinalizationCache` throws `ArithmeticException` out of `SolveHyperbolicKepler` on every physics frame [FOUND 2026-07-30 by the R12 live-validation runs. NOT FIXED]

Sibling of the frame-mismatch entry above - same file family (`BallisticExtrapolator`
/ `IncompleteBallisticSceneExitFinalizer`), different failure: this one does not
mis-orient the answer, it destroys the recording.

### The measurement

Run `2026-07-30_1532_S0.7-exit-auto-commit`, collected at
`logs/2026-07-30_1833_S0.7-exit-auto-commit`. The profile was EVA-2's, on
`eva2-lko-crewed`: `autoRecordOnEva=true`, `EvaExit settleSeconds=10`, `EvaBoard`.

```
18:33:01.503  Recording started: vessel="Valentina Kerman", parts=1, points=0
18:33:01.531  Sample skipped at ut=422.15; waiting for motion/attitude trigger
18:33:01.552  [EXC] ArithmeticException  <- first of 501
18:33:11.560  [EXC] ArithmeticException  <- last
18:33:11.638  (board merge; the Kerbal X recording promotes)
FinalizeTreeRecordings: 'Valentina Kerman' points=1 orbitSegs=0 maxDist=0m
FinalizeTreeRecordings: 'Kerbal X'         points=1 orbitSegs=0 maxDist=0m
```

TEN SECONDS of orbital flight at ~2.2 km/s, and exactly ONE point - the boundary
seed. `grep -c ArithmeticException` on that log is **501**, one per physics frame for
the whole EVA, every one on the identical stack:

```
FlightRecorder.OnPhysicsFrame
  -> FlightRecorder.RefreshFinalizationCache
  -> RecordingFinalizationCacheProducer.TryBuildFromLiveVessel
  -> IncompleteBallisticSceneExitFinalizer.TryFinalizeRecording
  -> ...TryCompleteFinalizationFromPatchedSnapshot -> TryBuildStartStateFromSegment
  -> BallisticExtrapolator.TryPropagate -> TwoBodyOrbit.GetStateAtUT
  -> TwoBodyOrbit.SolveHyperbolicKepler -> System.Math.Sign(NaN)
     ArithmeticException: Function does not accept floating point Not-a-Number values.
```

The NaN does not originate in the extrapolator. Immediately upstream, stock logs
`CheckEncounter: failed to find any intercepts at all` and
`dT is NaN! tA: NaN, E: NaN, M: NaN, T: NaN` from `PatchedConicSolver.Update`, driven
by `PatchedConicSnapshot.VesselPatchedConicSnapshotSource.Update()` - so a degenerate
patched-conic solve on the 1-part EVA kerbal feeds NaN elements into the snapshot,
and the extrapolator consumes them without a finite check.

### Why it matters more than one bad recording

The exception escapes `OnPhysicsFrame` (Unity logs it as `[EXC]` from the Update
loop), so it takes the SAMPLING with it. Everything the recorder would have done that
frame - trajectory points, part events, the background pass - does not happen. The
recording is not degraded, it is empty.

### Why nothing caught it

`EVA-2-orbital-board` is green and stays green. Its numeric guard is
`recordings.count = { min = 2, max = 2 }`, which counts `.prec` FILES, and two empty
recordings are still two files. This is the category inventory's "fourth trap" (a
vacuous PASS the tally cannot see) reappearing in the RECORDINGS dimension instead of
the batch-tally one. Nothing in the verifier chain asserts a recording has points.

### Scope, as far as the evidence goes

- PRE-EXISTING. R12 touched only `TestCommands/`, docs, tests and `harness/`.
- NOT reproduced on the pad: `EVA-4-atmo-chute`'s archived log
  (`logs/2026-07-25_1310_EVA-4-atmo-chute`) has ZERO occurrences of
  `SolveHyperbolicKepler` and recordings of 278 / 96 points. So the trigger looks
  specific to an orbital - or otherwise degenerate-conic - subject.
- UNKNOWN whether an ordinary orbiting SHIP (not a 1-part EVA kerbal) hits it. The
  only non-EVA orbital recording measured here ran 133 ms and never got far enough to
  say.

### Fix directions (not chosen)

1. Guard the boundary: `TwoBodyOrbit.TryCreateFromSegment` / `GetStateAtUT` reject
   non-finite elements and return false instead of throwing, so a degenerate conic
   declines to extrapolate rather than killing the frame. Cheapest, and it matches
   how the rest of the finalizer treats an unresolvable tail.
2. Guard the source: `PatchedConicSnapshot` drops a patch whose elements are not
   finite, so the NaN never enters a recording's predicted segments either.
3. Defensive: wrap `RefreshFinalizationCache`'s call in `OnPhysicsFrame` so a
   finalization-cache failure can never cost a sample. This one is worth doing
   REGARDLESS of the other two - the finalization cache is an optimisation, and it
   should not be able to stop the recorder.

A harness-side companion is worth considering separately: an
`expectations.recordings` assertion on POINTS, not only on file count, would have
caught this on EVA-2's first flight.

---

## Intermittent stock `SpaceTracking.buildVesselsList` NRE on TRACKSTATION ghost teardown is not classified by the ghost-NRE suppressor [FOUND 2026-07-30 by `H23-tracking-station`. NOT FIXED, low cost]

Observed on the FIRST `H23-tracking-station` flight (2 raw Unity exceptions) and NOT
on the second, identical, pinned flight (0) - so it is intermittent, which is why the
spec deliberately does not arm `[expectations.unityExceptions] maxTotal`.

```
[Parsek][WARN][GhostMap] SpaceTracking.buildVesselsList exception left visible:
  type=NullReferenceException totalVessels=0 ghostVessels=0
  ghostMissingOrbitRenderers=0 nonGhostMissingOrbitRenderers=0
  firstMissingOrbitRenderer=non-ghost-or-none priorStockNullCandidates=0
  scanError="NullReferenceException: Object reference not set to an instance of an object"
[ERR] Exception handling event onVesselDestroy in class SpaceTracking:
  System.NullReferenceException
```

It fires during the synthetic-ghost teardown the TS tests perform
(`SyntheticTrackingStationRecordingScope` dispose ->
`GhostMapPresence` ProtoVessel destroy -> stock `onVesselDestroy` ->
`SpaceTracking.buildVesselsList`).

`GhostTrackingStationPatch.IsKnownGhostProtoVesselNre` declined to suppress it, and
declined CORRECTLY on its own terms: the context scan it classifies from THREW too
(hence `totalVessels=0 ghostVessels=0` and the populated `scanError`), so it saw a
zero-ghost context and refused to swallow an exception it could not attribute. That
is the right default - a suppressor that swallows on missing evidence is how a real
defect goes quiet.

THE QUESTION, not the fix: should a scan that itself failed be classified from
`scanError` plus the known call site, rather than from counts the failed scan never
populated? Today the answer is "leave it visible", which costs nothing - the tests it
interrupts still pass - but it does mean a TRACKSTATION spec cannot arm a Unity
exception budget without absorbing this.

---

## ~~SAFEWRITE-DESTROYS-ON-FAILED-WRITE: the shared safe-write deleted the destination and then failed to replace it~~ [FOUND 2026-07-29 by a read of `FileIOUtils`. FIXED, branch `fix-safewrite-file-destruction`]

### What happened

`FileIOUtils.SafeWriteConfigNode` called `node.Save(tmpPath)` and **ignored the bool it returns**. KSP's `ConfigNode.Save` swallows its own IO exception and reports failure only through that return, so on a full disk / permission denial / locked file the method carried on to its `File.Delete(path)` + `File.Move(tmpPath, path)` sequence: the caller's existing file was DELETED, then the move threw because the temp file had never been written. Net effect of a failed write: the previous data destroyed, nothing written in its place. Every store on that path was exposed - recording `.prec` sidecars, the ledger, `ParsekSettings`, the milestone store, `GameStateStore`, and the offline-analyzer baseline.

The delete-then-move sequence was a second, independent hazard shared with `SafeWriteBytes` and `SafeMove`: even on a *successful* write there was a window in which the destination was deleted and the replacement was not yet in place, so a crash inside it lost the file.

There were no unit tests for any of the three methods.

### Fix

- `SafeWriteConfigNode` checks `ConfigNode.Save`'s return, and belt-and-braces that the temp file exists (and is non-empty when the node has content - an empty node legitimately serialises to a zero-byte file, so the size check is gated on `nodeHasContent`). On failure it deletes the temp file, logs a grep-stable `SafeWrite: failed to write temp file '<tmp>' (<reason>) - destination '<path>' left untouched` Warn, and throws an `IOException` **without ever touching the destination**. `SafeWriteBytes` does the same around `File.WriteAllBytes` (which throws rather than returning a status, so its exception is re-thrown verbatim). Both route the swap through `ReplaceOrDiscardTemp`, so a failed swap discards the temp instead of orphaning it and logs `SafeWrite: failed to replace '<path>' with temp file '<tmp>'`.
- All three methods now swap through one private `ReplaceDestination` helper. Its ordering invariant is *the previous file is never the thing that gets lost* - deliberately NOT "the destination is never absent": (1) a missing source throws `FileNotFoundException` before anything is touched, so a `SafeMove` whose source was never produced cannot shuffle a healthy destination around; (2) no destination yet -> a bare `File.Move` (which never overwrites on .NET Framework, so a racing creation throws instead of being clobbered); (3) destination present -> `File.Replace(src, dest, null, ignoreMetadataErrors: true)`, the single atomic swap, with the same 4-arg form `SidecarFileCommitBatch` uses because the strict 3-arg form fails on ACL / metadata mismatches (cloud-synced folders, non-NTFS volumes) and would push routine writes onto the weaker fallback; (4) only if `File.Replace` throws, the original is MOVED ASIDE to a GUID-suffixed sibling `<dest>.bak.<guid>` - never a bare `.bak`, which could be a file the user or another mod owns, and never pre-deleted - the replacement is moved in, and only then is the aside copy removed, restoring it if the second move fails and naming it in the log if even the restore fails. **The fallback is not atomic**: between its two renames the destination is briefly absent and nothing restores the aside copy on the next load. The window is a rename-pair rather than a write, and the GUID naming makes crash residue sweepable (`RecordingStore.OrphanCleanup.IsTransientSidecarArtifactFile` matches `<suffix>.bak.`). A `File.Replace` that throws AFTER the replacement landed (`ERROR_UNABLE_TO_MOVE_REPLACEMENT_2`) is detected by re-checking source-gone + destination-present and treated as success-with-warn, so the fallback never rolls back new content that is already in place.
- Callers keep their contract: same signatures, same tag parameter, still throw on failure.

Guarded by `Source/Parsek.Tests/FileIOUtilsSafeWriteTests.cs`. The temp-write failure is simulated by pre-creating a DIRECTORY on the `.tmp` path (neither `ConfigNode.Save` nor `File.WriteAllBytes` can open a directory for writing) - deliberately chosen over a read-only parent directory because it fails ONLY the temp write, leaving the destination perfectly writable, so a surviving destination proves the code chose not to touch it rather than that the OS refused. The replace-step failure is simulated by holding the destination open with `FileShare.None`; those cells no-op on a platform that does not enforce sharing (Mono on Unix). The move-aside fallback is otherwise unreachable from a test (on a healthy filesystem `File.Replace` always wins), so it is driven through the `FileIOUtils.ForceMoveAsideFallbackForTesting` seam - always false in the game, reset in the test's `Dispose` - covering both its success path and its restore-the-original path.

---

## Basic / Advanced UI mode: hide non-essential windows behind a Settings toggle [BUILT, pending in-game validation, branch `claude/mods-ui-basic-advanced-amrgy9`]

Players report the UI is too complicated: the main window launches eight windows totalling ~25k lines of IMGUI across 13 surfaces. Design doc: `docs/dev/design-ui-basic-advanced.md`.

Phase 2 landed the pure decision core in `Source/Parsek/UI/UiComplexityMode.cs` (`UiComplexityMode` / `UiSurface` enums, `UiSurfaceVisibility.IsVisible` / `HiddenSurfaces` / `ResolveMode`) with `Source/Parsek.Tests/UiComplexityModeTests.cs`.

Phase 3 landed the setting and its plumbing: `ParsekSettings.uiComplexityMode` (persisted int, raw default Advanced = fail-open) plus the clamping `UiComplexityModeLevel` accessor routed through `UiSurfaceVisibility.FromStoredInt`; full `showRouteLines`-analog wiring in `ParsekSettingsPersistence` for the int key (`RecordUiComplexityMode`, `ApplyTo` restore branch, `Save` branch, both diagnostic lines, `ResetForTesting`, `GetStoredUiComplexityMode` / `SetStoredUiComplexityModeForTesting`, plus the new `HasAnyStoredValue()` and the temp-dir-drivable `SavesRootHasParsekDirectory(savesRoot)`); the design 7.3 first-run resolution living entirely in the persistence layer, with `ParsekScenario.OnLoad` passing only a bare `scenarioNodePopulated` bool so the mode vocabulary stays out of it for the phase-8 grep gate; the single setter seam `ParsekUI.SetUiComplexityMode`; and a new "Interface" section drawn first in the Settings window. Tests: `Source/Parsek.Tests/UiComplexityModePersistenceTests.cs` (stored-wins before footprint, `ResolutionIsSticky`, each footprint signal alone, out-of-range clamp, and the two log-assertion cases). The toggle is live and persists but gates nothing yet, so there is still no CHANGELOG entry - the feature entry lands in phase 8 with the gates.

Phase 4 landed the first gates and the frame-latched apply of doc 7.2. `ParsekUI` now owns the latch (`AppliedUiComplexityMode`, seeded from settings in both constructors so scene entry needs no pending apply); `SetUiComplexityMode` still writes + persists immediately but only QUEUES the new value, and `ApplyPendingUiComplexityModeIfAny` - called first thing in `ParsekFlight.Update` and `ParsekKSC.Update`, ahead of their early-outs, so it runs before OnGUI - latches it, moves the Info `Mode changed:` line to the moment the mode takes effect, and calls the deliberately empty `OnUiComplexityModeApplied(prev, next)` extension point that phases 5 and 7 fill with the tab clamp and the window-close handler. Gated in `ParsekUI.DrawWindow`: Real Spawn Control (composed with its existing `InFlight && flight != null` condition, trailing separator already inside), Kerbals and Career (their shared LEADING `GUILayout.Space` moved into the gate so Basic shows one gap between Logistics and Settings, not two), and Gloops Flight Recorder (composed with `InFlight`, trailing separator inside). Timeline / Missions / Logistics / Settings are constant-true in both modes and stay unwrapped. Nothing else is gated: no `DrawIfOpen` call site, no tab bar, no settings section. Tests added to `UiComplexityModePersistenceTests` cover queue-without-latch, latch-once idempotence, nothing-pending silence, and the toggle-back-before-latch collapse; `ModeChangeLogsTransition` now asserts the Info line after the apply. Phase 5 (tab-bar gate + tab clamp + Timeline GoTo gate) is next.

Phase 5 landed the tab-bar gate, the clamp, and the Timeline GoTo gate. `RecordingsTableUI` gained two pure seams - `VisibleTabCount(mode)` (Advanced 2, Basic 0: Basic draws NO toolbar rather than a one-button one, and it derives from `IsVisible(UiSurface.TabRecordings, mode)` so there is still one decision point and no second list) and `ClampTabIndexForMode(index, mode)` (Basic pins to `TabMissions`, Advanced returns the index unchanged) - plus the instance `ClampTabForBasic()` the deferred apply calls and a shared clamp body that emits the doc 12.2 Verbose line (old index, new index, `activeTabs`, origin). `DrawRecordingsWindow` now reads the frame-latched mode ONCE per pass, runs the defensive on-draw clamp, skips `GUILayout.Toolbar` and its trailing separator entirely in Basic, and pins the content dispatch to Missions rather than trusting the clamp alone. `ParsekUI.OnUiComplexityModeApplied` is no longer empty: on Advanced -> Basic it clamps the live window's tab, reached through a new `activeInstance` static (each scene constructs one `ParsekUI`; `Cleanup` clears it only when it still points at itself, so a scene handover cannot null the live reference) - the same reference phase 7's close handler will use. ~~Both Timeline `GoTo` buttons are wrapped in `IsVisible(UiSurface.TabRecordings, ...)` per doc 4.1a, read once per `DrawEntryRow` pass. `ScrollToRecording` needed no gate of its own: its only caller is the now-Advanced-only GoTo button, and the on-draw clamp plus the pinned dispatch cover any future caller.~~ Superseded by the 4.1a revision below: GoTo now targets the Missions tab through `ShowMissionForRecording`, is gated on `UiSurface.TabMissions`, and is visible in both modes; `ScrollToRecording` has no production caller. Tests: `VisibleTabCountIsZeroInBasicAndTwoInAdvanced`, `TabIndexClampsIntoRange`, `ClampTabForBasicMovesTheSelectionOffTheHiddenTab` in `RecordingsTableUITests`, the `VisibleTabCount` assertions folded into `MissionsIsTheDefaultAndFirstTab` per doc 13.1, and `ApplyingBasicClampsTheMissionsWindowTab` in `UiComplexityModePersistenceTests` driving the clamp end-to-end through the setter seam + deferred latch on a real `ParsekUI`. Phase 6 (Diagnostics + Sample Density settings-section gates) is next.

Phase 6 landed the two settings-section gates. `DrawSettingsWindow` reads the frame-latched mode once (the Interface section that hosts the toggle draws BEFORE the gated sections in the same callback, so a raw settings-field read would diverge between Layout and Repaint) and wraps `DrawDiagnosticsSettings` and `DrawSamplingSettings` in `IsVisible(SettingsSectionDiagnostics / SettingsSectionSampleDensity, ...)`, each with its trailing `GUILayout.Space(SpacingSmall)` separator INSIDE the gate so Basic shows no double gap. Interface, Recording, Looping, Ghosts, Stock UI and Data Management stay unconditional. Hiding Diagnostics takes the Settings-launched Test Runner launcher with it; the separate global Ctrl+Shift+T `ParsekTestRunnerGlobal` window and its shortcut are never gated in either mode (doc 6.3) - the automated-testing harness needs them and never opens this window. Tests: the `IsVisible` decisions for both section keys were already covered by `UiComplexityModeTests`, and `DrawSettingsWindow` has no headless seam, so rather than extract a `ShouldDrawSection` indirection the code does not need, a source-text wiring gate (`Source/Parsek.Tests/SettingsSectionGateWiringTests.cs`, the established `*WiringTests` pattern) pins the gate, the separator-inside-the-gate rule, the frame-latched read, and that exactly two sections are gated.

Phase 7 landed the mode-change close handler, the input-lock release, and the Gloops guard - the highest-risk phase (doc edge case 2: a gated window force-closed without releasing its KSP input lock soft-locks the player's mouse). The close set is a single explicit list, `ParsekUI.BuildGatedWindowCloseSet()`, returning six `GatedWindowCloseTarget` entries in close order - `CareerState`, `Kerbals`, `GloopsRecorder`, `SpawnControl`, `TestRunner` (the Settings-launched one; the global Ctrl+Shift+T window has its own lock and is never gated), `GroupPicker` (owns no lock, edge case 4) - each carrying its window name, its `Parsek_*` input-lock id (null for the picker), an `IsOpen` probe, a `HeldInputLock` probe, and a close-and-release action. The handler, both unit tests and the in-game lock test all walk that SAME list, so it cannot drift away from what the handler closes, which is exactly the failure `ParsekUI.Cleanup()` exhibits (it omits gloops, logistics and the test runner). `OnUiComplexityModeApplied` now runs the close loop before the phase-5 tab clamp on Advanced -> Basic and returns early (one Verbose line) on Basic -> Advanced. Each entry gets its own try/catch because `InputLockManager.RemoveControlLock` fires `GameEvents.onInputLocksModified` and a throwing listener must not strand the windows after it (precedent `RouteCreationDialog.cs:466-480`); a swallowed exception logs Warn with the window name and the exception type + message (doc 12.2). The close+release runs unconditionally per entry (every `ReleaseInputLock` is an idempotent no-op when the lock is not held), but the Verbose `Window auto-closed on mode change:` line is emitted only for surfaces that were actually open or holding a lock, so an ordinary switch does not put six no-op lines in the log; a single summary line carries `closed` / `alreadyClosed` / `failed`. Supporting seams: `HasInputLock` on the five lock owners, their lock-id consts promoted to `internal`, `TestRunnerUI.ReleaseInputLock` promoted from private to internal, `RecordingsTableUI.CloseGroupPickerForModeChange()` + `IsGroupPickerOpen`, and `ParsekUI.ActiveInstance` + `GetKerbalsUI` / `GetGloopsUI` / `GetSpawnControlUI` / `GetTestRunnerUI`. Gloops guard (edge case 11), two layers: the load-bearing one is the seam - `SetUiComplexityMode` refuses `Basic` while a manual Gloops recording runs (pure `ShouldRefuseModeChange(next, gloopsRecording)` predicate over a null-safe `activeInstance?.flight?.IsGloopsRecording` read, null in SPACECENTER where Gloops state cannot exist), logs Info `Mode switch refused: Gloops recording in progress`, and writes and persists nothing; the UI layer disables the Basic option in the Settings Interface section and appends `Stop the Gloops recording first.` to the SAME hint label (never a second control, so the IMGUI control count cannot change mid-frame), with no per-frame logging. Tests: `Source/Parsek.Tests/UiComplexityModeCloseHandlerTests.cs` (18) covers `CloseHandlerCoversEveryGatedLockOwner`, the end-to-end close through the seam + deferred latch, that Basic -> Advanced closes nothing and Advanced -> Basic spares Timeline / Missions, that a force-close preserves window state, `AutoCloseLogsPerWindow` plus the silent-when-nothing-open case, the no-live-UI no-op, and both halves of the Gloops guard (the seam refusal is driven through a `GloopsRecordingProbeForTesting` hook, because `ParsekFlight.IsGloopsRecording` is a computed property on a MonoBehaviour xUnit cannot construct). New in-game category `UiComplexityMode` in `Source/Parsek/InGameTests/UiComplexityModeInGameTests.cs`: `BasicModeReleasesInputLocks` (opens every gated surface, switches through the SEAM, waits for the controller's own `Update()` latch, and asserts `InputLockManager.GetControlLock(id) == ControlTypes.None` for all five ids plus `IsOpen == false`, re-checked a frame later so a pass cannot be credited to the `DrawIfOpen` self-heal), `ModeRoundTripPreservesWindowState`, and `AdvancedRenderParityAfterRoundTrip` (reflection walk over `UiSurface`). The doc edge-case-8 audit was re-confirmed: no existing in-game test opens or draws a gated window, so none needed forcing.

Phase 8 landed the grep gate and the doc closeout. `scripts/grep-audit-ui-complexity-mode.ps1` (modeled on `grep-audit-ers-els.ps1` per doc 13.4: allowlist file with directory-prefix entries, exit codes 0/1/2, scan root `Source/Parsek` so `Source/Parsek.Tests` is out of scope for free) scans for the four mode symbols `UiComplexityMode` / `UiSurfaceVisibility` / `UiSurface` / `uiComplexityMode` as case-sensitive SUBSTRINGS, not word-boundary matches, so an identifier like `ApplyPendingUiComplexityModeIfAny` in a recorder file is caught rather than slipping through a `\b` boundary. `scripts/ui-complexity-mode-audit-allowlist.txt` holds seven entries with per-entry rationale: `Source/Parsek/UI/` (hosts `UiComplexityMode.cs` itself plus every gated window), `Source/Parsek/InGameTests/` (the phase-7 in-game category, inside the scan root unlike `Source/Parsek.Tests`), `ParsekUI.cs`, `ParsekFlight.cs` + `ParsekKSC.cs` (the two deferred-apply `Update` hosts), and `ParsekSettings.cs` + `ParsekSettingsPersistence.cs`. `ParsekScenario.cs` is deliberately absent and passes clean, confirming the doc 7.3 bare-bool seam kept the vocabulary out of it. Wired as a second `[Fact]` in `Source/Parsek.Tests/GrepAuditTests.cs` (shared `RunGrepAuditScript` helper, same pwsh-on-PATH graceful skip / 60s timeout / exit-code assert / repo-root walk-up). Local run: 200 pattern hits, all allowlisted, exit 0; a negative test (a mode identifier planted in `FileIOUtils.cs`) correctly exits 1.

All 8 phases have now landed and the unit suite is green. The in-game half of the remaining validation is now AUTOMATED and LIVE-PROVEN as harness scenario `H22-ui-complexity-mode` (daily tier, `harness/scenarios/H22-ui-complexity-mode.toml`; first live run 2026-07-28 = FULL PASS attempt 1, 53 s wall, all six verifiers green, measured `BATCH_COMPLETE v1 total=3 passed=3 failed=0 skipped=0 category=UiComplexityMode scene=FLIGHT` with zero `[Parsek][ERROR]` lines): it runs the three `UiComplexityMode` in-game tests (`BasicModeReleasesInputLocks`, `ModeRoundTripPreservesWindowState`, `AdvancedRenderParityAfterRoundTrip`) unattended from the gloops-airshow FLIGHT host, so the LIVE `InputLockManager` release, the round-trip state preservation and the Advanced `UiSurface` restore no longer need a Ctrl+Shift+T operator session. Its log contract also pins the two production `Mode changed: uiComplexityMode=` lines in both directions, so the deferred-latch apply wiring in `ParsekFlight.Update` is proven by the run rather than assumed. Still operator-run: the manual Basic/Advanced playtest of the toggle and the gated surfaces (visual/feel, not assertable).

Basic-mode cross-link follow-up (2026-07-31): the phase-5 disposition hid the Timeline `GoTo` buttons in Basic because they targeted the Recordings tab, which left a Basic player with NO way to get from a timeline row to the flight it belongs to - a core-loop action, not an advanced one. Fix (doc 4.1a revised, v0.3): GoTo now targets the Missions tab. `TimelineWindowUI` calls the new `RecordingsTableUI.ShowMissionForRecording`, which force-opens the window, sets `selectedTab = TabMissions`, and delegates to the new `MissionsWindowUI.RevealMissionForRecording`; resolution is recording -> `Recording.TreeId` -> `MissionStore.FindOriginalMission` (the deterministic, name-linked original, never a clone), the scroll uses the same two-frame Repaint-detect / apply-before-`BeginScrollView` handshake as the recordings tab with the offset measured as a DIFFERENCE between two header rects, only the global `MissionStore.HideArchived` filter is cleared (never `Mission.Archived`), and every failure warns and lands the player on the tab unscrolled rather than arming a pending target that could fire on an unrelated frame. The gate is kept but re-keyed to `UiSurface.TabMissions`, so it hides nothing today yet still makes "GoTo can never point at a hidden destination" mechanical. `ParsekUI.SelectedRecordingId` (write-only dead state, zero readers) is removed with its last two writers. `ScrollToRecording` is retained as the Recordings tab's own navigation API but now has NO production caller (see the follow-up item below). A tree committed mid-scene (post-revert merge, rapid-switch commit) carries no Mission until the Missions tab's own draw seeds one, and the freshest flight is exactly the row a player is most likely to click GoTo on, so the reveal calls the idempotent `MissionStore.EnsureDefaultsForTrees` itself rather than landing the common case on a warn one frame early. Tests: `Source/Parsek.Tests/TimelineGoToMissionTests.cs` (17 cells: happy path, tab move off Recordings, mid-scene default-mission seeding, original-not-clone pick, the three Archive-filter rules, the three headless-reachable failure paths, the stale-target clear, the no-mission button predicate plus a source gate that it is read, the mode-to-wording bridge plus a source gate on its call site, the gate key in both modes, and a source-text gate pinning both GoTo button sites).

Three-reviewer panel follow-ups (2026-08-01). (1) The capture now requires that the same frame ran the list's own LAYOUT pass, plus a zero-height reject. A Repaint alone is not proof that rects are solved, and the most common GoTo path - window closed, the click opens it mid-frame - could paint a Repaint against an unsolved cache, measure zeros, consume the target, land at offset 0 and log it as a success. (2) The Archive-filter clear is QUEUED by the click and applied by the draw on a LAYOUT pass only; writing it from the click flipped how many mission blocks draw between one frame's Layout and Repaint, which throws the control-count `ArgumentException`. (3) A captured offset is age-checked at the apply site, so a tab switch or window close between capture and apply cannot yank the list minutes later. (4) The reveal resolves through ERS again, restoring the invariant the old cross-link encoded. (5) `TimelineWindowUI.CanGoToMission` disables the button on rows with no `TreeId` - manual Gloops ghost-only recordings are committed without a tree and DO produce timeline rows, so the button was live and inert on them. (6) `ParsekUI.IsSpawnControlReachable` is now a scanned pattern in `grep-audit-ui-complexity-mode.ps1`: without it, a mode read wearing a plain-bool name could be consumed by any file with the gate still green.

Two other Basic-mode dangling cross-links found by the same audit and fixed in the same commit. (a) `ParsekFlight.NotifyNewProximityCandidates` posted a 10-second screen message telling the player to open Real Spawn Control - a window whose only launcher Basic hides. The message text now varies: `SelectiveSpawnUI.FormatProximityNotification` (pure, takes a bool) keeps the observation and drops the call to action when the window is unreachable, and the reachability question is asked of the new `ParsekUI.IsSpawnControlReachable` so `ParsekFlight` never names the mode vocabulary (grep gate + doc section 5 invariant intact; the narrow extension is written up as new doc section 9.1). (b) `MergeDialog.Commit`'s post-merge seal guidance said "seal it from the Recordings window", which is both the wrong window name (it is `Parsek - Missions`) and the wrong destination - Seal lives on the Timeline's separation rows and in the Missions tab's Re-Fly cell. Retargeted to the Timeline, a surface visible in every mode. Plain wording fix, no mode read.

Follow-up (open, small): `RecordingsTableUI.ScrollToRecording` and its supporting `pendingScrollToRecordingId` / `pendingScrollRowIndex` / `renderedRowCounter` machinery now have no production caller. Decide whether to delete them (also rewriting `ScrollToRecordingSelectsRecordingsTab`, `ClampTabForBasicMovesTheSelectionOffTheHiddenTab` and `ApplyingBasicClampsTheMissionsWindowTab`, which use the method as their only lever for selecting `TabRecordings`) or keep them as the Recordings tab's navigation API. Kept for now: deleting reaches into the recordings-tab row-draw hot path, which this change otherwise does not touch.

Open Basic-mode gap found by the same audit, NOT fixed here because it needs a design decision: `Recording.Hidden` suppresses timeline rows (`Timeline/TimelineBuilder.cs`, `if (rec.Hidden) continue;`) but every writer of that flag lives in the Recordings tab. Hide a recording in Advanced, switch to Basic, and it is gone from the Timeline with no Basic control able to bring it back. (It still shows in the Missions tab, which does not filter on `Hidden`.) Two candidate fixes: have the Timeline ignore `Hidden` in Basic, or give Basic an un-hide affordance. The former changes filtering semantics; neither should be picked without deciding what `Hidden` means to a Basic player.

Playtest fix (2026-07-28): the Settings window kept its old height across a mode change - dead space below the buttons in Basic (which drops the Diagnostics + Sample Density sections), clipped content back in Advanced - because the window passes a stored fixed height and has no resize handle. Fix: `ParsekUI.OnUiComplexityModeApplied` now calls `SettingsWindowUI.RequestHeightRemeasure()` in BOTH directions (doc 7.2 step 4), which flags a one-shot re-measure the next Layout pass consumes by dropping the `GUILayout.Height` option for that single pass; only the height is re-derived, so the window never jumps. The Missions window is left alone on purpose: its height is player-owned via the resize handle and its tab bar sits above a scroll view that absorbs the freed 26 px. Tests: `ModeApplyRequestsSettingsWindowHeightRemeasureInBothDirections` and `NoOpLatchDoesNotRequestAHeightRemeasure` in `UiComplexityModeCloseHandlerTests`.

Analysis result - Basic keeps Timeline, Missions, Logistics, Settings; hides the Recordings tab (the raw per-recording table, 62 buttons / 13 toggles), the Career window (2 buttons, 2 toggles, zero mutations - pure read-only reference), the Kerbals window (4 buttons, 0 toggles, zero mutations), Gloops Flight Recorder, Real Spawn Control, and the Diagnostics + Sample Density settings sections. Advanced stays byte-identical to today.

All blocking decisions are RESOLVED (2026-07-27) and a four-lens plan review (inventory, sufficiency, risk/invariants, implementation) was folded into doc v0.2 (2026-07-28); the doc is ready to implement. Naming: the main-window button, the window title, and the first/default tab all become "Missions" in BOTH modes (consistency: a label that changed with the mode would defeat the point). This is the one deliberate Advanced-visible change in the feature and ships as phase 1, standalone, with its own CHANGELOG entry; phase 1 also makes `ScrollToRecording` select the Recordings tab explicitly, or the reorder alone would break the Timeline GoTo cross-link in Advanced (doc 4.1a; the cross-link has since been retargeted at the Missions tab, see the 4.1a revision entry below). The "Recordings" strings that must NOT be renamed are tabulated in doc section 4.2 (window ID hash, storage paths, all diagnostic log strings, the Timeline entry filter, the Wipe All Recordings strings); exactly four user-facing strings change (button, title, two GoTo tooltips). `selectedTab` is transient, not persisted, so the tab reorder needs no migration. First-run default: the stored setting always wins unconditionally; with no stored value, an INSTALL-level footprint (any saves/*/Parsek dir, populated scenario node, or any stored settings keys) picks Advanced for existing installs and Basic for new ones, and the resolved default is persisted immediately so resolution runs at most once per install (a per-save footprint or an unpersisted resolve would silently flip the mode later - doc 7.3). Logistics stays visible in Basic even at zero routes, for discoverability (philosophy 7: Basic is a starting point, not a cage); the conditional "appear once used" variant is rejected, since a button that appears only after you found the feature cannot introduce you to it. Known accepted v1 limitation (doc 4.3): retroactive per-recording playback-disable is Advanced-only; follow-up proposal 17.8 adds a mission-level ghost-visibility toggle.

Key risks carried in the doc: (1) input-lock leak when a gated window is force-closed on mode change (`ReleaseInputLock` mandatory in an explicit per-window close list incl. Gloops / the Settings-launched test runner / the group picker, per-window try/catch, dedicated in-game test; the ungated per-frame `DrawIfOpen` self-heal is the backstop and must never be gated); (2) IMGUI layout stability - the mode is applied frame-latched in Update, never mid-OnGUI, or control counts diverge between Layout and Repaint; (3) `RecordingsTableUI.selectedTab` clamp when Basic draws zero tabs; (4) the stored-value branch must be written and tested before the footprint branch, and the resolve seam takes the stored value as an input so the precedence test can actually fail; (5) mode switch to Basic is refused while a Gloops manual recording is in progress. Enforcement: a `scripts/grep-audit-ui-complexity-mode.ps1` gate keeping the mode symbol out of every non-UI path, so the visibility-only invariant cannot rot.

Separate UI improvements proposed in section 17 of the doc (each ships on its own): extract the window chrome duplicated across 10 files (explicitly NOT a prerequisite for this feature), group the flat 8-button main window, add a name filter to the Recordings table and Missions tab, first-run onboarding, toolbar-button state badge for broken routes, collapsible Settings sections, mission-level ghost-visibility toggle (17.8).

## HARNESS-MIDMISSION-COMMIT-BYPASS: a mid-mission seam CommitTree bypasses the unmet-mission tail gate [FOUND 2026-07-28 by the retrospective review of PRs #1345-#1363. LATENT, NOT CAUSING FAILURES. Decision wanted: gate it, or document it as intended]

### What happens

PR #1349 added the unmet-mission tail gate in `harness/run.py` (`plan_unmet_mission_tail`, ~line 1137): after a mission returns UNMET, only `cleanup`-role seam verbs in the remaining `driver.steps` are driven, so a world-mutating tail (a CommitTree over a flight that never reached its envelope) can no longer fire. That gate covers the DRIVER-side tail only.

The route-1 mid-mission bridge is a second, ungated path to the same verb: `mission_runner.py` dispatches `ACTION_PARSEK_COMMIT_TREE` (~line 1503) to `_perform_seam_commit` (~line 1687), which writes `cmd=CommitTree` into the seam request channel from INSIDE the mission subprocess, using the reserved id `run.py` hands every mission (~line 1032). That write happens mid-flight, before the mission's verdict exists, so a mission that commits mid-flight and THEN returns UNMET has already landed a durable commit the tail gate never saw.

### Why it is latent, and the open decision

No committed mission hits this today: the only emitters of `ACTION_PARSEK_COMMIT_TREE` are the B-DOCK machines, which emit it on their success path. The exposure is a future forge/mission that mid-commits and then fails a later assertion. Two defensible resolutions, deliberately not chosen here:

- **Gate it**: make an UNMET verdict after a fired mid-mission commit visible (e.g. flag the run, or have `run.py` refuse to classify such a run clean without an explicit spec opt-in).
- **Document it as intended**: a mid-mission commit is a deliberate mission design act; the mission author owns the consequence, and the committed state is itself what the post-mission verifiers then inspect.

Until decided, treat "PR #1349 closed the world-mutating-tail-after-UNMET hole" as true for `driver.steps` ONLY, not for route-1 mid-mission commits.

---

## HARNESS-ANOMALY-SWEEP-DECORATIVE-WHEN-TRACERS-OFF: with tracers-off as the baseline, most specs' `allowedAnomalies = []` can never bite [RECORDED 2026-07-28 from the retrospective review of PRs #1345-#1363. DELIBERATE TRADE, DEFERRED - recorded so the coverage gap is not assumed closed]

### What happens

PR #1352 made tracers-off the deterministic instance baseline (`run.py` writes it at stage AND teardown), because S1.4's `mapRenderTracing=true` had been leaking instance-wide into every later run. Since then, a scenario that wants a tracer arms it with its own `SetSetting` step - and only S1.4 / S1.6 / S1.7 do (checked 2026-07-28: 3 of the 55 committed spec files set `mapRenderTracing`; none set `ledgerTracing`).

The Tier-C anomaly sweep (`hlib._anomaly_reasons`, ~line 3254) matches the tracers' raise shape (`phase=Anomaly ... reason=<token>`), and both raisers (`MapRenderTrace.EmitAnomaly`, `LedgerTrace.FormatAnomaly`) are behind those settings. So on every committed spec except S1.4/S1.6/S1.7, the sweep can raise NOTHING: the `allowedAnomalies = []` those specs all carry is decorative, and their `anomalySweep` verifier row is vacuously green - it proves the tracers were off, not that no anomaly occurred.

### Why it stays as-is for now

The alternative (tracer-on everywhere) is exactly the cross-run leak PR #1352 fixed, plus per-frame tracer cost on every flight. The trade is deliberate and deferred, not forgotten. What this entry pins: do NOT read a green `anomalySweep` row on a non-tracer spec as anomaly coverage, and if a future lane wants Tier-C coverage on a spec, the spec must arm the tracer itself (and expect the S1.4-class run-cost).

---

## ~~HLIB-ALLOWBATCH-NONLITERAL-FAILS-OPEN: a non-literal AllowBatchExecution argument silently resolves to true~~ [FOUND 2026-07-28 by the retrospective review of PRs #1345-#1363. LATENT - every committed declaration is literal today. FIXED 2026-07-31, branch `harness-hardening-2`]

### Fix

Both halves of the fix shape below, because they answer different questions:
`_resolve_bool_default_true` now returns True/False only for an absent argument or
the literal `true`/`false` and None for anything else, and
`parse_ingame_test_declarations` maps that None to (a) `allow_batch = False` -
fail-CLOSED in the `_resolve_bool_default_false` direction, so an unreadable
expression under-counts admissions and a pinned tally reds, never inflates - and (b)
a new `InGameTestDecl.allow_batch_marker` carrying the `<unresolved:<expr>>` marker,
which `unresolved_ingame_declarations` now reports alongside unresolvable
Category/Scene, so `CommittedBatchTallySourceSyncTests` reds ON THE DECLARATION
rather than leaving a bounds mismatch to be reverse-engineered. Both malformed shapes
(a const indirection, a computed expression - plus C#'s capitalized `False`, which is
an identifier to this parse) are pinned in
`test_hlib.py::test_non_literal_allow_batch_fails_closed_with_a_marker`. Verified
mechanically with the parser itself that the whole `Source/Parsek` tree still parses
IDENTICALLY: 542 declarations, 471 batch-allowed / 71 not, 72 restore-backed, zero
markers, zero unresolved - every committed argument is a literal, so the fail-closed
branch is dead code against today's tree by construction.

### What happens

`hlib._resolve_bool_default_true` (~line 1467) resolves an `AllowBatchExecution` attribute argument by `(expr or "true").strip() != "false"`: anything that is not literally `false` reads as true. A non-literal argument (a const indirection, a computed expression) therefore silently resolves to batch-allowed, loosening the derived tally bounds in the direction that under-reports skips - instead of failing loud the way Category/Scene do (`<unresolved:...>` + `unresolved_ingame_declarations` reds the sync gate).

The asymmetry is now sharp in-file: the sibling `_resolve_bool_default_false` (`RestoreBatchFlightBaselineAfterExecution`, added by PR #1367) admits only a literal `true` and documents WHY it fails closed (an unreadable expression must under-count admissions so a pinned tally reds, never inflates). `_resolve_bool_default_true` predates that reasoning and was not revisited.

### Scope note: half of the reviewed gap is already closed

The review also flagged that `derive_batch_tally` did not model the `PrepareBatchExecutionIncludingFlightRestore` runner path (`InGameTestRunner.cs` ~line 1340: the isolated entry point admits `AllowBatchExecution=false` tests when `RestoreBatchFlightBaselineAfterExecution=true`). That half was CLOSED by PR #1367 (merged 2026-07-28): `InGameTestDecl` carries `restore_baseline` and `derive_batch_tally` takes an `isolated=` mode. Only the fail-open bool resolution above remains.

### Fix shape when picked up

Make an unresolvable `AllowBatchExecution` argument resolve to a loud marker (or fail-closed like its sibling) so `CommittedBatchTallySourceSyncTests` reds on the declaration instead of loosening the bounds. Latent today: `hlib.parse_ingame_test_declarations` over `Source/Parsek` finds only literal arguments.

---

## ~~HARNESS-INJECT-FAILS-OPEN: a no-op fixture injection reports success, and the miss surfaces three minutes later as an unrelated seam rejection~~ [FOUND 2026-07-29 by run `2026-07-29_1525_S1.5-rewind-loop` attempt 1. Driver-side only - no Parsek code on the path. FIXED 2026-07-31, branch `harness-hardening-2`]

### Fix

The staging POSTCONDITION from the fix shape below, driver-side only.
`run.py::stage_fixture` step 3 now asserts what the preset MUST have written into
the staged save after the injector returns, whatever its exit code:
`_inject_postcondition_missing` requires a non-empty `Parsek/Recordings/` for both
presets plus `Parsek/RewindPoints/rp_b9_root.sfs` for `rewind-b9` (the RP every
consumer's `InvokeRewind rp=rp_b9_root` needs - which also closes the second silent
trigger, the KSP.log lock-probe refusal, and any future no-op mechanism; the check is
mechanism-independent). A miss fails the stage immediately as terminal
INVALID(`stage-inject-noop`) with ZERO KSP boots, and the error names the likely
cause with its one-line remedy (assembly-existence probe: "Parsek.Tests assembly
missing ... dotnet build Source/Parsek.Tests" vs "assembly present; check the KSP.log
lock probe"). The subkind is deliberately NOT in `RETRYABLE_INVALID_SUBKINDS`: the
miss is deterministic per worktree, so a retry burns the `once` budget to learn
nothing (the exact V1-map-dwell double-flight shape). `--no-build` stays on the
injector, exactly as the DO-NOT below demands, and the hidden coupling to
`run_analyzer`'s building side effect is now irrelevant to correctness. Covered
through the fake-KSP smoke harness (`test_run_smoke.py::InjectPostconditionTests`): a
full `run_attempt` over a no-op injection terminates INVALID(stage-inject-noop)
pre-boot and non-retryable, success paths for both presets stage clean, an RP-less
rewind-b9 injection fails closed, and the predicate's shapes are pinned directly.

**LIVE-PROVEN 2026-07-31 against the real game, in the exact worktree state that
triggers it** (`Parsek-cl2-capture-arming`, `Source/Parsek.Tests` never built - the
deterministic trigger this entry identifies). Both halves, no divergence from the
claim, so no driver change was needed:

```
19:26:47  python run.py --id S1.5-rewind-loop
[Harness][Info][Select]  ... (Select / Admit / Lock lines elided)
[Harness][Info][Preflight] zombie-check instance=stock-minimal result=CLEAR
[Harness][Error][Stage] inject postcondition failed preset=rewind-b9 exit=0
    missing=[non-empty Parsek/Recordings/, Parsek/RewindPoints/rp_b9_root.sfs];
    aborting pre-boot (INVALID stage-inject-noop). likely cause: Parsek.Tests
    assembly missing (never built in this worktree; the injector's deliberate
    --no-build runs nothing) - remedy: dotnet build Source/Parsek.Tests
[Harness][Info][Cost] scenario cost attempts=1 wallTotal=0s terminal=INVALID
19:26:48
```

Run `2026-07-31_1626_S1.5-rewind-loop`: `verdict=INVALID subkind=stage-inject-noop`,
`wallSeconds=0` (wall-clock 19:26:47 -> 19:26:48). PRE-BOOT is proven by the result JSON rather than asserted -
`kspExit={"code": null, "killed": false}`, `collectLogs={"path": null, "ran": false}`,
`driver.steps=[]` - and NON-RETRYABLE by `attempt=1` with zero `_a2` files in
`results/`. Note `exit=0` in that line: the injector still reported success, which is
precisely the fail-open the postcondition now catches. Running the NAMED REMEDY
verbatim (`dotnet build Source/Parsek.Tests`) and re-invoking then staged, booted and
went GREEN on attempt 1 - run `2026-07-31_1627_S1.5-rewind-loop`, 63 s, `stage
save=gloops-airshow ... inject=rewind-b9`, `launch exe=...KSP_x64.exe pid=26188`,
every verifier PASS/SKIPPED. So the error message's remedy is not just plausible, it
is the whole fix: one build, one green run.

### What happens

`run.py`'s staging step 3 shells out to `scripts/inject-recordings.ps1` and records the outcome as `injected = res.ok`, where `ToolResult.ok` is `(not timed_out) and exit_code == 0`. Nothing downstream checks that the injection actually WROTE anything. When the script exits 0 having produced no fixture, staging logs its ordinary `stage save=... inject=rewind-b9 ...` line, the run launches KSP, and the first verb that needs the fixture fails for a reason that names the SYMPTOM rather than the cause.

On the S1.5 first-flight that is `invokerewind refused: unknown-rp rp=rp_b9_root` - which reads as "this spec asked for a RewindPoint that does not exist", i.e. a spec-authoring error, when the truth is "staging silently declined to create it". The run burned a full KSP boot and 190 s to say so.

### Root cause: `--no-build` against a never-built test assembly

`inject-recordings.ps1` appends `--no-build` to its `dotnet test` invocation unless `-Build` is passed, and `Runtime.run_inject` never passes it (deliberately - the flag exists so an injection cannot rebuild and clobber the plugin DLL while KSP holds it). In a worktree where `Source/Parsek.Tests` has never been built there is no assembly for `--no-build` to run, so `InjectRewindB9` never executes. The script's only guard is `if ($LASTEXITCODE -ne 0) { throw }`, so whatever that invocation returned did not trip it.

The trigger is therefore FRESH WORKTREE, and it is deterministic, not racy: the first injected-fixture run in any new worktree burns one attempt.

WHAT MAKES THE RETRY PASS IS NOT THE INJECTION, and this is the part worth carrying forward. It cannot be that attempt 1's own `dotnet test` left the assembly behind - `--no-build` builds nothing (observed below). The actual builder is a VERIFIER: `Runtime.run_analyzer` (`harness/run.py:273-279`) invokes `scripts/analyze-recordings.ps1` WITHOUT `-NoBuild`, and that script runs its own `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` (`analyze-recordings.ps1:118-129`), which does build. The analyzer runs on the FAILED attempt too - attempt 1's harness log line 42 is `verify analyzer status=PASS red=0 ... (triage-only)` - so the failing attempt is what warms the assembly for its own retry.

So the "deterministic, self-recovering" property is a HIDDEN COUPLING to an unrelated verifier happening to build. If `run_analyzer` ever gains `-NoBuild` - entirely plausible, for exactly the KSP-DLL-lock reason the injector already passes it - the fresh-worktree miss stops self-recovering and becomes a hard two-attempt burn that exhausts `retry.policy = "once"` and reds the scenario. Nothing anywhere records that `analyze-recordings.ps1` is load-bearing for injection; the postcondition fix below removes the coupling rather than documenting it.

### Evidence

- Attempt 1 (`2026-07-29_1525_S1.5-rewind-loop`, INVALID `driver-arg`, wall 190 s): harness log carries `[Stage] stage save=gloops-airshow template=fixtures/saves/gloops-airshow inject=rewind-b9 craft=0` with NO accompanying `[Warn][Stage] inject-recordings failed` line - so `res.ok` was True and pwsh exited 0.
- The staged save at that moment held `Parsek/GameState` only: no `Parsek/Recordings/`, no `Parsek/RewindPoints/`.
- `KSP.log`: `[Parsek][WARN][TestCommands] invokerewind refused: unknown-rp rp=rp_b9_root`.
- Attempt 2 staged the SAME preset and produced `Parsek/RewindPoints/rp_b9_root.sfs` plus the three `b9-*` recordings, then PASSed (`2026-07-29_1528_S1.5-rewind-loop_a2`, wall 69 s).
- `Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll` mtime `18:28:31.9 +0300` = `15:28:31.9Z`, `obj/` `15:28:29Z` - the test assembly was first built inside ATTEMPT 1's verifier tail (attempt 1 ran `15:25:31Z -> 15:28:41Z`, attempt 2 `15:28:47Z -> 15:29:56Z`, per the two result JSONs' `startedUtc`/`endedUtc`), 10 s before attempt 1 ended and 15 s before attempt 2 began. An earlier draft of this entry said "built DURING attempt 2, about three minutes after attempt 1's injection": that was a UTC-vs-local slip, comparing a local-time mtime against UTC run ids. The corrected timeline is what identifies the analyzer as the builder.
- Corroboration from the next scenario: S4.1 drives the same `rewind-b9` preset and injected correctly on ATTEMPT 1 with the assembly now warm.
- 2026-07-30, V1-map-dwell-mun-orbit first invocation: the "whole flight" cost
  prediction below came true - attempt 1 flew the FULL ~21-minute B11 profile
  green and then red `invokerewind refused: unknown-rp rp=rp_b9_root` (fresh
  `v1-map-dwell` worktree, `Parsek.Tests` never built), and attempt 2 burned a
  second full flight the same way. Attempt 2 also exposed a SECOND silent
  trigger on the same fail-open path: the injector's KSP.log lock probe refuses
  when ANY process holds the automation instance's `KSP.log` open (attempt 2
  restaged while attempt 1's KSP was still exiting; reproduced standalone with
  a `tail -f` on that log), and that refusal exits through the same
  reports-success staging path. Two full flights (~42 min wall) to learn what a
  staging postcondition would have said pre-boot.

OBSERVED (was inference in the first draft of this entry): `dotnet test <Parsek.Tests.csproj> --filter InjectRewindB9 -v minimal --no-build` against a never-built copy of `Source/` (copied without `bin`/`obj`) **exits 0, prints nothing at all, and creates no `bin/` or `obj/`**. Reproduced directly on this machine's dotnet 6.0.428, so the exit-0 half of the fail-open is a captured fact rather than a deduction from the absent warn line. The fix below is mechanism-independent either way.

### Why it matters beyond one wasted boot

The failure is silent in the direction that costs the most: staging claims success, so the run proceeds, and the diagnosis surfaces attached to the wrong component. `unknown-rp` points an investigator at the spec's `rp` argument and at `RewindB9Fixture`, both of which were correct. On a scenario with a real flight in front of the fixture-consuming verb this would cost the whole flight, not 190 s - and the classification would still be `driver-arg`, which reads as a spec defect.

It also erodes the retry budget: `retry.policy = "once"` exists to absorb genuine flakes, and a deterministic first-run miss consumes it before any real flake can.

### Fix shape when picked up

Assert the POSTCONDITION in `run.py` staging rather than trusting the exit code - the check is mechanism-independent, so it holds whatever `dotnet test` returns:

- `rewind-b9` -> require `saves/<run save>/Parsek/RewindPoints/rp_b9_root.sfs` and a non-empty `Parsek/Recordings/`.
- `all-synthetic` -> require a non-empty `Parsek/Recordings/`.

A miss should fail the stage immediately with a named subkind (`stage-inject-noop`) so the run never boots KSP, and the message should name the likely cause and its one-line remedy (`dotnet build Source/Parsek.Tests` in this worktree). Optionally make the script itself fail loud when `--no-build` finds no assembly, but the staging postcondition is the load-bearing half: it catches every way an injection can no-op, not just this one.

Do NOT "fix" this by dropping `--no-build` from the injector. That flag is deliberate (an injection must not rebuild and clobber the plugin DLL while KSP holds it), and removing it would trade a loud, cheap, pre-boot stage failure for exactly the silent cross-worktree DLL clobber the intentional-only deploy gate exists to prevent.

Worth doing in the same pass, since the coupling above is only visible once: assert the assembly EXISTS before invoking the injector, and if `run_analyzer` is ever given `-NoBuild`, that assertion becomes the only thing standing between a fresh worktree and a two-attempt red.

---

## ~~S4.1-IDLE-DISCARD: the scene-exit idle-on-pad auto-discard tears down a LIVE re-fly session's tree, leaving the marker with nothing to merge~~ [FOUND 2026-07-28 by run `2026-07-28_1932` attempt 1. Product call made 2026-07-30 (it IS a defect - "refuse while re-fly live"). FIXED 2026-07-30, branch `fix-s41-idle-discard`. SWEPT 2026-07-30: five consecutive S4.1-rewind-merge runs, 5/5 PASS on attempt 1 (57-72 s), flake quarantine CLEARED (rate 0.0, quarantined=false). CAVEAT: the sweep route never entered the guarded branch (zero `idle detected` / `refusing - refly-active` lines; every run concluded through the post-transition deferred dialog), so the guard's proof is the three behavioral unit tests, not the sweep - see the S4.1 row in `docs/dev/autotest-status.md`]

### Fix

`SceneExitInterceptor.TryAutoDiscardIdleActiveTree` now REFUSES (returns false) whenever `ParsekScenario.Instance.ActiveReFlySessionMarker != null` or `ActiveMergeJournal != null`, mirroring the sibling `ParsekFlight.TryEvaluateActiveSwitchSegmentNoOp` guard pair (`refly-active` / `merge-journal-active`). On refusal control falls through to step (6) of the scene-exit prefix, which shows the `ReFlyAttempt` dialog - the designed conclusion path: Commit tolerates an unflown provisional (`AppendRelations outcome=refused-unflown-provisional`), Discard clears the marker through `MergeDialog.TryDiscardActiveReFlyAttempt`. So the idle-on-pad discard keeps doing exactly what it is for on ordinary trees, and a live re-fly session always gets a conclusion instead of being silently dropped.

The two guards run FIRST in the method, before the `flight == null` / `HasActiveTree` / `IsActiveTreeIdleOnPad()` checks. That is production-equivalent (the prefix only reaches step 5 with a live flight holding an active tree), it avoids a pointless recorder flush inside the MUTATING `IsActiveTreeIdleOnPad`, and it makes the refusal behaviorally testable from xUnit without a live `ParsekFlight` - so the new coverage in `SceneExitInterceptorTests` executes product code instead of grepping source. Two new grep-stable Info lines on the `[SceneExit]` tag: `TryAutoDiscardIdleActiveTree: refusing - refly-active sess=<id> dest=<scene> - falling through to conclusion dialog` and the `merge-journal-active journal=<id>` variant. `AutoDiscardActiveTreeCore` is unchanged, so the other two auto-discard entry points keep their behavior.

### What happens

A re-fly is invoked, nothing is flown, and the session is concluded. At the scene exit:

```
[Flight]    IsActiveTreeIdleOnPad: all 4 recordings within 30m - idle on pad
[SceneExit] TryAutoDiscardIdleActiveTree: idle detected dest=SPACECENTER
            - tearing down live tree without finalize/stash
[Flight]    AutoDiscardActiveTreeCore: discarding live tree
            reason='scene-exit idle-on-pad auto-discard dest=SPACECENTER'
```

The tree is discarded WITHOUT being stashed, so no pending tree exists, so no merge dialog is ever raised - while `ParsekScenario.ActiveReFlySessionMarker` is still LIVE and valid (`Marker valid=True; spare=1 discarded=0` in the same run). The session dangles: a live marker, a spared provisional, and no tree and no dialog to conclude it with.

### The product question, which is genuinely open

**SETTLED 2026-07-30: the second reading was picked (a defect). The forensics below are preserved as written; see the Fix section above for what shipped.**

`TryAutoDiscardIdleActiveTree` does not consult the re-fly marker. Two defensible readings:

- **Correct as-is.** A re-fly attempt that flew nothing IS an idle-on-pad tree, and discarding it is exactly what that feature is for. The player rewound, changed their mind, and left; nothing of value is lost.
- **A defect.** The discard is silent, and it leaves an ACTIVE re-fly session with no tree, no dialog, and no conclusion. Nothing tells the player their attempt was dropped, and the marker's own lifecycle expects a merge or a discard decision that now never comes. Compare the sibling path, which is careful about exactly this: `AutoDiscardActiveTreeCore` explicitly clears an armed `SwitchSegmentSession` when it tears a tree down, precisely so a dangling session cannot resurface as a deferred dialog on the next load - but it does not do the equivalent for a re-fly marker.

That second point is the strongest argument that this is a real defect rather than working-as-intended: the same function already recognises the "do not leave a session pointing at a tree I just destroyed" hazard for switch segments, and simply does not handle the re-fly case.

**Do not fix this by making the harness avoid it.** S4.1 exists to exercise rewind-then-conclude-without-flying; if that shape trips a product decision, the scenario is doing its job.

### Why it presents as a test flake

S4.1 sits exactly on the idle-on-pad boundary (it rewinds to PRELAUNCH and flies nothing), so the classification lands on either side run to run: on `2026-07-28_1932` the discard fired on attempt 1 (INVALID, `AnswerMergeDialog` waited 120 s for a dialog that could not exist) and did not fire on attempt 2 (PASS). The scenario is currently flake-quarantined (`rate=0.75 over 7d`, a figure that also spans pre-fix history). Until the question above is settled, S4.1 should not be trusted as a nightly gate even though it can now pass.

### Root cause of the unentered guard, and the route pin (S4.1-PREFIX-RACE, RESOLVED 2026-07-30, branch `s41-prefix-live-coverage`)

The 2026-07-30 sweep's HONESTY CAVEAT (guard branch never entered, all five runs concluded via the deferred post-transition dialog) is now explained, and the candidate cause named there is REFUTED: seam commit `f97717744` is not what changed the route - run `2026-07-28_1939` already carried it (`persisted=True` in its log) and still hit the prefix (`idle detected`). The real cause is a RACE the flake above was pointing at all along, just one seam deeper:

- `InvokeRewind` completes when the re-fly MARKER lands (`TryCompleteInvokeRewind` keys on `ActiveReFlySessionMarker`), but the thing the scene-exit prefix gates on - `ParsekFlight.HasActiveTree` - only becomes true a few hundred ms later, when the `RestoreActiveTreeFromPending` coroutine finishes its in-place-continuation swap + vessel wait and resumes the pending-LIMBO tree as active.
- If `AnswerMergeDialog` drives the `LoadScene` inside that window, the prefix sees no active tree, no switch session, and a pending tree in LIMBO (not Finalized), returns `DialogVariant.None`, and lets the exit through UN-INTERCEPTED - zero prefix lines, conclusion via the deferred dialog. In run `2026-07-28_1939` the resume won by 121 ms (resume 22.282, driven exit 22.403) and the prefix fired; in the five sweep runs the seam won.

The fix is in the SEAM (the driven exit modeled a scene exit faster than any human route - same error class as S4.1-DEFERRED-DIALOG): `AnswerMergeDialogImpl`'s marker-live-no-dialog branch now defers the drive through the pure `TestCommandMergeAnswer.DecideConclusionDrive` until `HasActiveTree` is true in FLIGHT, bounded by `ReFlyResumeSettleBudgetSeconds` (30 s; on expiry it drives anyway and the deferred-dialog fallback concludes, so a restore give-up / placeholder-mode attempt cannot wedge the verb). S4.1's spec now REQUIRES the pre-transition route end to end: `TryAutoDiscardIdleActiveTree: refusing - refly-active` (the S4.1-IDLE-DISCARD guard's first live proof), `Pre-transition tree merge dialog: .* labels=ReFlyAttempt`, and the synchronous `answermergedialog choice=merge result=committed` (the deferred path logs `deferred-dialog answered` instead, so a silent fallback cannot pass).

---

## ~~S4.1-DEFERRED-DIALOG: the driven scene exit skipped the save, so the re-fly marker was swept before any dialog existed~~ [FOUND 2026-07-28 by runs `2026-07-28_1515` / `_1518`. FIXED and PROVEN the same day by run `2026-07-28_1932` (marker survives, sweep spares the provisional). S4.1 now PASSES but FLAKES 1-in-2 on a DIFFERENT residual - the idle-on-pad auto-discard - carried forward as its own item below]

### What happens

`S4.1-rewind-merge` (`LoadGame -> SetSetting -> InvokeRewind -> AnswerMergeDialog -> RecordingState -> FlushAndQuit`, nothing flown between the rewind and the conclusion) fails at step 3 every time:

```
18:19:40.961 [TestCommands] answermergedialog driving re-fly conclusion scene-exit
18:19:40.964 [Flight]       Scene change requested: SPACECENTER at UT=21.40
18:19:41.027 [TestCommands] exec id=0004 verdict=PENDING (two-phase awaiting completion)
18:19:44.451 [Scenario]     Showing deferred tree merge dialog in SPACECENTER      <-- POST-transition
18:21:41.032 [WARN][TestCommands] timeout id=0004 cmd=AnswerMergeDialog deferred=120.0s reason=answer-timeout
18:21:41.033 [TestCommands] exec id=0004 verdict=ERROR
```

Harness side: `drive resp id=0004 verdict=ERROR met=False` -> `driverValidity FAIL subkind=driver-verdict-mismatch` -> `verdict=INVALID`, 366 s over 2 attempts, identical both times.

### Cause: the re-fly MARKER is lost across the scene change, not merely the dialog path

The first diagnosis of this ("the conclusion took the deferred path and the seam can only answer the pre-transition dialog") is TRUE but too shallow - it describes the symptom, not the mechanism. The full chain, every step verified in `logs/2026-07-28_1821_S4.1-rewind-merge/KSP.log`:

1. `InvokeRewind` succeeds and writes the marker IN MEMORY (`Started sess=... provisional=rec_0538... inPlaceContinuation=True`). All four re-fly log contracts fire.
2. The RP quicksave now carries the fixture-authored `RECORDING_TREE`, so `TryRestoreActiveTreeNode` stashes the tree into pending-**Limbo**. Nothing flies, so it never reaches **Finalized**.
3. `AnswerMergeDialog` drives `LoadScene(SPACECENTER)` 0.25 s later. The PRE-transition intercept is skipped because `SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive` requires `PendingTreeStateValue == Finalized` and the tree is Limbo:
   `18:19:43.365 [WARN][Scenario] Deferred merge dialog fired - pre-transition intercept missed scene=SPACECENTER pendingTree=B9 Stack`
4. **No `ParsekScenario.OnSave` runs between the in-memory marker write and the scene change**, so the SPACECENTER `OnLoad` reads `Marker loaded: none` (`18:19:43.360`). This is the root.
5. `LoadTimeSweep` therefore sees no valid marker and discards the provisional as a zombie: `Zombies discarded=1`, `Marker valid=False; ... discarded=1` (`18:19:43.365`), then `ReFlySession End reason=<cleared>` (`18:19:43.392`).
6. The dialog that finally appears is a **PLAIN whole-tree merge dialog** with no re-fly session behind it: `Tree merge dialog: tree='B9 Stack', recordings=3, spawnable=2` (`18:19:44.454`).
7. Both `AnswerMergeDialogImpl` and `TryCompleteAnswerMergeDialog` gate `FindReFlyMergePopup` behind `markerLive` (`scenario.ActiveReFlySessionMarker != null`). The marker is null, so the popup is never found and the answer is never applied - hence `reason=answer-timeout` rather than `AnswerAppliedSceneStall`.

So the seam is not merely pointed at the wrong dialog: by the time any dialog exists, the session it was supposed to conclude has already been swept away. `DecideAnswerCompletion` behaved correctly throughout - it reported an UNAPPLIED answer rather than falsely claiming a committed merge, exactly as its doc-comment anticipates for the post-transition path.

Contrast, same build, same day: `R1-rewind-loop-flown`'s `AnswerMergeDialog` returned OK, because R1 flies a real re-flight so its tree reaches Finalized and the pre-transition intercept fires while the marker is still live. INFERRED for R1 specifically (its `KSP.log` was overwritten and `collect-logs` does not run on PASS); confirm on the next R1 run rather than trusting this note.

### Why it matters

S4.1 is `tier = "nightly"`, and `hlib.CADENCE_TIERS` maps the nightly cadence to `("daily", "nightly")`, so it IS scheduled. A deterministic INVALID burns ~366 s a night and produces no verdict, while the scenario's real purpose - being the dedicated rewind-then-teardown regression case - goes unserved. INVALID is retryable and does not red the sweep as PARSEK-FAIL, so this fails quietly rather than loudly.

### RUN 2026-07-28: the seam fix is PROVEN, and it exposed a different residual

Run `2026-07-28_1932`: **PASS on attempt 2, INVALID on attempt 1** (`flakedThenPassed`, wall 237 s). The save is re-staged from the template between attempts (`[Stage] stage save=gloops-airshow template=... inject=rewind-b9` appears after the retry line), so attempt 2's pass is from a clean fixture, not attempt 1's leftovers.

**What the fix demonstrably achieved**, comparing the same log lines before and after:

| | pre-fix (`_1515`) | post-fix (`_1932`) |
|---|---|---|
| `SafeWritePersistent ... dest=SPACECENTER` | absent | **fires** |
| marker at SPACECENTER `OnLoad` | `Marker loaded: none` | **`Marker loaded: sess_365b9d4a...`** |
| load-time sweep | `Marker valid=False; discarded=1` | **`Marker valid=True; spare=1 discarded=0`** |
| `outcome=refused-unflown-provisional` | absent | **fires** (the newly-required contract) |

So the marker now survives the driven scene change and the sweep spares the provisional, on BOTH attempts. That half is settled.

**The residual is a DIFFERENT cause, and it is more interesting than the first.** On attempt 1 the scene exit never produced a dialog at all:

```
19:37:22.405 [Flight]     IsActiveTreeIdleOnPad: all 4 recordings within 30m - idle on pad
19:37:22.405 [SceneExit]  TryAutoDiscardIdleActiveTree: idle detected dest=SPACECENTER
                          - tearing down live tree without finalize/stash
19:37:22.406 [Flight]     AutoDiscardActiveTreeCore: discarding live tree
```

The tree is torn down WITHOUT stash, so `pend.tree=-`, so no merge dialog spawns, so `AnswerMergeDialog` waits 120 s for a dialog that will never exist. On attempt 2 that discard did not fire (0 occurrences) and the ordinary Limbo stash + deferred dialog path ran to completion.

**This is plausibly the product question S4.1 exists to ask.** `TryAutoDiscardIdleActiveTree` does not consult the live re-fly marker, and S4.1's premise - rewind to PRELAUNCH, conclude without flying - sits exactly on the idle-on-pad boundary, which is why it lands on either side run to run. When it fires during a live re-fly session, the session's tree is silently discarded and the marker is left live with nothing to merge. Whether that is correct (an empty attempt SHOULD be discarded) or a defect (the session then dangles with no conclusion) is a product decision, NOT a harness one, and it should be settled before S4.1 is trusted as a nightly gate.

**Harness state:** `[Coverage] flake quarantine scenario=S4.1-rewind-merge stage=run rate=0.75 over 7d`. That rate spans pre-fix history, so it is not a verdict on the fixed code, but the scenario IS quarantined today.

### FIXED in the SEAM, not the product

`ParsekTestCommandAddon.AnswerMergeDialogImpl` now calls
`SceneExitInterceptor.SafeWritePersistent(GameScenes.SPACECENTER)` immediately before its
`HighLogic.LoadScene`, so the driven exit is production-shaped and the marker survives into
the destination scene.

**Why the seam and not the product** - the first framing of this entry had this backwards.
`AtomicMarkerWrite` assigns the marker IN MEMORY only; durability comes from a later
`ParsekScenario.OnSave`. In production one ALWAYS runs before a scene exit: stock
`saveAndExit` saves BEFORE the `LoadScene` prefix fires (see the comment in
`SceneExitInterceptor.TryAutoDiscardIdleActiveTree`), and `SafeWritePersistent` exists
precisely to cover the stock routes that do not. The seam's raw `LoadScene` therefore
modelled a scene exit **no stock UI route performs** - the same error class as the R1
fixture omitting `RECORDING_TREE isActive=True`: a DRIVEN flow diverging from production
shape and presenting as a product defect.

**The sweep is NOT the defect this scenario was catching.** To the product, an unpersisted
marker plus a `NotCommitted` provisional at OnLoad is indistinguishable from a crash
mid-re-fly, and discarding it is the designed recovery (`LoadTimeSweep` header, design
6.9). Making the marker durable at INVOKE time would make crash semantics WORSE: a crash
mid-re-fly would resurrect a dead session instead of cleanly restoring the pre-rewind
state. `MergeJournalOrchestrator` / `LoadTimeSweep` semantics are untouched by this fix.

### Options considered and rejected

1. Drop the `markerLive` gate on the popup finder for the deferred case. REJECTED as a confirmed false green: after the sweep the session is `<cleared>` and the dialog is a plain whole-tree dialog over an ORPHANED Limbo tree (`hasOrphanedLimboTree=True` at `18:19:43.364`), so answering it merges the wrong thing while the scenario's subject no longer exists.
2. Re-tier S4.1 to `operator` until a real fix lands. REJECTED because the fix was one call away, and this forfeits the only scheduled coverage of a path the product now explicitly supports - `SupersedeCommit`'s comment block states that rewind-then-conclude "reaches it in normal play", and that graceful zero-row completion is one careless revert from regressing.
3. Give S4.1 a flight between the rewind and the conclusion. REJECTED: it makes S4.1 a near-duplicate of R1 and destroys the rewind-then-conclude-WITHOUT-flying coverage it uniquely holds.

### Spec changes that had to ride the fix

Fixing the driver alone would have left the spec asserting things S4.1 cannot do:

- `expectedFail.bugId` / `subkind` DELETED. The signature they named no longer exists in the code, so they could never match - and a key that cannot match silently demotes any UNRELATED expectation-subkind failure under a resolved bug id.
- `AppendRelations outcome=refused-unflown-provisional` ADDED to `logContracts.required` - the positive assertion of what this scenario uniquely covers.
- `[expectations.rewind] supersedeRows` flipped `min = 1` -> `max = 0`. As written it would have RED S4.1 for CORRECT behaviour the day the M-C2 verifier landed. VINDICATED 2026-07-31: the block is now ARMED (`gating = true`), the reading run measured exactly 0, and the negative control that put `min = 1` back reddened the run `PARSEK-FAIL(save-structure)` - i.e. the un-flipped spec would have failed live, precisely as predicted.
- `supersede-relation` D9 claim MOVED to R1 (proven there by `Added 1 supersede relations...`); `head-tip-split` moved to NOBODY and is now honestly uncovered, since no archived run proves any scenario reaches the splitting branch.

### Observability: better than expected, but with a real gap

The first version of this entry said "nothing logs that a modal is open and blocking". That is TOO STRONG and is corrected here. Parsek DOES log the miss, at WARN, naming the path and the scene:

```
[WARN][Scenario] Deferred merge dialog fired - pre-transition intercept missed
                 scene=SPACECENTER pendingTree=B9 Stack (check SceneExitInterceptor or KSP version compat)
```

The sweep is equally explicit (`Marker valid=False; ... discarded=1`, `Zombies discarded=1`, `ReFlySession End reason=<cleared>`). Anyone reading the log AFTER the fact has everything needed.

What is genuinely missing is the LIVE signal: no line says "a modal is now waiting on input that no automated driver can supply". The pending seam command logs nothing per poll, so the blocker is visible only as an ABSENCE - a `journal id=NNNN phase=CLAIMED` with no matching `phase=DONE` - until the budget expires 120 s later. A human watching a live log sees nothing obviously wrong for two minutes, which is exactly what happened here. Cheap fix: have the pending seam command log what it is waiting for on each poll (`waiting id=0004 cmd=AnswerMergeDialog for=refly-popup markerLive=False elapsed=NN/120`), which would have named the `markerLive=False` cause on the first poll instead of after the timeout.

---

## CL-1: the crew-loss atom - the first scenario that kills its subject, the first CAREER fixture with a flyable craft, and two findings a live flight had to produce [LIVE-PROVEN 2026-07-28 flight 2, branch `crew-loss`]

**FLIGHT LOG.** Flight 1 (2026-07-28, `logs/2026-07-28_1913_CL-1-pod-impact`): PARSEK-FAIL,
`expectations` 2 mismatches. Flight 2, same build, fixed fixture: **FULL PASS on attempt 1**,
166 s wall, all seven verifiers PASS/SKIPPED, `recordings.count` observed exactly 1. BOTH
flights killed the kerbal and reached `CREW-LOST` with `MISSION-OK`, so the machine and the
roster channel were right from the first run.

**FLIGHT-1 FINDING (fixed here): a fixture with no `ParsekScenario` node persists NOTHING
through the seam's FLIGHT route.** The flight itself was perfect - 262 points over 108.9 s,
the whole destruction path, Jebediah `state = Dead` on disk - and it produced ZERO recordings
and no `CrewStatusChanged` line. ONE root cause for both: `career-pad-craft` inherited
`fresh-career`'s deliberate absence of a `SCENARIO{name=ParsekScenario}` node, and the seam's
FLIGHT focus route does not run `UpdateScenarioModules` the way the SPACECENTER route does
(known-gate 6 fixed that one). So `ParsekScenario` was never added to the loaded game: not one
`[Scenario]` line in the entire collected KSP.log, `OnSave` never ran, and the whole flight was
recorded in memory and thrown away. Every fixture that has ever flown carries the node; the
only ones without it are the three never-flown `fresh-*` KSC templates. FIXED fixture-side
(the builder splices the donor's inert 7-line node as a third edit and `verify()` refuses a
fixture without one), which is the right layer for a fixture meant to look like a save KSP
itself wrote. NOT fixed product-side: the FLIGHT route's asymmetry with the SPACECENTER route
is real and would bite the next hand-authored fixture the same way - worth its own diagnosis.

**THE MEASUREMENTS THE LEDGER EXTENSION WAS BLOCKED ON, taken by both flights and IDENTICAL
across them, so they are reproducible rather than incidental:**
- stock reputation on the death: `Added -9.999828 (-10) reputation: 'VesselLoss'.` So stock
  KSP DOES penalize a crew death, nominal **-10**, and **the reason key is `VesselLoss`, not
  `CrewKilled`**. That settles a contradiction the repo carried against itself:
  `GameActions/PostWalkActionReconciler.cs` mapped `ReputationPenaltySource.KerbalDeath ->
  "CrewKilled"` and was WRONG;
  `docs/dev/done/research/reputation-reservation-not-warranted.md:133-134` said `VesselLoss`
  and is right. Neither had ever been pinned against the enum. ~~FIXED~~ - the mapping now
  reads `"VesselLoss"`, and the direction was re-verified independently of the audit before
  changing it: the archived flight log
  (`logs/2026-07-28_1913_CL-1-pod-impact/KSP.log:10660`) carries the measured line, and
  `Assembly-CSharp.dll` has no `CrewKilled` member on `TransactionReasons` at all (the string
  sits in the `GameEvents` block as `onCrewKilled`, while `VesselLoss` sits in the
  `TransactionReasons` block next to `ContractReward` / `ContractPenalty` / `VesselRollout`).
  The old key was therefore unpairable by construction and produced a false
  "missing earning channel" WARN on every crew death. Pinned by
  `Source/Parsek.Tests/PostWalkActionReconcilerTests.cs` (the per-case
  `PostWalkActionReconciler` suite, audit item A2's unit tier).
- progress milestones on a 12 km crewed hop: `RecordsSpeed` funds=4800, `FirstLaunch`
  funds=800, `RecordsAltitude` funds=4800, all `rep=0.0`, plus two
  `Added 0.9999995 (1) reputation: 'Progression'`.
- produced career pools: funds 529600 (seed 500000), sci 100, rep -7.99982834
  (= -9.999828 + 1 + 1; the arithmetic closes exactly).

**What it is.** A crewed pod launches, does not deploy a chute, and hits the ground. The
crew dies. That is the whole scenario (`harness/scenarios/CL-1-pod-impact.toml`, mission
`cl1_pod_impact`). Its job is to make ONE thing gated for the first time: what
Parsek RECORDS when a kerbal dies. The LEDGER half was cut - see below; that cut is the
main finding.
Deliberately out of scope, so it stays the ATOM other crew-loss scenarios extend: EVA,
any parachute, re-fly / rewind / tombstones, multiple crew, any new seam verb.

**No new seam surface was needed.** Every other "wait for a physical outcome" in this
harness needed a bounded seam verb (`EvaChuteDeploy`'s `awaitDown`, `EvaExit`'s
`settleSeconds`). Here the MISSION SUBPROCESS is the wait: mlib's phase machines already
poll kRPC per frame, so the fall IS the mission and the seam tail is just FlushAndQuit
(the CommitTree step was cut - see below). What WAS added is a telemetry channel, not a verb: `ACTION_SET_ROSTER_WATCH`
plus `TelemetrySnapshot.crew_roster_status`, read from
`SpaceCenter.GetKerbal(name).RosterStatus` (both verified present at the PINNED kRPC
v0.5.4, not a newer-kRPC feature).

**The one inversion, and the trap in it.** Everywhere else a vessel-lost terminal is a
FAILURE - `mlib.resolve_flight_verdict` returns MISSION-ASSERT-FAIL on a `loss_reason`
BEFORE the assertions run, so a destroyed craft's residual telemetry cannot satisfy them.
Here the death is the SUCCESS terminal, and it has to be: an unmet mission makes the run
driver-INVALID, which SKIPS every verifier below it - the analyzer, the log contracts, the
recording count - so a scenario whose whole subject is "what does Parsek record when a
kerbal dies" would collect no evidence at all. (The original authoring reason was the
CommitTree step - the kerbal-death ledger row is created at COMMIT time via
`LedgerOrchestrator.CreateKerbalAssignmentActions` -> `KerbalsModule.PopulateCrewEndStates`,
`TerminalState.Destroyed` -> `KerbalEndState.Dead` - but that step was CUT as unreachable,
see below; the verifier-skipping argument is the one that survives the cut.) So `cl1_decide` has its OWN terminal (`CL1_CREW_LOST`):
done, NO `loss_reason`, verdict left None. The guard the inversion removes is replaced
rather than dropped - the terminal reads the KERBAL's roster status (a property of the
kerbal, not of the wreckage), debounced over 2 frames, and gated on the kerbal having
been OBSERVED alive and aboard first.

**What the archived dead-kerbal run already proved headlessly**
(`logs/2026-07-25_1310_EVA-4-atmo-chute`), and what it could not:
- PROVED: the destroy-path token set and its exact wording (`[RecState]
  OnVesselWillDestroy:entry`; `[FinalizerCache] Destroy-reason override: ...
  reason=destroy_event`; `[Recorder] Active vessel destroyed during recording`); that the
  commit path derives the death (`PopulateCrewEndStates: ... crew=1 aboard=0 dead=1`) and
  creates the action (`CreateKerbalAssignmentActions: 1 crew members`); and that a kerbal
  death emits NO `[Parsek][ERROR]` line (the run's single ERROR is the EVA-4 seam verb's
  own assert-fail, which CL-1 does not drive).
- COULD NOT PROVE: (a) it was an EVA KERBAL impact, not a CREWED POD crash - the pod-crash
  path is unexercised; (b) it was SANDBOX, so the ledger half is untouched by it; (c) the
  crew transition it shows is TWO-step (`Assigned -> Dead -> Missing`) only because
  `b1-pad-craft` carries `MissingCrewsRespawn = True`; every `fresh-*` fixture carries
  False, so on a career fixture the kerbal settles at Dead and the second line never
  appears. CL-1 pins the FIRST hop for exactly that reason, and its machine accepts
  Dead / Missing / NotInRoster so the contract is not tied to one save's difficulty flag.

**Fixture, forged by construction.** `harness/fixtures/saves/career-pad-craft`, built by
`harness/tools/build_career_pad_craft.py` (no KSP launch, no forge flight, `--check` mode
re-verifies the committed bytes). It is `fresh-career` with exactly two edits:
`b1-pad-craft`'s Jumping Flea VESSEL node spliced into the empty FLIGHTSTATE (its
`type = SpaceObject` asteroid dropped), and the crew kerbal's roster row replaced by
`b1-pad-craft`'s `state = Assigned` one. The craft is byte-identical to B1's, so B1's
MEASURED profile applies verbatim (peak ~11,965 m, terminal -301 m/s by ~2,700 m, ~120 s
hop, one `.prec`). This also closes roadmap item **R11** ("a CAREER fixture with a flyable
craft"), which the roadmap proposed closing with a `FORGE-career-pad` FLIGHT.

**THE LEDGER HALF IS NOT IN THIS SCENARIO, AND THAT IS THE MAIN FINDING.** CL-1 was
authored with a `CommitTree` step and an `[expectations.ledger]` block, on the correct
reasoning that the kerbal-death ledger row is created at COMMIT time. Both were CUT after
the review panel proved the commit is UNREACHABLE on this exact profile:

- When the ACTIVE RECORDED vessel is destroyed in tree mode,
  `ParsekFlight.OnVesselWillDestroy` takes `DestructionMode.TreeAllLeavesCheck`
  (`ParsekFlight.cs:3125`) and `ShowPostDestructionTreeMergeDialog` finalizes the tree,
  STASHES it as the PENDING tree, and nulls `recorder` and `activeTree`
  (`ParsekFlight.cs:3304-3334`), deferring the merge to a scene transition. The seam's
  `CommitTreeImpl` then fails its `HasActiveTree` guard
  (`ParsekTestCommandAddon.cs:1511`) and returns ERROR.
- MEASURED, not inferred, in `logs/2026-07-20_1829_B1-pad-hop/KSP.log` - the same craft,
  the same terminal-velocity impact, the same tail: `:11141` in-tree-mode destruction,
  `:11300` pending tree stashed, `:11310` `[WARN][TestCommands] committree
  no-active-tree`, `:11325` `OnSave: saving 0 committed tree(s)`. `PopulateCrewEndStates`
  and `CreateKerbalAssignmentActions` occur ZERO times in that entire log.
- Both auto-commit routes are gated on `HighLogic.LoadedScene != GameScenes.FLIGHT`
  (`ParsekScenario.cs:3781` cold-load, `:1121` the OnSave safety net), and at the time of
  that run no seam verb produced a FLIGHT -> SPACECENTER transition (roadmap R12).
  `FlushAndQuit` saves and quits from inside FLIGHT, which is why that same log's final
  line is `saving 0 committed tree(s)`. **[R12/A2, built: the `ExitToSpaceCenter` seam
  verb now produces exactly that transition.** It drives the stock exit-button path
  (`SafeWritePersistent` then `HighLogic.LoadScene(SPACECENTER)`) so the finalize / stash
  pipeline runs and the pending tree auto-commits on arrival, and it REFUSES up front with
  `REJECTED msg=dialog-required variant=<RegularMerge|ReFlyAttempt|SwitchSegmentSession>`
  in the states where the exit would instead raise a merge modal that no seam verb can
  answer. The route exists, and the CL-1 spec extension that USES it shipped 2026-07-30 as
  `CL-2-pod-impact-ledger` (stage A: auto-commit + ledger half live-proven; the
  tombstone stage B remains, scoped in the roadmap's R12 block).**]**

An unmet `CommitTree` step would have made the run driver-INVALID, which SKIPS every
verifier below it - the scenario would have produced NO evidence about the crew death at
all, twice (the subkind is retryable). So the commit-dependent tokens and the ledger block
are gone, and a unit cell
(`test_the_spec_drives_no_commit_and_declares_no_ledger_block`) stops them being re-added
without the commit route that makes them reachable.

**THE LEDGER HALF IS THEREFORE THE ATOM'S FIRST EXTENSION.** What it needs, in order:
(1) ~~a commit route out of a destroyed-vessel flight~~ **BUILT (R12/A2)** - the shape to
aim at was `SetSetting autoMerge=true` plus a real scene transition, and that is now
exactly the `ExitToSpaceCenter` verb's documented v1 contract (autoMerge ON is what makes
its wedge guard return Proceed rather than a `dialog-required` REJECTED); (2) the commit-time tokens
re-derived from what THAT path emits, not carried over from the `CommitTree` path;
(3) the career-pool arithmetic, which has a second knowably-unpinned term - a career
FLIGHT trips stock PROGRESS MILESTONES that move pools (the EVA-4 archive carries
`Game state: MilestoneAchieved (standalone) 'RecordsSpeed' funds=4800 rep=1.0 sci=0.0`,
inert in that SANDBOX save, live in a career one). CL-1's own first run MEASURES those,
without asserting them, in its `.analysis.json` careerSave block - which is exactly the
input the extension needs and nobody has.

Also NOT established, and needed before any death-rep constant is declared: nothing in
`Source/Parsek/` ever CONSTRUCTS a `ReputationPenaltySource.KerbalDeath` action (the enum
member is referenced only by the UI label formatter, the post-walk reason-key map, and
deserialization). The `TransactionReasons`-key half of that gap is now CLOSED: the map says
`VesselLoss`, matching `reputation-reservation-not-warranted.md:133-134`, the measured CL-1
line, and the actual `TransactionReasons` member list in `Assembly-CSharp.dll`. Still open:
nothing constructs the action, so the magnitude is still nowhere and the end-to-end
crew-death -> ledger row -> tombstone -> rep-penalty chain still has no flown proof.

**Mutation-checked.** 17 mutations over the CL-1 cells, 16 killed and 1 proved
EQUIVALENT. The survivor is the deletion of the alive-aboard conjunct from the
success terminal: it changes no behaviour, because every not-alive frame is also a
never-aboard frame, so the `crew-watch-never-aboard` terminal always fires first on
any latch-less not-alive run. That is stated at the call site and the invariant it
guards is proved by exhaustion instead
(`test_the_success_terminal_is_unreachable_without_the_latch` walks all 1,296
four-frame sequences over the whole roster alphabet). The two that initially
SURVIVED were both real test gaps and both are now closed: the runner dropping the roster
reading from a `vessel_lost` snapshot (the whole vessel-independence claim), and arming the
watch behind an active-vessel resolve (the fake in that cell was a class with a raising
property, so attribute access returned the property object and never raised).

**Opus review panel (3 reviewers, disjoint mandates, 2026-07-28) - one REAL BUG and two
false-claim fixes.** The bug: `crew-survived-impact` computed its `landed` streak from
`vessel_lost` + `situation` only, never consulting the frame's roster reading, and step 5
pre-empts step 6 only when the death debounce is ALREADY COMPLETE on that same frame. The
real event is STAGGERED, not simultaneous - the wreck settles into LANDED and the roster
flips one ~0.5 s poll later - so the sequence (LANDED+Assigned, LANDED+Dead) completed the
SURVIVED streak one frame before the death streak and would have RED A SUCCESSFUL FLIGHT,
with a reason that contradicted itself inside one line ("with the crew still alive; ...
lastRoster=Dead"). Fixed by adding `not not_alive` to the `landed` conjunct, so any
not-alive frame resets the survival streak. The merge review (2026-07-28) closed the
same hole's UNREAD face: a blind frame also advanced the survival streak, so a channel
that went blind at touchdown completed it (K=2) four frames before the unread give-up (6)
could name it a retryable `roster-channel-lost` flake - `not unread` is now the third
conjunct, per the module's own "a blind frame proves nothing either way" rule, pinned by
three new cells. Also from the panel: `crew-watch-name-unknown`
widened into `crew-watch-never-aboard` (an already-dead-at-load kerbal and an
Available-forever kerbal used to burn the whole FLIGHT budget into an unnamed flake, which
contradicts this module's own stated rule); a new `crew-watch-unnamed` PRELAUNCH terminal
(an empty `crewName` passes the schema, arms a watch the runner treats as UNARMED, and
surfaced as the RETRYABLE `roster-channel-lost` flake - blaming the kRPC channel for a spec
typo); the roster latches registered in `MACHINE_DIFF_FIELDS` so a live run shows
`rosterStatus Assigned->Dead` as a loud gate line (NOT `MACHINE_STATE_FIELDS`, which
renders every field for every mission and would widen all 20 machine lines); the spec's
`flown` tag corrected to `pending-flight` (`--tag` is a real selector); the builder's
`--check` WIRED into `FixtureDriftTests`, which also re-runs the splice and asserts
byte-identity with the committed save, so the "fixture nobody maintains" objection is
answered mechanically rather than in prose; and roadmap item R11 struck as CLOSED with its
two dependents unblocked.

**Open after this lands.** (1) ~~It has never flown~~ - flown twice 2026-07-28, flight 2
FULL PASS (see the flight log above); the tier is promoted to `nightly` and the spec's
tag restored to `flown`. (2) It does not touch the EVA-4 `eva-chute-kerbal-lost` /
`PARSEK-FAIL(mission-outcome)` path or the `EvaExit` standoff wiring; those halves of
known-gate 10 stay open. (3) What the atom is meant to be extended into, in order: a
rewind / re-fly ACROSS the crew loss, which is what would first exercise
`SupersedeCommit`'s kerbal-death tombstones, D9 `tombstones`, and D12 `dead-crew-strip` /
`tombstone-rep-penalty` - all still uncovered. `InGameTests/KerbalRecoveryOnSupersedeTest`
currently AUTO-SKIPS with "No kerbal-death actions in supersede subtree - create a BG-crash
with kerbals aboard before running this test"; CL-1's committed tree is exactly that
subtree.

---

## ~~R1-EMPTY-PROVISIONAL: a Re-Fly session can reach the merge with NO recorder ever bound to its provisional~~ [FOUND by R1 flight 2, DIAGNOSED by R1 flight 3, 2026-07-26. **RESOLVED AS A FIXTURE ARTIFACT 2026-07-28** by the discriminating experiment, run `2026-07-28_1509_R1-rewind-loop-flown` = PASS. The fail-loud gap and the merge non-convergence were fixed independently in PR #1360; layer 1 was correctly never built]

### RESOLVED: the discriminating experiment ran, and it discriminated

`R1-rewind-loop-flown` over the corrected fixture: **PASS**, first attempt, 304 s wall
(`harness/results/2026-07-28_1509_R1-rewind-loop-flown.json`). Every link in the flight-3
failure chain is inverted, and the difference is the FIXTURE, not the product. Measured on
the run's own `KSP.log`:

| Signal | Flight 3 (2026-07-26) | Run 2026-07-28_1509 |
|---|---|---|
| `activeTreeRestoredFromSave` | `False` | **`True`** |
| RP quicksave's tree | absent | `TryRestoreActiveTreeNode: stashed active tree 'B9 Stack' (3 recordings)` |
| launch-guid veto (`conclusively differs`) | fired | **0 occurrences** |
| bug #585 marker swap | refused, `marker-tree-id-mismatch` | **fired**, `swapped target rec='b9-upper-b'->'rec_104c4599...'` |
| re-flight recorded into | `820de77e...` "B9 Slot 1" (NEW tree) | **`tree-b9-stack-root`**, 23 points |
| supersede rows | **0** | **`Added 1 supersede relations for subtree rooted at b9-booster-a`** |
| `[Parsek][ERROR]` lines | present (the throw) | **0** |

So the answer to "is this reachable in production at all", for this route, is **no**. Give
the re-fly a production-shaped RewindPoint - one that carries `RECORDING_TREE isActive=True`,
which every real `GamePersistence.SaveGame` does - and the whole loop works: the tree
restores, `PopPendingTree()` evicts the pre-rewind Limbo stash exactly as it always did, the
guid guard does not veto, the #585 swap binds the recorder to the provisional, the re-flight
records into the origin's tree, and the merge supersedes the branch it was invoked to
replace. The two things that looked like product defects were both artifacts of an injected
quicksave that no production code path can produce.

**Two side-answers the run also settled**, both previously flagged as unverified:

- **KSP DOES preserve an authored VESSEL `pid` (the `Vessel.id` guid) across the ProtoVessel
  load.** Observed (`conclusively differs` never fires) AND confirmed mechanically against the
  decompiled `Assembly-CSharp`: the `ProtoVessel` ConfigNode ctor parses the node's 32-hex pid
  straight into `vesselID` (`if (name == "pid") { vesselID = new Guid(value.value); }`), and
  `ProtoVessel.Load` assigns it verbatim - `if (vesselID == Guid.Empty) { vesselRef.id =
  Guid.NewGuid(); } else { vesselRef.id = vesselID; }`. The ONLY regeneration branch is the
  empty-guid case; there is no collision or dedup check, in deliberate contrast to
  `persistentId`, which DOES have a fallback (`if (persistentId == 0) persistentId =
  FlightGlobals.GetUniquepersistentId();`). Fresh vessel guids are minted only by
  `ShipConstruction.AssembleForLaunch` and `FlightEVA` - genuine new launches - which is
  exactly the launch-identity contract `.claude/CLAUDE.md` states. The contingency lever noted
  earlier (aligning `VesselSnapshotBuilder.ProbeShip`'s snapshot pid) is NOT needed and was not
  applied.
  **LATENT TRAP, still open, for any OTHER fixture.** The guid agreement holds here only because
  `RewindB9Fixture` now sets an EXPLICIT `recordedVesselGuid`, which suppresses the backfill.
  A fixture that does NOT set one gets its recording guid backfilled from the snapshot pid by
  `RecordingSidecarStore.cs:517-528`, and `VesselSnapshotBuilder.cs:221` authors that pid as
  `persistentId.ToString("x8").PadLeft(32,'0')` - a DIFFERENT value from
  `ScenarioWriter.DeriveVesselLaunchGuid`. It would conclusively differ from the RP-stamped
  VESSEL pid and the guard WOULD veto, presenting exactly like a product defect. (This is the
  mechanism that produced flight 3's `00000000000000000000000000030d43`: 0x30d43 = 200003 =
  `ProbeShip(pid: 200003)`.) Smallest fix if it ever bites: have `VesselSnapshotBuilder` derive
  the snapshot pid from `DeriveVesselLaunchGuid(recordingId)` so all three values come from one
  source.
- **The new detection raises produce no false positives.** `outcome=unbound-refly-provisional`
  and `outcome=refused-unflown-provisional` are both absent from a healthy run - they stayed
  silent through a full rewind-and-re-fly loop, which is the only way a fail-loud signal keeps
  its meaning.

**What was kept, and why it still earns its place** even though the finding was a fixture
artifact: the fail-loud gap was real and fixture-independent - a Re-Fly CAN reach the merge
with nothing bound to its provisional and nothing in between objected, which is exactly why a
fixture fault masqueraded as a product bug for two days. Those raises now make the next
occurrence self-diagnosing. The merge non-convergence was also real and independent of all of
this. Both stay.

**Historical record of the wrong turns is preserved below** - two superseded diagnoses and a
reverted fix. Read the corrections before the narrative; the OBSERVATIONS in the original
write-up were sound throughout, only the causal claims were wrong.

**TWO CORRECTIONS, both to earlier versions of this entry. Read them before the rest.**

1. The FIRST version read "rewind then quit without re-flying" and said Release "loses no data ... arguably the correct end state". BOTH WRONG. Flight 3 flew the FULL loop - rewind AND re-fly - and reproduced it exactly; the quit was never required. And the Release end state is not benign: the supersede the feature exists to perform does not happen.

2. The SECOND version blamed "a stale Limbo tree occupying the single pending-tree slot" and proposed evicting it. **That causal claim is INVERTED and the proposed fix would have made things worse.** Caught by the 2026-07-26 review panel and independently re-verified here. Eviction is ALREADY production behaviour: `RecordingStore.PopPendingTree()` is called UNCONDITIONALLY at `ParsekScenario.cs:5158`, with a comment saying it exists precisely to drop the Limbo stash so the freshly-loaded disk version wins. It never ran in flight 3 because there was NO DISK TREE TO RESTORE (`KSP.log:15068`: `savedRecNodes=0, savedTreeRecs=0, activeTreeRestoredFromSave=False, hasOrphanedLimboTree=True`). Emptying the slot would have been actively harmful: `ParsekFlight.cs:13452-13459` yield-breaks on `!RecordingStore.HasPendingTree`, so no adoption path is reached at all - a diagnosable red becomes a silent skip. The OBSERVATIONS in this entry stand; the causal chain below is the corrected one.

### What actually happens

The re-fly session's provisional recording is **never bound to a recorder**, so the re-flight is recorded into a brand-new, unrelated tree. The `empty Points` invariant is only where it surfaces.

Flight 3 (`logs/2026-07-26_2303_R1-rewind-loop-flown/`), in order:

```
23:03:15.740 [ReFlySession] Started sess=... rp=rp_b9_root slot=1
             provisional=rec_5b0697a6cb744dc98af82cb7e8553652 origin=b9-booster-a
             supersedeTarget=b9-booster-a tree=tree-b9-stack-root inPlaceContinuation=True
23:03:16.206 [Flight] RestoreActiveTreeFromPending: waiting for vessel 'Kerbal X'
             (pid=2708531065) to load (activeRecId=9a98f7c366c847d5a81a1bb3da7ab1e6)
23:03:16.206 [Flight] RestoreActiveTreeFromPending: active vessel 'B9 Slot 1' pid=948397159
             liveGuid=... conclusively differs from recording '9a98f7c3...' - skipping pid/name match
23:03:16.204 [RecState] [#38][OnFlightReady] mode=none tree=- rec=- rec.live=F/F
             tree.recs=0/0 pend.tree=b435c4ad:Limbo
23:03:17.207 [Flight] Auto-record started (first staging on pad, stage=7)
23:03:17.207 [Flight] StartRecording succeeded: pid=948397159, chainActive=False, tree=True
23:03:17.207 [RecState] [#45][StartRecording:post] mode=tree tree=820de77e|B9 Slot 1
             rec=f6155f8d|B9 Slot 1|pid=948397159
23:03:21.990 [TestCommands] recordingstate recording=true tree=820de77e857a4b03bb1747ac7d16c994 points=24
23:03:23.976 [Supersede] AppendRelations invariant violation:
             provisional=rec_5b0697a6cb744dc98af82cb7e8553652 reason=empty Points
```

**Which recording got the flight, on disk** (`saves/b2-lko-craft/Parsek/Recordings/`):

| recording | tree | `.prec` size | POINT nodes |
|---|---|---|---|
| `rec_5b0697a6...` (the marker's provisional) | `tree-b9-stack-root` "B9 Stack" | 82 bytes | **0** |
| `f6155f8d...` (the actual re-flight) | `820de77e...` "B9 Slot 1" (NEW) | 5,408 bytes | **58** |

### Root cause

The marker-aware adoption exists - `ParsekFlight.RestoreActiveTreeFromPending` carries the bug #585 "in-place continuation Re-Fly carve-out", which swaps the wait target to `marker.ActiveReFlyRecordingId` via `ReFlySessionMarker.ResolveInPlaceContinuationTarget(marker, tree.Id, ...)` precisely so the re-fly keeps recording into the provisional. It refused, logging `marker present but no swap (reason=marker-tree-id-mismatch)` (`KSP.log:15958`).

**The trigger is FIXTURE-SHAPED, not a stale-Limbo-tree defect.** The chain:

1. The INJECTED RewindPoint quicksave never authors a `RECORDING_TREE isActive=True` node. `Source/Parsek.Tests/Generators/ScenarioWriter.cs`'s `BuildRewindPointQuicksave` rewrites `FLIGHTSTATE` / `VESSEL` only. A PRODUCTION RewindPoint is a full `GamePersistence.SaveGame`, which DOES write one (`ParsekScenario.cs:1856-1858`, `SaveActiveTreeIfAny` -> `RECORDING_TREE` + `isActive = True`).
2. With no tree node in the quicksave, `TryRestoreActiveTreeNode` has nothing to restore, so its unconditional `PopPendingTree()` (`ParsekScenario.cs:5158`) never runs and the pre-rewind Limbo stash survives by default rather than by defect. Hence `hasOrphanedLimboTree=True` with `savedTreeRecs=0`.
3. `RestoreActiveTreeFromPending` therefore ran against that leftover (`tree.Id = b435c4ad`, `activeRecId = 9a98f7c3...`) while the marker names `tree-b9-stack-root` / `rec_5b0697a6...`, so the swap was refused on the tree-id mismatch.
4. A SECOND, INDEPENDENT blocker sits behind it and would have refused anyway: `QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers` (`ParsekFlight.cs:13639-13667`) rejects the candidate when both guids are known and differ. The provisional carries `recordedVesselGuid = 00000000000000000000000000030d43` (a synthetic fixture guid, `persistent.sfs:700`) while the live "B9 Slot 1" is `cede10704d2049dcac1d1beccd3031fa` (`:1255`), same pid `948397159`. With `activeRecGuidRejected` true, BOTH the pid match and the name match are skipped.
5. The scene reached `OnFlightReady` with `mode=none tree=- rec=-`, and the relaunch staging took the plain auto-record path, which creates a fresh tree by construction.

### PROVEN / NOT-PROVEN

- PROVEN by this run: the swap was refused (`marker-tree-id-mismatch`), no recorder was bound to the provisional, the re-flight landed in a new tree, zero supersede rows were written, and the merge-journal recovery does not converge.
- PROVEN about the trigger: the injected quicksave carries no `RECORDING_TREE`, and the fixture-vs-live guid disagreement independently blocks the match.
- **NOT PROVEN, in either direction: whether this is reachable in production at all.** A real RewindPoint is a full `GamePersistence.SaveGame` and would carry the tree node, and a real re-flight's guid provenance differs from the fixture's. There is no archived Re-Fly against a flight-authored RP to check against. Do NOT describe this as a confirmed player-facing bug until that exists.
- **The narrowing experiment proposed in the previous version ("rewind + re-fly with NO prior commit") CANNOT NARROW ANYTHING and is withdrawn.** Removing the commit empties the pending slot, which hits the `!HasPendingTree` yield-break at `ParsekFlight.cs:13452`: a red proves only that the coroutine skipped, and a green is unreachable. **The discriminating experiment is a FIXTURE change**: make `BuildRewindPointQuicksave` emit `RECORDING_TREE isActive=True` for `tree-b9-stack-root`, AND make the sidecar VESSEL's guid agree with the provisional's `recordedVesselGuid` so the guid guard does not veto the match. Both are required - fixing only the tree node still loses at step 4. (Whether KSP preserves the authored VESSEL guid across the ProtoVessel load should be verified on the run rather than assumed.)
  **BUILT in PR #1360; RUN 2026-07-28 and it DISCRIMINATED - see the RESOLVED block at the top of this entry.** `BuildRewindPointQuicksave` now authors the owning tree as `RECORDING_TREE isActive=True` with `activeRecordingId` = the focus slot's origin, and `StampVesselIdentity` stamps the sidecar VESSEL `pid` from the new deterministic `ScenarioWriter.DeriveVesselLaunchGuid(originRecordingId)`, which `RewindB9Fixture` also sets as each child recording's `recordedVesselGuid` - so both sides agree by construction instead of by luck. Note the mechanism the earlier guid value came through: the recordings never set a guid explicitly, and `RecordingSidecarStore.cs:516-523` BACKFILLED one from the vessel snapshot's top-level `pid` (`ProbeShip(pid: 200003)` -> `00000000000000000000000000030d43`). An explicit guid suppresses that backfill, so the fixture no longer depends on it. STILL UNVERIFIED, exactly as flagged above: whether KSP preserves the authored VESSEL guid across the ProtoVessel load. If it regenerates, the guid guard vetoes again and the next lever is aligning the SNAPSHOT pid (`VesselSnapshotBuilder.ProbeShip`) with the same derived value.
- **The reachable PRODUCTION shape to inspect is the Finalized early-return** at `ParsekScenario.cs:5142-5150`, which returns WITHOUT popping so an in-memory Finalized tree can legitimately survive a load - not the Limbo one the previous version named. Note it has explicit re-fly handling (`ShouldAcceptFinalizedPendingTreeForReFlyRetry`, `ParsekFlight.cs:13446/14228`), so it is a path to AUDIT, not a known defect.

### Release consequence IF the shape is reachable: BOTH branches stay live

**Severity, stated precisely.** The chain below is confirmed sound and build-independent, but the only DEMONSTRATED route to an unbound provisional is fixture-shaped (see PROVEN / NOT-PROVEN), so "a player hits this" is unproven. The fixture-independent statement that IS worth a HIGH is narrower: **a Re-Fly session can reach the merge orchestrator with no recorder ever bound to its provisional, and nothing between refuses.** Two detection points were passed silently on the way - `KSP.log:16775` `RestoreActiveTreeFromPending: ... not active within 3s - leaving tree in Limbo` (a WARN that names the exact failure and is not treated as one) and the `reason=trajectory-missing` sidecar rewrite at OnSave - and the first thing that objected was the merge orchestrator, by throwing. That gap is real regardless of how the provisional came to be unbound.

The `#if DEBUG` throw is Debug-only; everything above is build-independent. In Release `AppendRelations` returns an empty list, the merge completes, and the final state - which is exactly what the flight-3 save shows, since the Debug throw happened after the same decision - is:

- **Zero `RECORDING_SUPERSEDE` nodes in `persistent.sfs`** (verified: `grep -c RECORDING_SUPERSEDE` = 0).
- `b9-booster-a`, the pre-rewind branch the re-fly was invoked to REPLACE, is therefore never superseded and stays effective in ERS.
- The actual re-flight sits in an unrelated committed tree ("B9 Slot 1") with no supersede relation, no chain link, and no reference back to the origin.
- The provisional `rec_5b0697a6...` is stranded in the B9 Stack tree with `mergeState = NotCommitted` and no trajectory.

So the user ends up with **the original branch AND the re-flown branch both live, as two unrelated histories**, plus an empty orphan. That is a correctness bug in the feature's core promise ("this attempt replaces that one"), not a logging bug.

### Debug-only extra: a non-convergent stuck merge

Predicted in the first version of this entry, now OBSERVED. The throw lands at `RunMerge` step 2, after `DurableSave("split")`. `RunFinisher` treats any post-Begin phase as drive-forward, and `CompleteFromPostDurable`'s `Split` block falls through into `Supersede` and re-runs `AppendRelations` against the same empty provisional:

```
[WARN][MergeJournal] RunFinisher drive-forward FAILED at phase=Split: InvalidOperationException ...
  Save is mid-merge and may be inconsistent. Manual recovery: copy save, clear
  scenario.ActiveMergeJournal and scenario.ActiveReFlySessionMarker, reload.
[ERROR][Scenario] OnLoad: exception phase=merge-journal ... journal=...,phase=Supersede
[ERR] Exception loading ScenarioModule ParsekScenario: System.InvalidOperationException ...
```

The save keeps `MERGE_JOURNAL { phase = Supersede }` and `REFLY_SESSION_MARKER`, and the input never changes, so **every subsequent load repeats it**. The ERROR line's own promise, "journal will drive recovery on next load", is false for this condition.

### Does the OnLoad-abort save-wipe risk apply? NO - assessed, with evidence

The known-dangerous class is "a static GameEvent handler NRE aborted OnLoad and WIPED the persistent.sfs recording index". It does NOT apply here, for two independent reasons:

1. **Ordering.** `ParsekScenario.OnLoad` reaches `loadPhase = "merge-journal"` only after the recordings and trees are loaded (`... -> sidecar-reconcile -> rewind-post-load -> merge-journal -> load-time-sweep -> test-batch-reconcile -> rewind-point-reap`). The exception line itself reports `committedRecordings=13 committedTrees=3`, so the store was populated when it threw.
2. **Empirically.** The post-run save retains all 13 recordings and all 3 trees. Nothing was wiped.

What the abort DOES cost is everything after it: **`LoadTimeSweep.Run()` and `RewindPointReaper.ReapOrphanedRPs()` never run**. That is self-reinforcing - the sweep is exactly the thing that would discard a zombie marker and stale RPs, and the exception is what prevents it. Severity stays HIGH on the Release correctness ground above, not on a wipe.

**CLOSED by PR #1360.** The abort had exactly one cause - the `#if DEBUG` throw out of `AppendRelations` - and layer 3 removed it. Both builds now take the named refusal, `RunMerge` completes, the journal reaches its terminal phase instead of persisting at `Supersede`, and the sweep and reaper run. The self-reinforcing loop (the abort preventing the pass that would clean up after it) is gone.

### Fix status (PR #1360)

Three layers were proposed, in priority order. Layers 2 and 3 SHIPPED; layer 1 deliberately did not.

1. **Bind the provisional. NOT BUILT, on purpose.** The proposal was to make the adoption independent of whichever tree owns the single pending slot - either evict a stale Limbo tree, or drive the adoption from `marker.TreeId`. Eviction is already production behaviour (see correction 2 above), and a first attempt at the marker-driven variant was written and then REVERTED: with the only demonstrated route fixture-shaped, it would have been a product change built on an unproven premise, and it would not have made flight 3 green anyway (blocker 4 still vetoes). If the fixture experiment above still reds, this becomes the right next move - but it must be an ADDITIONAL entry point, never a relaxed tree-id gate: `ReFlySessionMarker.cs`'s `marker-tree-id-mismatch` refusal is a genuine stale-marker guard.
   **SETTLED 2026-07-28: the experiment did NOT red, so layer 1 is not needed and should not be built.** The run proves the adoption path is correct as written: given a production-shaped RP the #585 swap fires, binds the recorder to the provisional, and the supersede lands. The reverted attempt would have added a second adoption entry point to work around a fixture fault. Anyone tempted to revive it should first produce a failing case that is NOT fixture-shaped.
2. **Detect it early and loudly. SHIPPED.** Pure core `ReFlyProvisionalBinding`; both raises emit a grep-stable `outcome=unbound-refly-provisional` Warn and are OBSERVATION ONLY (no control-flow change, for the same unproven-premise reason). Wired at the two points that were passed silently: `ParsekFlight.RestoreActiveTreeFromPending`'s give-up (`EvaluateRestoreGiveUp`, plus a ScreenMessage; the reason token separates `gave-up-on-marker-tree` from `gave-up-on-other-tree`) and `ParsekScenario.EnsureRecordingFilesCurrentForSave`'s `reason=trajectory-missing` rewrite (`EvaluateSidecarRewrite`). The second is independently sufficient: it is the only one that fires when the restore coroutine was never scheduled.
3. **Named, non-throwing outcome. SHIPPED.** `AppendRelations` now logs `outcome=refused-unflown-provisional` and returns an empty list in BOTH builds. The guard is unchanged; only the "this can never happen" modelling is gone. This removes the non-convergent stuck merge described below - and with it the `MergeDialog.Commit.cs:249` ERROR that S4.1's forbidden-ERROR contract was redding on.

Still open from the original note: whether a conclusion with an un-flown provisional should reach the merge orchestrator at all.

### Standing regression coverage

- SCENARIO, scheduled: **`S4.1-rewind-merge`** is the rewind-then-conclude case (`LoadGame -> InvokeRewind -> AnswerMergeDialog -> RecordingState -> FlushAndQuit`, no flying between). It KEPT `expectedFail.bugId = "R1-EMPTY-PROVISIONAL"` + `subkind = "expectation"` until 2026-07-28 (the keys are now REMOVED - see the end of this bullet): the known finding demoted to EXPECTED-FAIL on the nightly, a DIFFERENT failure still reds as PARSEK-FAIL, and a fixed finding reports XPASS and the keys must be deleted.
  **FIXTURE CONFOUND, flagged rather than resolved:** if the operative trigger is the fixture's tree-less RP (see PROVEN / NOT-PROVEN), then this tag permanently demotes a FIXTURE red on a nightly cadence, and the XPASS that is supposed to signal "Parsek fixed it" would never fire from a Parsek fix - only from a fixture fix. Keeping the tag is still the better of the two available options today (an untagged S4.1 reds the nightly on a known issue), but it is provisional: re-evaluate the moment the discriminating fixture experiment above runs. If that experiment shows the fixture was the trigger, this belongs on a FIXTURE defect id, not on a Parsek one.
  **KEYS: kept at first on the morning's evidence, REMOVED later the same day once the blocker was diagnosed - the final state is REMOVED.** S4.1 was run to observe the predicted XPASS and did NOT reach it: `verdict=INVALID` on `driverValidity FAIL subkind=driver-verdict-mismatch`, deterministically, both attempts (runs `2026-07-28_1515` and `_1518`, wall 366 s; collected logs `logs/2026-07-28_1818_S4.1-rewind-merge/` and `_1821_`).
  **CORRECTION (2026-07-28, verified in source).** An earlier version of this bullet claimed a second reason to keep the keys: that `SupersedeCommit.cs:1124`'s Site B-1 slot-lookup fallback is a reachable `ParsekLog.Error` with no throw behind it, which would trip the forbidden-ERROR contract independently. **That is WRONG on S4.1's path.** The fallback is guarded: `if (IsInPlaceContinuation(marker, provisional))` logs at **Verbose**, and the `ParsekLog.Error` is the NON-in-place branch. S4.1's marker is `inPlaceContinuation=True` (`Started sess=... inPlaceContinuation=True`, `18:19:40.744`), so it takes the Verbose branch. The claim was made from a line number without reading its guard - the same mistake, in miniature, as the two superseded diagnoses above.
  So the ONLY surviving reason to keep the keys is the honest one: the run never reached PASS, so the predicted XPASS is untested. A subsequent review argues they should come OFF entirely rather than wait for an XPASS, on the grounds that the signature they name (`AppendRelations invariant violation ... reason=empty Points`) no longer exists in the code at all, so the keys can never match and would instead silently demote an UNRELATED expectation-subkind failure under a resolved bug id. That reasoning PREVAILED: the keys were REMOVED in the same commit as the S4.1 seam fix, once the deferred-dialog diagnosis established that the blocker was a driver defect and the predicted XPASS could therefore never arrive through it - waiting on an XPASS that is structurally unreachable is not a guard, and a key that can never match is active masking. The blocker itself is a separate defect - see the S4.1 deferred-dialog entry below. What the run DID confirm about the merge fix: `AppendRelations invariant violation` appears 0 times and the only `[Parsek][ERROR]` in the whole run is the seam reporting its own timeout, so the throw removal holds on this path too.
  **(superseded reasoning, kept for the record) KEYS DELIBERATELY KEPT by PR #1360, with an expectation recorded.** That PR removes the throw S4.1 was actually redding on: `AppendRelations` no longer throws, so it no longer escapes `MergeJournalOrchestrator.RunMerge`, so `MergeDialog.Commit.cs:249` no longer logs the `[Parsek][ERROR]` that trips this spec's `logContracts.forbidden`. S4.1 is therefore EXPECTED to report XPASS on its next scheduled run, and that observed XPASS - not this prediction - is the trigger to delete both keys. They were not deleted pre-emptively: `classify_expected_fail` makes XPASS a non-failure, so leaving them costs nothing but a loud signal, whereas deleting them on an unverified prediction would red the nightly if anything else in that path errors. This also satisfies the "re-evaluate once the fixture experiment runs" instruction above rather than pre-empting it.
- SCENARIO, unscheduled: **`R1-rewind-loop-flown` deliberately does NOT carry the tag.** It is `operator` tier and therefore in no cadence (`hlib.CADENCE_TIERS` maps operator to nothing), so it cannot redden any sweep, and its red is the loudest evidence we have that the bug is open on the PRIMARY use path - the full rewind-and-re-fly loop. Tagging it would demote exactly the signal worth keeping, and would leave a key to remember to delete. Its `Added [1-9][0-9]* supersede relations` logContract is the positive assertion that fails today and passes the moment the fix lands.
- HARNESS-SIDE HONESTY (2026-07-26 review, SF-1). `postRewindPointsRecorded` was RENAMED to `postRewindFlightRecordedSomewhere` because it did not observe what it named: `RecordingState.points` is `RecorderStateSnapshot.bufferedPoints`, the LIVE recorder's count for whatever recording is live, NOT the provisional's. Flight 3 is the proof - the row logged `met=True value=24 channel: observed` while the merge was refusing `provisional=rec_5b0697a6... reason=empty Points`. The reply's `tree` field is now captured as the row's `recordedTree` evidence (one `_r1_seam_payload` call would have put the flight-3 diagnosis straight into the result JSON instead of only KSP.log), and the row carries an explicit `doesNotProve` detail. The comparison itself remains IMPOSSIBLE through the seam - no verb exposes the marker's provisional id - so this is diagnosability, not semantic closure; the `Added [1-9][0-9]* supersede relations` logContract is what actually gates the fact.
  REVIEWER NOTE WORTH PRESERVING: one reviewer called the row under-guarded, another's mutation sweep showed it HAS row-level coverage. Both are right on different axes - the sweep pinned the BOOLEAN, the critique tested what the boolean MEANS. The fix was to correct the semantics and KEEP the coverage, not to average the two verdicts.
- MACHINE: `R1LoopClosureTests.test_a_rewind_without_a_second_flight_does_not_evaluate_green` and the shell cell `test_a_rewind_with_no_second_flight_is_not_mission_ok` pin the flight-2 shape; `test_flying_is_not_enough_the_flight_must_be_RECORDED` pins the flight-3 shape (the craft flew, the recording stayed empty), which is the harness-side guard that would have caught this without a merge.
- MACHINE (PR #1360): `ReFlyProvisionalBindingTests` (15 cells) pins both detection points, including that ordinary sidecar churn (`epoch-mismatch` / `schema-mismatch` / ...) must stay SILENT - a raise that fires on everything stops meaning anything - and that the two points fire independently on one session. `SupersedeCommitTests.AppendRelations_{EmptyProvisional,NullTerminalProvisional}_RefusesAndWarns` pins the named outcome in both builds and asserts the absence of the old "invariant violation" wording. `RewindB9FixtureTests.Inject_RpSidecar{CarriesTheTreeAsAnActiveResumeNode,VesselGuidsAgreeWithRecordedVesselGuid}` + `DeriveVesselLaunchGuid_IsDeterministicAndParsesAsAGuid` pin the production-shaped sidecar so the fixture cannot silently drift back. All four load-bearing behaviours mutation-checked (suppress the restore raise -> 4 red; raise on any sidecar reason -> 5 red; drop the isActive node -> 1 red; random per-clone guid -> 1 red).

## Rewind-in-flight: a verb-agnostic seam bridge, the R1 rewind-loop mission, and two wrongly-operator-tiered scenarios [BUILT, branch `rewind-loop-lane`]

Three things, one of which is a correction to what this doc and two scenario headers asserted.

**1. THE PREMISE THAT WAS WRONG: "the seam has NO flight-entry verb."** S1.5, S4.1 and the B9 entry below all justified `tier = operator` with it, and it was never true. `LoadGame` IS the flight-entry verb: `ParsekTestCommandAddon.LoadGameImpl` realizes the `.sfs`, and `TestCommandLoadGame.DecideLoadRoute` takes the FOCUS route whenever `game.flightState.activeVesselIdx` names a focusable vessel - `FlightCameraReloadPin.Arm` + `StartAndFocusVessel`, landing in FLIGHT. The `NoVesselSpaceCenter` route is only the fallback for the vessel-less career fixtures. And `fixtures/saves/gloops-airshow` is NOT a SPACECENTER host: it carries `activeVessel = 1` over two `VESSEL` nodes. LIVE EVIDENCE rather than code reading: EVA-1 (tier nightly, FULL PASS 2026-07-24) has been driving six `RequiresFlight` verbs from that exact template on every run, and EVA-2 / EVA-3 / S0.5 / S0.6 do the same. **S1.5 and S4.1 are therefore RE-TIERED to `nightly`.** S1.5's other stated blocker ("do NOT schedule before the integration-fixes PR merges", the TimeJump completion-decider fix) is also closed: that is PR #1322, merged as `eb94607dd`. Their first scheduled nightly run IS their live-prove; a systematic failure there surfaces as a per-step TIMEOUT = driver-INVALID (retryable), never a false PARSEK-FAIL.

**2. Generalized, verb-agnostic mid-mission seam commands (additive).** The route-1 bridge could only ever issue `CommitTree` (`_perform_seam_commit` hardcodes the verb and uses the single reserved id `run.py` passes). Added ALONGSIDE it, leaving `ACTION_PARSEK_COMMIT_TREE` / `_perform_seam_commit` untouched because five scenarios are live-proven on their exact bytes:
- `mlib.ACTION_PARSEK_SEAM_COMMAND` carrying `{seam_verb, seam_args, seam_tag}` (a tuple of pairs, not a dict, so `Action` stays frozen/hashable - the `crew` / `landing_config` precedent), plus the pure `seam_command_id` / `format_seam_command_line` / `seam_command_poll_seconds`.
- `mission_runner._perform_seam_command(verb, args, tag)`, sharing the reserved id + channel paths `configure_seam` already wires and the same first-wins response reader. The wire id is `"<reservedId>.<tag>"`: **the C# seam SKIPS DUPLICATE IDS**, so re-using the bare reserved id for a second command would make it a SILENT no-op whose poll expires as a bogus TIMEOUT. `hlib.step_id_for_index` formats `"%04d"` (pure digits), so a `.`-bearing sub-id can never collide with a runner step id.
- The outcome rides three fail-closed UNREAD sentinels on `TelemetrySnapshot` (`seam_command_result` / `_tag` / `_payload` = `""` / `""` / `()`), so no pre-existing mission's snapshot moves. `seam_command_tag` is load-bearing: a phase gate that read only the token would advance on the PREVIOUS command's OK.
- Two-phase verbs get a longer poll window (`InvokeRewind` 420 s, `AnswerMergeDialog` 240 s) because their completion straddles a KSP scene reload; polling those for the commit bridge's 120 s manufactures a TIMEOUT out of a healthy reload.
- ADDITIVITY, stated explicitly: nothing was added to `snapshot_dict` or to `MACHINE_STATE_FIELDS` / `MACHINE_DIFF_FIELDS`. `format_machine_state` emits every `MACHINE_STATE_FIELDS` key unconditionally, so one entry there would move every mission's machine line; `snapshot_dict` is an explicit key list and one entry would move every mission's status file. `seam_commit_result` set that precedent (it is in neither). Cost: R1's seam/observation state is visible in the mission log lines, not in the status-file snapshot block. Gated by unit cells.

**3. `r1_rewind_loop` - the rewind cycle driven from a flight.** `ASCENT (delegated `b2_decide`) -> COMMIT (seam CommitTree) -> STOP (seam StopRecording) -> RECORDER-IDLE (seam RecordingState) -> REWIND (seam InvokeRewind) -> VERIFY -> REWOUND -> RELAUNCH -> LOOP-POINTS (seam RecordingState) -> LOOP-CLOSED` (the chain AFTER the flight-1 and flight-3 fixes; the original five-phase version is what flight 1 flew). No new ascent is written: the machine carries a nested `B2State` and delegates every ASCENT frame to the live-proven `mlib.b2_decide`, propagating a nested flake as a give-up that NAMES the ascent phase that died. Two things are worth reading twice:
- **The advance out of VERIFY is an OBSERVATION, not the verb's OK.** "InvokeRewind returned `verdict=OK`" is a COMMANDED reading and this codebase has been burned five times gating on that shape (the CAPTURE-BURN NodeExecutor, B1's chute, EVA-4's ladder release, the B-DOCK docking AP, and the EVA-4 fail-open, since fixed by PR #1359). So the token only opens the door to VERIFY; the ADVANCE is that **the game clock RAN BACKWARD** - a Rewind-to-Separation loads an earlier RewindPoint quicksave, so kRPC's `space_center.ut` must read lower than the value stamped before the cycle. Nothing in normal flight can do that. Assertion rows: `clockRewound` + `vesselStateChanged` are `channel = observed`; `rewindSeamAccepted` is `channel = commanded` and is listed last, never alone (`all_assertions_met` requires every row, so it can only make the mission stricter).
- **Game-time budgets are unusable past a rewind.** Every other machine bounds a phase with `snapshot.ut - phase_entry_ut > budget`. After a rewind that difference is NEGATIVE forever, so such a budget can never expire and the phase would hang until the mission budget killed it with no named give-up. R1 bounds every post-ascent phase by FRAME count instead. Distinctly named give-ups: `rewind-target-unresolved` (frame 1, before an ascent is burned), `commit-seam-<token>` / `commit-seam-silent`, `stop-seam-<token>` / `stop-seam-silent`, the recorder-never-idle and unreadable-`recording` flakes, `rewind-seam-<token>` / `rewind-seam-silent`, `rewind-not-observed`, the never-climbed flake, and the points-stayed-zero and unreadable-`points` flakes.
- `AnswerMergeDialog` is deliberately NOT a mission action: `TestCommandMergeAnswer.DecideAnswerCompletion` requires the scene to LEAVE FLIGHT, which ends the mission's own telemetry. It is a POST-mission seam step (EVA-4's split), and being world-mutating it is dropped by the default `skipTailOnUnmetMission` on an unmet mission.
- Scenario `R1-rewind-loop-flown` (`tier = operator`, PROMOTE to nightly after one green flight - reason (b), ~1,900 s x retry for a lane that is not yet green).
- Tests: `harness/missions/lib/test_r1_rewind.py` + `test_r1_seam_bridge.py` + the `R1RewindLoopShellTests` block in `test_shells.py`. MUTATION-VERIFIED in two passes (37 mutations, 36 caught). Three cells were GREEN under their own mutation and were FIXED, not excused - one polled with `poll_seconds=0.0` so the loop body never ran and the channel was never read; one seeded a payload-free response so the payload-reset mutation was invisible; one mutation selector had gone STALE after a test rename, which the harness scored as a catch (a nonexistent selector exits nonzero exactly like a red) - every selector is now checked to PASS on unmutated source before its result is believed. The one deliberate non-catch is documented below.
- The single UNCAUGHT mutation, stated rather than hidden: deleting the two OBSERVED assertion rows does not red the SHELL-level cell, because a run that never observed a backward clock flakes in the machine (`rewind-not-observed`) and `resolve_flight_verdict` returns FLAKE before assertions are evaluated. The same mutation IS caught at unit level (`R1AssertionTests`). Likewise the `_r1_seam_payload` tag gate is redundant at every current call site (the token gate already opened the branch), so it is covered by a DIRECT helper cell rather than a machine cell - a future call site that reads a payload without checking the token inherits a proven guard instead of an assumed one.

**FLIGHT 1 (2026-07-26, run `2026-07-26_2212`): `INVALID(autopilot-flake)`, wall 520 s / 2 attempts. It found a REAL ordering defect in the mission, which is what a first flight is for.**

PROVEN LIVE, first try: the generalized seam path drove `CommitTree` mid-flight (`treeCommittedBeforeRewind value=OK met=True`); the delegated B2 ascent reached ORBIT (ap 84,051 / pe 75,784); and the SUB-ID SCHEME WORKED - KSP.log carries `id=0003.commit` and `id=0003.rewind` as separate commands, so REWIND did not advance on COMMIT's OK and neither was swallowed as a duplicate id. `rp_b9_root` also resolved fine (`Keeping session-prov rp=rp_b9_root bp=bp_b9_root`), so the injected-RP scope note above was NOT what bit.

THE FAILURE: `[TestCommands] reject id=0003.rewind cmd=InvokeRewind reason=recording-active`. `TestCommandDispatcher.DecideDispatch` refuses `InvokeRewind` whenever `state.Recording` (= `ParsekFlight.HasLiveRecorderForTagging()`), deliberately - a re-fly reloads the scene and would silently discard a live recording.

ROOT CAUSE, established from the code + the log rather than guessed. `CommitTree` does NOT leave a recorder running; it does the opposite. `ParsekFlight.CommitTreeFlight` stops the recorder (`recorder?.StopRecording()`) and then NULLS BOTH HANDLES (`activeTree = null; recorder = null`), so `HasLiveRecorderForTagging()` is false the instant it returns. What happens is that a NEW recording BEGINS ~14 ms later: the commit leaves the active vessel live and marks its recording `VesselSpawned = true`, and on the next frame `ParsekFlight.TryRestoreCommittedTreeForSpawnedActiveVessel` re-adopts the just-committed tree copy-on-write and starts a fresh `promotion` recording on the surviving 28-part stage. Flight-1 log, in order:

```
22:12:29.393  [Recorder] Recording stopped. 239 points          (CommitTreeFlight)
22:12:29.673  [TestCommands] exec id=0003.commit verdict=OK
22:12:29.681  [RecordingStore] Armed committed-tree restore attempt for 'Kerbal X'
22:12:29.687  [Recorder] Recording started: parts=28, points=0, promotion
22:12:30.402  [TestCommands] reject id=0003.rewind reason=recording-active
```

That promotion is CORRECT Parsek behaviour (commit-then-keep-flying), so the machine adapts to it, not the other way round.

THE FIX, and why it is not just a reordering. Two new phases sit between COMMIT and REWIND: **STOP** (seam `StopRecording` - idempotent-OK, and its executor gates on the SAME `HasLiveRecorderForTagging()` predicate the dispatcher reads) and **RECORDER-IDLE** (seam `RecordingState`, polled until the reply's payload reads `recording=false`). Ordering alone is an ASSUMPTION; the machine now carries the dispatcher's gate as an OBSERVED PRECONDITION and refuses to command the rewind until it has READ the recorder idle. That reading fails CLOSED by construction: `RecordingState`'s `recording` is `ParsekFlight.Instance != null && recorder.IsRecording` (`RecorderStateSnapshot.CaptureFromParts`), a SUPERSET of the dispatcher's `activeTree != null && recorder != null && recorder.IsRecording` - so `recording=false` GUARANTEES the dispatcher's gate is open, while a spurious `true` only costs another probe. Each probe carries its own tag (`state0`, `state1`, ...) because the seam skips duplicate ids. New give-ups: `stop-seam-<token>` / `stop-seam-silent` / the recorder-never-idle flake (which NAMES the promotion) / `state-seam-<token>` / the unreadable-`recording` fail-closed. New assertion row `recorderIdleBeforeRewind` (`channel: observed`), whose value is the READ `recording` field, never the StopRecording verb's own OK.

DIAGNOSABILITY REGRESSION, also fixed. The REWIND give-up used to read "the re-fly never started, or its post-load marker never landed" - a guess, and on flight 1 it pointed the operator at the RewindPoint while the response carried the real answer. A non-OK seam response now surfaces PARSEK's own `msg` reason verbatim (percent-decoded via `decode_seam_value`) as `Parsek's reason: <reason>`, admits "the response carried no msg= reason" when there is none, and rides `assertions[rewindSeamAccepted].rejectReason` in the result JSON.

RUNBOOK GAP, also fixed. Attempt 1 of the flight died `INVALID tooling-venv (terminal, no KSP boot)` because a fresh worktree has no gitignored `harness/missions/.venv`. `bootstrap_venv` appeared ZERO times in `autotest-status.md`; `python missions/bootstrap_venv.py` is now step 1 of the R1 runbook, ahead of provision.

**OPEN, and it is the remaining scope gap - but NOT a blocker, and flight 1 cleared it as a suspect: nothing can name a RewindPoint a flight itself authored.** `rp_b9_root` loads and resolves correctly in a flown save, so the injected target works; what it cannot be is self-authored. R1 is therefore a rewind-AFTER-a-flight test, not a rewind-your-own-separation loop. Two independent blockers:
- (a) **An ordinary ascent authors NO RewindPoint.** `ParsekFlight.TryAuthorRewindPointForSplit` requires a MULTI-CONTROLLABLE split (`SegmentBoundaryLogic.IsMultiControllableSplit`, controllable = `ParsekFlight.IsTrackableVessel` = SpaceObject / EVA / has `ModuleCommand`). A dropped Kerbal X booster has no `ModuleCommand`, so the split logs `Single-controllable split: no RP`. Only an EVA split or a dock/undock authors one - i.e. the EVA-1/2/3 shape or B-DOCK, not B1/B2.
- (b) **Even then, no seam channel exposes the id.** `ParsekTestCommandAddon.InvokeRewindImpl` matches `RewindPoint.RewindPointId` EXACTLY (else `REJECTED unknown-rp`) and live ids are fresh GUIDs (`RewindPointAuthor.cs`: `"rp_" + Guid.NewGuid().ToString("N")`). `RecordingState`'s payload is `recording / tree / points / scene` only.
  PROPOSED FIX for (b), NOT built here (it needs a `provision.py` DEPLOY to reach the harness instance, and this lane could not fly): add the live RewindPoint id (and its slot count) to the `RecordingState` payload via `TestCommandRecordingState.BuildPayload` - additive, pure, xUnit-coverable, and read-only in an AnyScene verb. The generalized bridge already captures response payload fields, so the Python half is done: a machine can already read a `RecordingState` reply.

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
Rule, unchanged: one token per claimed class, and never loosen a token to keep a
claim.

**R2. Two registry cells cannot be honestly claimed as written. Decide before anyone
claims against them.**
`harness/coverage/registry.toml` D1 `stop-on-switch` describes a decision that does
not exist: `FlightRecorder.VesselSwitchDecision` is `{None, ContinueOnEva,
ChainToVessel, DockMerge, UndockSwitch, TransitionToBackground, PromoteFromBackground}`
with no Stop member (always-tree mode removed it). D3 `surface-body-fixed` does not
name a `ReferenceFrame` member either: the enum has exactly `Absolute`, `Relative`,
`OrbitalCheckpoint`, and the only production symbol carrying that meaning is
`TrackSection.bodyFixedFrames`, a Relative-section sub-surface which therefore overlaps
`parent-anchored-debris`.
Build: delete each cell or redefine it against a real symbol, with the rationale in the
registry comment. The coverage denominator moves, so do it before the next snapshot.

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
  stage B is unwritten) [MEASURED 2026-07-31, run
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
  through. Stage B scope: the R12 residue block in `docs/dev/autotest-roadmap.md`.
- **R10** runtime-handle plumbing so a live tree / vessel / route id can reach a verb
  (today `run.py:1157` substitutes exactly one token, `${runSave}`, and no response
  payload is ever captured) - OPEN. NOTE R12 solved the SPECIFIC instance that
  blocked it worst, without solving R10: `SimulateStockSwitchClick` takes `vessel=`
  (a stable NAME) precisely because a TOML author cannot know the pid a launch will
  mint, the same stable-addressing dodge `InvokeRewind` used. That is a per-verb
  workaround, not the general mechanism.
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
  stage B remains.
- **R13** widening `SINGLE_BATCH_SELECTOR_RULE` to N categories with N pinned
  tallies - OPEN.
- **R14** provisioning `modded-compat` for D17 - OPEN.

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
   Fix: correct the two stale rows to match the rest of the file.
2. `harness/fixtures/saves/bdock-station-craft/` is an orphan: no spec LOADS it (no
   `saveTemplate` points at it). It IS named in a provenance comment at
   `BDOCK-1-station-interceptor.toml:97`, whose own `saveTemplate` is
   `bdock-station-pad`, and by `harness/tools/harvest_bdock_station.py` plus the
   design doc. Decide keep or delete; if delete, drop that comment reference with it.
3. `S1.5-rewind-loop.toml:3-8` and `S4.1-rewind-merge.toml:3-9` state a "gloops
   SPACECENTER host" premise that the `LoadRoute` contract contradicts. Correct the
   comment or replace it with an R3 measurement.

---

## Playback was never gated, and the parity oracle had been measuring nothing [BUILT, branch `autotest-render-parity`]

Found 2026-07-26 while wiring the first playback scenario. Two independent problems, both about the same blind spot.

**1. Nothing gated PLAYBACK.** Every committed scenario verifies RECORDING (a flight happens, Parsek writes a recording, the analyzer / invariants / ledger oracle judge what was written). Whether the geometry Parsek RENDERS for a committed recording matches what it RECORDED had no automated gate, even though the oracle for it has existed and been production-wired for months (`Source/Parsek/MapRender/RenderParityOracle.cs` + `MapRenderProbe.ComputeFaithfulOrbitParity` / `ComputeSynthesizedConicParity`, diffing the live `OrbitDriver.orbit` that drives the icon and the orbit line against the recorded `OrbitSegment`, body-relative, scale-derived tolerance) and 25 in-game `GhostMap` tests assert through it. No scenario had ever driven that category: the only categories any spec drives were `GhostPlayback`, `RecordingInvariants` and `RouteRewindTimeline`.

Fix: `harness/scenarios/S1.6-render-parity.toml` (daily, sandbox), reusing S1.4's `gloops-airshow` fixture and injected corpus verbatim (zero new fixture work). It pins `mapRenderTracing` on, drives ONE `RunTests category = "GhostMap"` batch (run.py's `_driven_category` returns the FIRST RunTests category, so a second batch would not be gated at all), and gates on `failed=0` plus a clean anomaly sweep with `allowedAnomalies = []`.

**2. The oracle was measuring NOTHING, and a green sweep could not tell.** S1.4's last flight logged 552 `[MapRenderTrace]` probe frames, every one reading `ghosts=0 sampled=0`, ZERO `faithful-parity summary` lines - and the archived result still read `anomalySweep: {hits: [], status: PASS}`, indistinguishable from a run where the oracle looked and found nothing wrong. The batch also counts an `InGameAssert.Skip` as not-failed, and several parity tests skip on 5+ preconditions, so an all-skip batch reads `failed=0`.

Fix: S1.6 carries a MANDATORY anti-vacuity conjunct in two parts. Structural - a required `BATCH_COMPLETE` contract, so an all-skip batch reds. Semantic - two required `[Parsek][INFO][TestRunner]` measurement lines that `RenderParitySamplerFixtureTest` emits only AFTER every skip precondition passed and a real diff ran on live ghost geometry: `ParitySampler_CapturesHandComputedOrbitGeometry ... refDelta=<m>` (the recorded reference and the rendered orbit were both sampled through the production capture and compared) and `SynthesizedParity_LoopShiftedGhost_PhaseMatched_ZeroDrift ... mismatchedDev=<m>` (unreachable unless the deliberately phase-MISMATCHED negative control ALSO flagged drift, so it proves the comparison can still fail). Plus a forbidden `faithful-parity summary sampled=N overTolerance=[1-9]...`, which is strictly stronger than the `parity-drift` anomaly token because `MapRenderProbe` increments `faithfulParityOverCount` BEFORE consulting the per-pid rate limit.

**FIRST FLIGHT 2026-07-26: PASS, every verifier green, and the oracle demonstrably measured.**

```
BATCH_COMPLETE v1 total=25 passed=14 failed=0 skipped=11 category=GhostMap scene=FLIGHT
Scene eligibility skip summary: skipped=9 currentScene=FLIGHT byRequiredScene=TRACKSTATION:9
ParitySampler_CapturesHandComputedOrbitGeometry: pid=2905038605 sma=800000 ecc=0.00
  iconR=800000 orbitAtLiveR=800000 iconOnLineDelta=0m refDelta=0.0m
SynthesizedParity_LoopShiftedGhost_PhaseMatched_ZeroDrift: pid=2279670454
  loopShift=1100.0 matchedDev=0m mismatchedDev=1049421m tol=1927m
```

The negative control is the load-bearing number: the phase-mismatched arm read 1049421 m against a 1927 m tolerance, ~545x over, so the zero-drift assertion is provably not a circle compared with itself. The structural conjunct was `passed=[1-9][0-9]*` while the numbers were unknown and is now the exact measured line, which additionally reds on a silent pass/skip drift. All 11 skips are accounted for: 9 are the TRACKSTATION-scoped GhostMap tests filtered out of a FLIGHT batch (pinning `scene=FLIGHT` is what keeps a future host change visible), and 2 are documented self-skips from the loop-icon warp cluster (`StateVectorReseed_CalculatePhysicsStatsResnapsIconOntoConic`, `FreshLoopGhostIcon_OnRecordedPhaseAtCreation`) - neither a parity assertion, both naming the xUnit gate that covers them.

**Correction the flight forced:** the probe-level `faithful-parity summary` was documented as never emitted at all on this fixture. That was wrong. Frame 7887 read `ghosts=1 sampled=1` for a CORPUS ghost (pid=2906759605, recId=dock-r2-merged) and the probe emitted exactly one summary: `sampled=0 overTolerance=0 skip.no-recording-or-segments=1`. The line IS emitted; it reaches the probe and then skips for want of a covering recorded segment. It is still correctly excluded as a REQUIRED contract (a `sampled=0` summary proves nothing, and requiring `sampled=[1-9]` would red every flight for a non-defect), but the correction cuts the other way for the FORBIDDEN `overTolerance` contract, which is now known to be REACHABLE rather than structurally inert. The synchronous-test reasoning still holds for TEST ghosts - every `GhostMap` test creates its ghost and calls `RemoveAllGhostVessels` inside one frame, so `LateUpdate` never observes one; it was the claim about the corpus that was wrong.

**What S1.6 does NOT prove** (stated in the spec header too): nothing about `Recording.Points` / `TrackSection.frames` / `bodyFixedFrames` (the parity fixtures author 2-point recordings whose TrajectoryPoints are explicitly non-load-bearing; the geometry comes from the OrbitSegment elements); nothing about the flight-scene ghost mesh or `IGhostPositioner`; nothing across TIME (every parity assertion samples ONE frame, and the high-warp canary that would cover a warp step-up is `AllowBatchExecution = false`); nothing about re-aim SOLVE correctness (the synthesized arms check DRAW fidelity only). Follow-up: the same shape over `category = "MapRender"` (22 more tests).

New registry value `D6 recorded-vs-rendered-parity` (growth rule), synced into the catalog markdown.

**Follow-up SHIPPED on the same branch: `S1.7-maprender-parity`, the same shape over the stronger category.** 22 in-game `MapRender` tests, all `Scene = FLIGHT` (so zero scene-eligibility attrition, unlike S1.6 where 9 of 11 skips were TRACKSTATION-scoped): the parity baselines with the typed PhaseChain spine driving, multi-body concurrent ghosts, the re-aimed-loop lens distinction, and the descent / re-stitch / dock-undock / overlap / parent-anchored / BG-on-rails spine cells. LIVE-PROVEN 2026-07-26, `total=22 passed=21 failed=0 skipped=1 category=MapRender scene=FLIGHT`; the single skip is `MapRenderHighWarpCanaryInGameTest`, which carries `AllowBatchExecution = false` and is excluded because the seam drives `RunCategory` (the non-restore variant, `ParsekTestCommandAddon.cs:1497` -> `PrepareBatchExecution`), so no batch ever drives real high warp.

**The SINK TRAP, which changed which arms S1.7 can gate on.** The obvious anti-vacuity candidate is the three-oracle flag-on baseline (`FlagOnParityBaselineInGameTest`, the faithful + synthesized + polyline lens run in one test). It is UNUSABLE, and the reason generalizes: `ParsekLog.Write` calls `TestSinkForTesting(line)` and then RETURNS - it does not tee to `Debug.Log`. So every ParsekLog line a test emits between installing a sink and restoring it in its `finally` is diverted away from KSP.log entirely. `FlagOnBaseline_AllThreeModes: ...` is emitted at `FlagOnParityBaselineInGameTest.cs:225`, inside the try whose finally restores the sink at `:243`, so it lands in the test's own capture list and never reaches the log. Four MapRender test files install a sink (`FlagOnParityBaselineInGameTest`, `FailClosedFaithfulInGameTest`, `ColdLoadClockGuardInGameTest`, `MapRenderTracerCoverageInGameTest`); requiring any of their measurement lines would red every flight. They still gate through `failed=0` - their numbers just cannot serve as the log-visible proof that a diff ran, and cannot be trended.

S1.7 therefore pins the two arms that emit OUTSIDE any sink, both load-bearing rather than "we ran" markers:

- `MultiBodyConcurrentGhostsParityInGameTest` emits one line per ghost carrying the probe seam's own verdict fields, pinned as `sampled=True skip=(none) hasMeas=True ... over=False` on BOTH arms - so neither a precondition skip, a blind lens nor drift can satisfy it. The Mun arm is the one S1.6 has no equivalent for: parity resolves in each ghost's OWN body frame and `ComputeFaithfulOrbitParity` skips with body-mismatch when the rendered body differs from the recorded covering body, so a cross-frame leak surfaces as `sampled=False`. Measured `Kerbin: pid=2361972787 ... tol=1989.4m over=False` / `Mun: pid=1693297703 ... tol=955.8m over=False` - distinct pids and distinct scale-derived tolerances are themselves evidence the two resolved in different frames.
- `ReaimedLoopSynthesizedOracleInGameTest` emits `ReaimedLoop_SynthOracle: pid=3494681962 recordedLAN=0 reaimedLAN=70 | synthDev=0m synthTol=2726m (ZERO) | faithfulDev=1319093m faithfulTol=2701m (FLAGGED)` AFTER both oracle blocks, the second of which asserts `faithful.Result.OverTolerance` is TRUE. That is the sharpest negative control in the codebase: the SAME rendered conic must read ZERO through the synthesized lens (rendered == the intended re-aimed seed) and FLAG through the faithful lens (rendered != the recorded segment, LAN rotated 70 deg). ~488x over tolerance, so the two lenses provably disagree and the comparison can still fail.

S1.7 claims NO new registry value, deliberately - `D6 recorded-vs-rendered-parity` + `D14 sandbox/scene-flight`, exactly S1.6's set. Its value is depth on an axis S1.6 opened, not breadth; inventing a token so the coverage ledger grows would be the same vacuity this scenario family exists to prevent. Also not claimed: `D14 mun` (the Mun arm frames a synthetic ghost orbit in Mun's reference body inside a Kerbin FLIGHT scene - that exercises Mun's frame in the render path, but it is not a flight at the Mun and B5 owns that cell).

## The Tier-C anomaly sweep matched any log line that NAMED a token [FIXED, branch `autotest-render-parity`]

Found 2026-07-26 by S1.7's own first flight, which classified `PARSEK-FAIL(anomaly)` on a run whose in-game batch was completely clean (`failed=0`, `perCategory=1`) and whose KSP.log contains **zero** `phase=Anomaly` lines.

`run.grep_anomaly_tokens` was `if tok in log_text` for each of the seven harness-owned Tier-C tokens, over the entire log. The single hit was:

```
[Parsek][INFO][TestRunner] SpineDrive parity-drift: sampled=True skip=(none) hasMeas=True maxDev=0.0m tol=1989.4m over=False
```

A `PhaseSpineSwapInGameTest` diagnostic whose LABEL happens to be the token and whose body reports the ABSENCE of drift (`maxDev=0.0m`, `over=False`). Any test, comment echo or future message naming a token would red an otherwise clean run - and the more thoroughly a category tests the anomaly machinery, the more likely it is to name one, so the bug got worse exactly where the sweep mattered most.

Fix: the matching moved into `hlib` (it is a DECISION; run.py keeps only a thin delegate) and is now anchored on the raise shape both tracers actually produce - `MapRenderTrace.EmitAnomaly` -> `EmitRaw(phase "Anomaly", "reason=" + Token(reason) + ...)` and `LedgerTrace.FormatAnomaly` -> `"phase=Anomaly ... reason=" + Token(reason)`. A hit now requires `phase=Anomaly` on the line AND the token as the whole `reason=` field. Hits are returned in `ANOMALY_TOKENS` order so the list is deterministic regardless of emit order. Covered by `AnomalyGrepAnchoringTests`, which pins the exact false-positive line, the real raise shape for both tracers, that a false positive cannot mask a real raise in the same log, and that a `reason=` prefix cannot satisfy a different token.

## The harness anomaly token set has drifted from what the mod raises [DEAD-TOKEN HALF FIXED 2026-07-29 branch `harness-fail-open-gates`; the NINE UNGATED REASONS ARE STILL OPEN - wants a per-token decision]

Found 2026-07-26 while anchoring the sweep above; the fix for that bug made this one visible rather than causing it. `hlib.ANOMALY_TOKENS` is described as the fixed harness-owned Tier-C set, but it no longer matches the `reason=` values the mod emits.

**FIXED HALF (2026-07-29).** The DEAD `icon-jump` token is REMOVED from `hlib.ANOMALY_TOKENS` and retired to `hlib.ANOMALY_TOKENS_DEAD`; the two tuples are now disjoint and the source-derived enumeration asserts a retired token is still raised by nothing, so one that gains a producer reds instead of quietly sitting ungated. The removal cannot move a verdict - a token no producer raises can never be a hit - which is exactly why it was safe to do without a calibration flight. What it buys is honesty: the gated set no longer advertises coverage of the icon-teleport family it has never been able to see. Shipped alongside it, a per-token COUNT BUDGET on `allowedAnomalies`:

```toml
[expectations]
allowedAnomalies = ["polyline-orbit-overlap", { token = "icon-teleport", maxCount = 3 }]
```

A bare string keeps its historical meaning (tolerated at ANY count) so all 55 committed specs parse unchanged; the table form reds at N+1. The two are different claims, and only the second catches a regression that turns a rare benign transient into a per-frame storm. `parse_allowed_anomalies` rejects a malformed entry pre-launch (a misspelled `maxcount` that silently degrades to unbudgeted is the fail-open the surface exists to close) and warns on an inert one. NO committed spec arms a budget; `anomalySweep.hitCounts` now records per-token raise counts on every run so one can be sized from a green flight instead of guessed.

**STILL OPEN, unchanged:** the nine ungated reasons below, `icon-teleport` first among them. Nothing about the dead-token removal decides them.

Ground truth, DERIVED FROM SOURCE (not hand-listed): `hlib.ANOMALY_REASONS_RAISED_UNGATED` carries the ungated half, and `AnomalyGroundTruthEnumerationTests` walks every `EmitAnomaly` call site under `Source/Parsek` excluding `InGameTests/`, resolves the reason argument by position for both tracer signatures, and requires the derived set to partition exactly into the gated tuple plus that one. So a new raise site nobody gates reds the harness suite instead of silently widening the fail-open.

| Raised reason | In ANOMALY_TOKENS? | Producer (decision site) |
|---|---|---|
| `parity-drift` | yes | `MapRenderProbe.cs:1193`, `:1449`, `:1627` (via `MapRenderTrace.AnomalyParityDrift`) |
| `line-blink` | yes | `MapRenderProbe.cs:647` |
| `decision-vs-truth` | yes | `MapRenderProbe.cs:579` |
| `polyline-orbit-overlap` | yes | `MapRenderProbe.cs:599` |
| `rigid-seam-tangent-discontinuity` | yes | `MapRender/CrossMemberSeamStitcher.cs:419` |
| `ledger-vs-truth` | yes | `GameActions/KspStatePatcher.cs` x6, `FacilityStatePatcher.cs:158` |
| `icon-teleport` | **NO** | `MapRenderProbe.cs:753` |
| `icon-off-orbit` | **NO** | `MapRenderProbe.cs:834` |
| `unaccounted-drawn-recording` | **NO** | `MapRenderProbe.cs:437` |
| `gap-vs-retire` | **NO** | `MapRender/GhostRenderReconciler.cs:240` |
| `decision-vs-old-truth` | **NO** | `MapRender/GhostRenderReconciler.cs:260` |
| `clock-not-ready` | **NO** | `MapRender/ShadowRenderDriver.cs:316` -> `MapRenderTrace.EmitClockNotReady` (`:1342`) |
| `retire-not-held` | **NO** | `MapRender/ShadowRenderDriver.cs:394` -> `MapRenderTrace.EmitRetireNotHeld` (`:1365`) |
| `anchor-resolve-fail` | **NO** | `MapRender/AnchorFrameResolver.cs:87` -> `MapRenderTrace.EmitAnchorResolveFail` (`:1391`) |
| `factory-parity` | **NO** | `MapRender/ShadowRenderDriver.cs:709` -> `MapRenderTrace.EmitFactoryParity` (`:1416`) |

That is NINE ungated reasons, not five. **The first version of this table listed five**, and the four it missed are the last four rows: the cutover-hardening raises, which reach `EmitAnomaly` through thin once-per-event `MapRenderTrace` wrappers instead of calling it at the guard site, so a grep for `EmitAnomaly` call sites does not land on them. They emit the same `phase=Anomaly ... reason=<token>` line as any direct raise (all four route through `MapRenderTrace.cs:1294` `EmitRaw(true, "Anomaly", ...)`), so all four are genuinely ungated. Understating the ungated count understates the size of the fail-open, which is the one thing this entry exists to size, hence the source-derived gate above. `clock-not-ready` in particular is the cold-load UT<=0 defer - a defect class this project already tracks separately.

And `icon-jump` WAS in the set but is raised by nothing - it is a DEAD token (RETIRED from the gated set 2026-07-29, see the FIXED HALF above). That one matters most: the icon-teleport family is precisely the defect class the map-render wave has spent months chasing, and the sweep has never been able to see it. Before the anchoring fix the token would occasionally "hit" by matching prose (`MapRenderHighWarpCanaryInGameTest`'s own description text contains `icon-jump`), which is a false positive dressed as coverage, not a gate. Retiring it removes the false advertisement; it does NOT add the coverage - that is the `icon-teleport` decision below.

The NINE remain unresolved, deliberately, because reconciling them is a per-token decision (defect signal vs instrumentation signal) rather than a mechanical rename:
- `unaccounted-drawn-recording` is documented in `.claude/CLAUDE.md` as the S0 polyline-COVERAGE instrument, not a defect signal - gating it would red runs for an instrumentation gap.
- `factory-parity` is the same shape: a shadow-only PhaseFactory comparator that never drives a draw, so a fire is a build-bug signal, not a rendered regression.
- `gap-vs-retire` / `decision-vs-old-truth` / `icon-off-orbit` / `retire-not-held` / `anchor-resolve-fail` / `clock-not-ready` each need a call on whether a raise is a defect or an expected transient.
- `icon-teleport` is the one that most likely SHOULD be gated (renaming `icon-jump` -> `icon-teleport`). What blocks doing it here is that nobody knows whether it FIRES on a green run: the only tracer-armed scenario that walks the real 272-tree corpus is S1.4, and every S1.4 flight so far predates the `unlistedReasons` channel, so no archived result records whether an icon-teleport raise happened. Gating it blind could red a live-proven daily on the strength of a rename. S1.4's next nightly is the measurement - `anomalySweep.unlistedReasons` in its result JSON answers it for free - and the rename should follow that number, not precede it.

**What the deferral is NOT.** An earlier draft of this entry justified it with "adding any of them WIDENS the gate for every committed scenario at once, and several run with the tracer armed". That is no longer true after the sidecar baseline in this same change: exactly three specs arm the map tracer (`S1.4`, `S1.6`, `S1.7` each carry `SetSetting mapRenderTracing=true`) and the baseline pins it OFF for the other 27, so every `MapRenderTrace` emit early-returns on `IsEnabled` elsewhere and widening the set can only move those three scenarios' verdicts. The reasons above are the real ones; the blast-radius claim was overstated and is retracted here.

Interim so it is not a silent fail-open: `hlib.unlisted_anomaly_reasons` returns every raised reason absent from the set, run.py warn-logs it (`anomalySweep saw N raise(s) with reason(s) NOT in the harness token set (REPORT-ONLY, not gating)`) and records it in the result JSON under `anomalySweep.unlistedReasons`. Non-gating by construction. Pinned by `AnomalyGrepAnchoringTests.test_icon_jump_is_retired_and_icon_teleport_is_still_only_reported` (which now pins BOTH halves - the retirement that happened and the report-only status that did not change; delete it when `icon-teleport` is decided) and by `AnomalyGroundTruthEnumerationTests` (which stays, and is what keeps this table honest). The budget surface is pinned by `AnomalyBudgetParseTests` / `AnomalyBudgetSweepTests` / `AnomalyTokenCountTests`, plus a whole-set cell asserting no committed spec arms a `maxCount`.

## STOCK_AWARD_PATTERNS were dead against real KSP logs, and nothing ever read a non-Parsek log line [MECHANISMS BUILT 2026-07-29, branch `harness-fail-open-gates`; both land REPORT-ONLY and their ARMING is operator-blocked]

Two fail-open gaps closed at the mechanism level in one change, both deliberately NOT armed. The shared reason for the restraint: no KSP can be launched from the change's own environment and no calibration run was possible, so nothing here may move a committed scenario's PASS/FAIL on the next nightly. Mechanism plus knob, not blind gates.

### 1. The ledger oracle's independence cross-check had never once run

`hlib.STOCK_AWARD_PATTERNS` keyed on shapes nobody had ever seen in a KSP.log - `ContractSystem ... funds=<n>`, `ResearchAndDevelopment ... delta=<n>` - written from the design document rather than from a log. So `parse_stock_award_lines` captured ZERO lines on every run ever flown, `unmatched_captured_awards` got an empty input, and the leg-A capture cross-check (design edge 4, written as a HARD `PARSEK-FAIL(ledger)`) was a structural no-op dressed as a gate. Worse than harmless: the four `StockAwardCaptureTests` cells fed the SAME invented shapes, so the tests and the patterns agreed with each other and both disagreed with the game. That is the shape of a test that can never fail.

The real KSP 1.12 idiom, MEASURED (both CL-1 flights 2026-07-28, identical across them; the `Progression` line independently archived from `logs/2026-04-19_0049_career-ledger`):

```
Added -9.999828 (-10) reputation: 'VesselLoss'.
Added 0.9999995 (1) reputation: 'Progression'.
```

i.e. `Added <appliedDelta> (<nominal>) reputation: '<TransactionReasons key>'`. The leading number is the APPLIED per-event delta (the parenthesised value is stock's rounded nominal); the quoted tail is the reason key. Every enumerated pattern now cites the archived line it came from (`StockAwardPattern.measured_from`), and every test cell feeds a measured literal.

> **CORRECTION, same day - the funds line above was struck.** The first version of this entry also listed `Added 4800 funds: 'RecordsSpeed'` and described one shared `<pool>` idiom. That funds line was never measured; see "The funds half of the fix was itself invented" below. The capture is REPUTATION-ONLY.

Three consequences worth stating:
- **The reason key is now carried** (`CapturedAward.reason`). It is the ONLY identity a stock award line has - there is no guid and no subject on one. CL-1's two `Progression` awards are identical in amount and reason and are separated only by their distinct UTs, which the seqKey supplies.
- **Reputation captures are stamped `repMode = applied`.** The measured `-9.999828` for a nominal -10 IS the curve output, so letting the oracle re-curve it would be the 15.1 double-curve distortion read backward.
- **SCIENCE IS NOT ENUMERATED, and now never will be.** Originally deferred as unmeasured; closed negatively 2026-07-29 once the assembly was checked - there is no science award line to measure. See below.

**WHY IT LANDS REPORT-ONLY, and this is the load-bearing part.** Making the patterns real turns a dormant hard gate live in one step, against scenarios nobody has measured it on. The original reasoning cited a career pad hop tripping `RecordsSpeed` / `FirstLaunch` / `RecordsAltitude` funds awards plus two `Progression` rep awards, none declared by any L1 seam manifest. That is half wrong in a way that matters (the funds awards are never logged, so they cannot surface as captured awards at all - only the `Progression` rep awards can), but the conclusion stands for a different and stronger reason: no L1 spec can capture anything whatsoever. So the cross-check's hardness moved behind a spec knob:

```toml
[expectations.ledger]
captureCrossCheck = "gate"   # default "report"; declared by ZERO committed specs
```

`report` records each unmatched award as a REPORT-ONLY oracle divergence (logged, counted in `reportOnly`, kept in `results/<runId>.manifest.json` `capturedRaw`); `gate` restores the hard `PARSEK-FAIL(ledger)`. OPERATOR-BLOCKED: arm per scenario after one green run shows that scenario's real award baseline. Pinned by `test_unexpected_award_is_report_only_by_default` (the same log that reds when armed must not red by default) and its armed twin. **The "ZERO committed specs" in that snippet held until 2026-07-31**, when `CL-2-pod-impact-ledger` was armed over three flights - it is now the one and only armed spec, and the whole-set cells are allowlists rather than empty-set assertions; see the struck corroboration-key entry above for the evidence.

**AND THE CORROBORATION KEY HAD TO CHANGE WITH THEM (review follow-up, found by reproduction).** The generic captured kinds made corroboration STRUCTURALLY IMPOSSIBLE: `unmatched_captured_awards` joined captured-vs-seam on `(seqKey, kind, identity)`, but a captured award now always carries `stock-funds-award` / `stock-reputation-award` while a seam entry carries a scenario kind (`kerbal-hire`, `facility-upgrade`, ...), so the kinds can NEVER be equal and every captured award reported "unexpected" - including the scenario's own. Reproduced against `L1-hire-kerbal-career`, whose own declared -62113 hire debit read as an unexpected award. The consequence was worse than noise: `captureCrossCheck = "gate"` could never be armed on any scenario that declares anything, so the escalation path documented one paragraph above could not be walked.

Kind cannot be repaired by mapping stock reasons onto scenario semantics - that mapping is exactly the unmeasured inference the generic kinds exist to avoid. What both sides DO carry comparably is the seqKey, the POOL and the AMOUNT, so the key is now:

- `(seqKey, facet, amount within the facet tolerance)`, matched ONE-TO-ONE PER `(entry, pool)` - a match consumes that pair, so one declared effect explains at most one award ON EACH POOL IT DECLARES. Both halves were found by reproduction. The one-to-one half stops two same-size awards at one seqKey from both corroborating a single declared amount; the measured instance is CL-1's two `'Progression'` +1 rep awards. (The original wording used the `RecordsSpeed` 4800 / `RecordsAltitude` 4800 pair; per entry 1b those are funds awards KSP never logs, so they never reach the capture.) The PER-POOL half is the multi-facet case: a contract-complete entry declares funds AND reputation, and consuming per ENTRY let whichever line matched first swallow the whole entry and strand its sibling as permanently unexpected (reproduced: a funds 1000 + rep 5 entry against two lines -> 1 unexpected). That reproduction used a synthetic funds line, so against a real log the rule is fail-closed structure against a future second-pool producer rather than live behaviour; it is kept because the cost is nil and the alternative is a latent stranding bug. `oracle.parse_manifest_entries`' fill path already documents that exact pairing and carries a facet filter for it; this is the same rule on the matching side;
- structured identity (contract guid / science subject) as a FAIL-CLOSED discriminator when BOTH sides carry one, preserving the multi-subject rule (item 10) - a real stock line carries neither, so for funds/rep it never discriminates;
- an OPTIONAL per-entry `stockReason = ["CrewRecruited"]` (additive `ManifestEntry.stock_reasons`, declared by zero specs) that TIGHTENS the match to a named stock effect, for the author who has read a green run's `capturedRaw` and is about to arm `gate`. Pinned entries are tried FIRST: the match is greedy (no bipartite search), so with one pinned and one unconstrained entry of the same amount at the same seqKey, log order used to decide the outcome and could strand the pinned entry - penalizing exactly the author who did the extra work of pinning reasons.

The rep facet needs the tolerance WINDOW rather than an exact compare, and this is not slack: a seam entry declares the NOMINAL delta (-10) while the stock line reports the APPLIED post-curve one (the measured -9.999828). An exact compare would leave every reputation award permanently unexpected.

M-B2 INDEPENDENCE IS INTACT, and is pinned by its own cell (`test_corroboration_never_touches_the_expected_totals`, whose witness is the PINNED 437887 = seed 500000 + the one seam-declared -62113: it re-computes EXPECTED after corroborating over a log carrying five further stock awards worth 10,400 more funds and two rep awards, and requires the number to be unmoved - a bare double-compute would be a tautology over a pure function). Corroboration only CLASSIFIES. A captured amount is still never summed into `compute_expected`, and `diff_expected_vs_parsed` is untouched - the seam-declared-vs-produced-save leg reds identically whether or not an award corroborated. Corroboration can only suppress the extra unexpected-award row; it can never weaken the save-diff leg into self-cancellation.

### 1b. The funds half of the fix was itself invented - the capture is reputation-only

Found 2026-07-29 while preparing the operator calibration for gate 3. The rewrite above corrected the funds/science/reputation table from measured evidence - except that the FUNDS line it enumerated, `Added 4800 funds: 'RecordsSpeed'`, had never appeared in a KSP.log. It was composed by analogy from the reputation line, and its cited source proves it: `CL-1-pod-impact.toml`'s "progress milestones: RecordsSpeed funds=4800" quotes Parsek's OWN `[Parsek][INFO][GameStateRecorder] Game state: MilestoneAchieved (standalone) 'RecordsSpeed' funds=4800` line, and `parse_stock_award_lines` skips `[Parsek]`-tagged lines by design. The rewrite therefore reproduced, on one facet, the exact defect it closed on the others - a pattern and its tests agreeing with each other and both disagreeing with the game. `StockAwardCaptureTests` carried it as a "MEASURED, not composed" literal, and `test_run_smoke` carried a third generation of the same fiction (`Added 1000 funds: 'RecordsSpeed'`).

**KSP LOGS NO FUNDS AWARD AND NO SCIENCE AWARD.** Three independent proofs, all cheap to re-run:

1. **Assembly string table.** UTF-16LE literal counts in `Assembly-CSharp.dll` (KSP 1.12.5): `") reputation: '"` = 1, `") reputation. Total Rep: "` = 1, `" funds: '"` = **0**, `" science: '"` = **0**. A concatenated `Debug.Log` keeps its format fragment as a single literal, so a zero count is conclusive rather than suggestive.
2. **Decompiled bodies.** `Funding.AddFunds(double, TransactionReasons)` and `ResearchAndDevelopment.AddScience(float, TransactionReasons)` mutate the pool and fire `GameEvents.Modifiers.*` with no `Debug.Log` of any kind. Only `Reputation` logs an award.
3. **Field corpus.** All 137 collected `KSP.log`s: zero stock funds/science award lines. Including `logs/2026-07-10_2339_rerun4-green`, a career run that demonstrably credited +4800 `RecordsSpeed` milestone funds (`runningBalance=5600`) - and logged the credit only through Parsek's own lines.

Fix: the funds pattern is RETIRED to `hlib.STOCK_AWARD_PATTERNS_DEAD` rather than left in the live table advertising coverage it cannot have - the same call, for the same reason, as the `icon-jump` retirement in `ANOMALY_TOKENS_DEAD`. Removing it moves no verdict by construction. Science is closed NEGATIVELY: there is no shape to measure, so the standing operator item asking for "an L1 science scenario's collected KSP.log" is withdrawn. The only R&D line stock writes is `[Research & Development]: +<n> data on <subject>. Subject value is <v>` - experiment DATA, not a currency delta, correctly inadmissible and explicitly not to be adopted as a science pattern.

Two capture GAPS are now documented rather than discovered later, and they are NOT the same case. `Reputation.addReputation_discrete` logs `Adding <n> (<r>) reputation: '<reason>'.` - it does carry the `TransactionReasons` key, in exactly the quoted-tail form the live pattern reads, and the only thing excluding it is the verb (`Adding`, not `Added`); a pattern for it is writable and would correlate, and it is absent purely because that line has never been measured in the field. The no-reason `AddReputation` branch logs `Added <n> (<r>) reputation. Total Rep: <total>` (period, not colon) and genuinely carries no reason key, so it has no identity to correlate on at all. Both move the produced save, and the save-diff still catches them.

Consequences that change how gate 3 gets armed:
- **`fill-from-capture` is now unreachable in the field.** It is legal only on the funds facet (science and reputation fills are rejected outright to preserve M-B2 leg independence), and the funds capture pool is provably always empty, so a `null` funds amount ALWAYS fails ambiguous -> hard drift. That is the correct fail-closed outcome; `test_funds_fill_from_capture_is_unreachable_in_the_field` pins it, replacing a cell that had asserted a PASS on a manufactured input.
- **No L1 spec can arm `captureCrossCheck = "gate"` meaningfully.** All six are funds-only, science-only, all-zero or manifest-less, and all six are `scene-ksc` runs that never fly, so none can produce a captured award. Arming there would be a no-op gate reading green forever. Arming wants a reputation-producing scenario; `CL-1-pod-impact` is the only committed spec measured doing so, and it deliberately carries no `[expectations.ledger]` block.
- **Tolerance note.** Stock prints the applied rep delta at 7 significant figures: CL-1's three captured amounts sum to -7.999829 against a save carrying -7.99982834. An exact compare cannot succeed; budget the ~1e-6 display-rounding residual.

### 2. Nothing in the verifier chain read a line Parsek did not write

Every committed spec's `logContracts.forbidden` list carries Parsek-authored tokens only, and the layer below it (`scripts/validate-ksp-log.ps1` -> `ParsekLogContractChecker`) parses ONLY `[Parsek]`-tagged lines: SES-000/001, FMT-001/002, WRN-001, REC-001/003 are about session markers, Parsek line FORMAT, WARN content, and recording start/stop pairing. So a KSP.log full of raw `NullReferenceException` stack traces, or an IMGUI `ArgumentException: GUILayout` storm (one bad OnGUI frame re-emits every frame for the rest of the scene), passed every gate the harness has.

`hlib.scan_unity_exceptions` counts four patterns - `NullReferenceException`, `MissingReferenceException`, `IndexOutOfRangeException`, `ArgumentException: GUILayout` - deliberately as the COMPLEMENT of the validate-ksp-log layer, with no overlap duplicated. `[Parsek]`-tagged lines are skipped: a Parsek line naming an exception is the mod REPORTING a caught one, already covered by the forbidden tokens and WRN-001, and counting both would double-signal one event and make the number uncalibratable. run.py records a `unityExceptions` row in every result JSON - on a PASS (where no collect-logs runs, so this is the only place the number survives) and on a KILLED attempt too, since a kill tears the SAVE, not the log, and an exception storm is a leading suspect for whatever hung the process.

NOT GATING. OPERATOR-BLOCKED arming - though the baseline that was missing now exists: sweeping all 137 collected `KSP.log`s with `hlib.scan_unity_exceptions` (2026-07-29) gives **73 of 137 runs at exactly 0**, median 0, p90 3, max 158; the 158 is one old career outlier (`2026-07-10_2339_rerun4-green`) and the corpus max excluding it is **7** (`BDOCK-1`, the longest flown scenario). Only `NullReferenceException` fires at all (284 total); `MissingReferenceException`, `IndexOutOfRangeException` and `ArgumentException: GUILayout` are ZERO corpus-wide, so the IMGUI-storm pattern the budget was partly designed for has never been seen. The corpus is not segmented green-vs-red and spans many builds, so treat it as an order-of-magnitude floor rather than a per-spec baseline; on it, 8-10 is a defensible first ceiling for heavy flown specs and 3-5 for short KSC/L1 runs. Nothing is armed:

```toml
[expectations.unityExceptions]
maxTotal = 0    # declared by ZERO committed specs
```

Over-budget classifies `PARSEK-FAIL(unity-exception)`. `validate_unity_exception_expectations` rejects a misspelled or non-integer ceiling pre-launch, because a ceiling that silently degrades to report-only is the same fail-open one layer up.

## The harness leaked a diagnostic tracer across every later run [FIXED, branch `autotest-render-parity`]

Found 2026-07-26. `SetSetting` does not only mutate the live per-save GameParameters: eight of the sixteen whitelisted settings (Parsek's `SettingWhitelist` `PersistenceRoute.GameParametersPlusSidecar`) are ALSO written to the INSTANCE-WIDE `GameData/Parsek/PluginData/settings.cfg`, and `ParsekScenario.OnLoad` applies that sidecar OVER whatever the loaded save carries. So S1.4's `SetSetting mapRenderTracing=true` pinned the per-frame map/TS render tracer ON for every later run on the automation instance - including multi-thousand-second landing flights that never declared it, which paid the tracer cost and were gated by an anomaly sweep they never asked for. Confirmed on the live instance: the sidecar contained exactly `mapRenderTracing = True`.

Second half of the same problem, found while fixing it: EIGHT of the eleven committed fixture saves also carry `mapRenderTracing = True` inside their own GameParameters `ParsekSettings` node (they were harvested from a dev save with tracing on), so the tracer had in fact been on for those fixtures' flights even BEFORE S1.4 first ran. Deleting the sidecar therefore does not fix it; only an explicit stored OFF does, because the sidecar is the layer that wins over the save. (An earlier draft said EVERY fixture save; it is 8 of 11. The three without the key are `fresh-career`, `fresh-sandbox` and `fresh-science` - the synthesised career/sandbox/science templates, which were not harvested from the dev save. Immaterial to the fix, which pins the tracer OFF regardless of what the save carries, but it was stated as universal.)

Fix (harness-side, no rebuild or re-provision needed): `run.reset_settings_sidecar` writes a deterministic tracers-OFF baseline (`ghostRenderTracing` / `mapRenderTracing` / `ledgerTracing` = False, and nothing else, so the other five tracked settings stay unset and the fixture's own values keep governing them) into the sidecar at STAGE, before launch, and again at TEARDOWN in `run_attempt`'s `finally`. Both calls write the SAME baseline rather than restoring a captured prior state - restoring the prior state would faithfully preserve the previous run's contamination. Robust to a budget-watchdog process-tree kill (the finally still runs) and self-healing if the harness process itself dies (the next run's stage call cleans it). The clearing is logged by name, so the leak is visible rather than silently clobbered. Pure half plus tests in `hlib` (`render_settings_sidecar_baseline` / `parse_settings_sidecar` / `settings_sidecar_tracers_on`); shell half driven end to end over the fake-KSP seam.

CONSEQUENCE to be aware of: this de-arms the INCIDENTAL map-render anomaly sweep for the 27 scenarios that never declared a tracer (their `allowedAnomalies` gate becomes structurally inert, because every `MapRenderTrace` emit early-returns on `IsEnabled`). That is the intended correction - tracer state is now a declared per-scenario property - but any scenario that wants the sweep armed must add its own `SetSetting mapRenderTracing=true` step, as S1.4 and S1.6 do.

## `allowedAnomalies` is misplaced in every committed scenario spec [FIXED, branch `autotest-render-parity`]

Found 2026-07-26. `run.py` reads `expectations.allowedAnomalies`, but all 28 pre-existing specs write a bare `allowedAnomalies = [...]` AFTER the `[expectations.logContracts]` header, which TOML scopes to `expectations.logContracts.allowedAnomalies` - a key nothing reads. Same class as the `skipTailOnUnmetMission` misplaced-key trap.

Inert for the 27 specs declaring `[]` (the default is also `[]`, so the sweep behaves identically), but NOT inert for S1.4-injected-playback, whose declared `polyline-orbit-overlap` exception has never applied: S1.4 has been running with NO anomaly allowed. Its green runs therefore prove that overlap did not occur, not that it was tolerated.

**Decision (2026-07-26): promote the validator to an ERROR, and relocate the key WITHOUT activating S1.4's dead exception.** The alternative was to move the key as-declared in all 28 and accept that S1.4 gains a tolerance that has never been in force. Rejected, for three reasons.

1. *A warning does not fail anything.* The failure mode is a spec author believing a declared tolerance applies when it does not, and a green run then meaning "the anomaly did not happen" rather than "the anomaly was tolerated". A warning is invisible at exactly the moment that matters; it had already been printed for one commit and changed nothing.
2. *An error is cheap and loud.* `validate_spec` runs BEFORE the KSP launch, so a misplaced key costs a fast pre-flight rejection with the fix named in the message - never a wasted run. The reason it shipped as a WARN was that all 28 specs still carried the misplaced form; relocating them in the same change removes that constraint entirely.
3. *The trap re-arms.* Which sub-table a bare key lands in depends on whichever `[expectations.<sub>]` header precedes it, so the hazard returns every time a spec grows a new sub-table. Only a hard gate makes it unrepeatable. The check now covers EVERY expectations sub-table, not just `logContracts`.

And the widening it avoids: activating S1.4's `polyline-orbit-overlap` exception would blind a live-proven scenario to a class of anomaly it currently catches, on the strength of a comment rather than a flight. The evidence points the other way - the overlap has never been observed. So all 28 specs get an explicit `[expectations]` table carrying `allowedAnomalies = []` ahead of their sub-tables, S1.4 included, with the original comment and the history preserved in the file. Every spec keeps exactly the gate strength it has actually been flying with, so no confirming flight is required - none of them changes behavior. If the overlap ever does fire, S1.4 reds, it gets an entry here, and it is quarantined via `[expectedFail]` or re-declared WITH the flight that shows it.

Implemented in `hlib.validate_spec` (error, per sub-table, naming the inert exceptions when the list is non-empty) plus the 28 spec relocations; covered by `MisplacedAllowedAnomaliesRejectionTests`, which also pins that no committed spec still carries the misplaced form and that S1.4's list is empty while its history stays documented.

## A shipped daily scenario read GREEN while executing ZERO tests [FIXED, branch `autotest-batch-coverage`]

Proved live on the real automation instance 2026-07-26. `B10-career-passive-safety` (tier `daily`) passed every run while its in-game batch executed nothing:

```
BATCH_COMPLETE v1 total=2 passed=0 failed=0 skipped=2 category=RecordingInvariants scene=SPACECENTER
Scene eligibility skip summary: skipped=2 currentScene=SPACECENTER byRequiredScene=FLIGHT:2
```

**Root cause, three facts that had to line up.** (1) Both `RecordingInvariants` tests are `Scene = GameScenes.FLIGHT`. (2) The `fresh-career` fixture has ZERO `VESSEL` nodes by construction - that IS the scenario - so `TestCommandLoadGame.DecideLoadRoute` takes the `NoVesselSpaceCenter` branch and the batch runs at the Space Center, where `FilterSceneEligibleBatchCandidates` skips both tests before `RunBatch` even starts. (3) The spec's ONLY batch contract was `BATCH_COMPLETE v1 .* failed=0\b`, and an all-skipped batch has `failed=0` exactly like an all-passed one. `L1-passive-sandbox` had the identical shape (`fresh-sandbox`, also vessel-less, also `RecordingInvariants`). This is the THIRD instance of the class this cycle, after EVA-3's recordings-count floor being set to the buggy output and S1.4's anomaly sweep passing over zero samples.

**Instance fix - the category moves, the pin does not paper over it.** A vessel-less fixture can never host a FLIGHT-scene category, so pinning `total=2 passed=2` on B10 would just convert a permanent false green into a permanent red. Both scenarios move to `GameActionsHealth`: 4 scene-agnostic (`RequiredScene == AnyScene`), batch-allowed, READ-ONLY tests (GameStateRecorder's three suppression flags not stuck; `Funding` / `ResearchAndDevelopment` / `Reputation` singletons resolving with in-range pools). Read-only matters - both scenarios' ledger oracle asserts a ZERO career delta, so the batch must not move the economy.

**Evidence standing per row, corrected 2026-07-26 after review.** An earlier draft of this section claimed "every tally below is MEASURED; no derived value and no PENDING-OPERATOR remains in this table". That was wrong on two of the seven rows and is restated here. The strict test is: was the EXACT pinned line already the spec's contract when the live run executed, so that the run's `expectations status=PASS` matched the whole line? Four rows pass that test; H6 and S1.4 do not, and the `Standing` column says so.

| Spec | Pinned `BATCH_COMPLETE v1` tally | Standing | Evidence |
|---|---|---|---|
| B10-career-passive-safety | `total=4 passed=4 failed=0 skipped=0 category=GameActionsHealth scene=SPACECENTER` | MEASURED | 2026-07-26 PASS with this exact pin already committed (a0d03a949) before the flight. The vacuity fix proven: the same step previously emitted `total=2 passed=0 skipped=2` and read green. 4 AnyScene batch-allowed tests; `Mode = CAREER` so none of the three pool-singleton self-skips fired |
| L1-passive-sandbox | `total=4 passed=1 failed=0 skipped=3 category=GameActionsHealth scene=SPACECENTER` | MEASURED | 2026-07-26 PASS (first flight in the corrected category), exact pin committed before the flight. `Mode = SANDBOX` so Funding/Reputation (`!= CAREER`) and Science (`!= CAREER && != SCIENCE_SANDBOX`) correctly self-skip; `SuppressionFlagsNotStuck` executes. The 3 skips ARE this scenario's subject; a 4th would mean the one real assertion stopped running |
| M1-mission-loop-unit | `total=12 passed=5 failed=0 skipped=7 category=Missions scene=SPACECENTER` | MEASURED | Exact pin committed before the flight, and the line is byte-for-byte in the archived `logs/2026-07-26_1247_M1-mission-loop-unit/` KSP.log |
| M2-periodicity-solver | `total=11 passed=7 failed=0 skipped=4 category=Periodicity scene=SPACECENTER` | MEASURED | 2026-07-26 PASS attempt 1 with the exact pin already committed before the flight |
| H5-invariants-corpus | `total=2 passed=2 failed=0 skipped=0 category=RecordingInvariants scene=FLIGHT` | MEASURED | 2026-07-19 live run's own line, byte-for-byte in that run's archive, and it agrees with the attributes |
| H6-route-rewind-timeline | `total=7 passed=7 failed=0 skipped=0 category=RouteRewindTimeline scene=FLIGHT` | **DERIVED** | No archived H6 run carries a whole tally: the pin in force on 2026-07-24 was `failed=0 skipped=0` with NO total / passed / category / scene tokens, so the live PASS proves only those two. `total=passed=7` follows from that plus the attribute count (7 AnyScene batch-allowed tests, skipped=0 forces passed=7). `category=` is the driver's own RunTests argument. **`scene=FLIGHT` has no H6-specific evidence at all** - it is inferred from the gloops-airshow fixture's Focusable/FLIGHT LoadGame route, the same rule H5 and S1.4 measured on the same fixture. Failure mode is a loud RED naming the real scene, never a false green |
| S1.4-injected-playback | `total=42 passed=40 failed=0 skipped=2 category=GhostPlayback scene=FLIGHT` | **PARTLY MEASURED** | The 2026-07-26 run (`harness/results/2026-07-26_1012_S1.4-injected-playback.json`, PASS) executed the LOOSE `passed=[1-9][0-9]* skipped=[0-9]+` pin that a0d03a949 shipped, so it proves total=42, failed=0, category=GhostPlayback, scene=FLIGHT and passed>=1 - not the 40/2 split. That split was read off the run's live log, which was NOT archived (`collectLogs: {ran: false}`, and the instance KSP.log has since been overwritten), so it is not re-derivable from any committed artifact. Structurally re-derivable: total=42 (attribute-exact) and >= 1 skip (`RunAllDuringWatch_DoesNotLeakSunLateUpdateNREs`, `AllowBatchExecution=false`); the 2nd skip is the fixture-determined `WatchEntry_SameBody_PreservesFreshEntryAngles` self-skip. **The pin STAYS** - a wrong split reds loudly with the numbers to re-derive from - but re-fly with collect-logs forced if the split ever needs proving from an artifact |

Also dropped: B10's `D16 = ["schema-gate"]` claim. The load-time schema gate is per-recording (`RecordingSidecarStore` -> `RecordingStore.IsRecordingSchemaCompatible`; analyzer INV5 likewise) and B10 injects nothing and records nothing, so the cell was claimed over an empty set. H5 covers it honestly over 306 recordings.

**Second-order fix - the pinned tallies are now cross-checked against the C# they describe.** Pinning the tally WHOLE (the fix above) trades one failure mode for another: `total=N` is a hardcoded copy of a number that lives in `Source/Parsek/InGameTests/`, and nothing kept the two in step. Adding one `[InGameTest]` method to `Missions`, `Periodicity`, `GameActionsHealth`, `RouteRewindTimeline`, `RecordingInvariants` or `GhostPlayback` moves `total` by one and reds that category's daily scenario on its NEXT NIGHTLY RUN - hours later, in another process, as a logContract mismatch on a spec the author never opened, with no local signal at all. The gate is now a `python -m unittest` failure that names the spec and hands over the re-derived numbers.

Pure decisions in `hlib` (`parse_ingame_test_declarations` / `derive_batch_tally` / `resolve_batch_tally_pin` / `batch_tally_pin_mismatches`), file reading in the `test_hlib` sweep - deliberately NOT in `validate_spec`, because that runs inside `run.py` against a provisioned instance where the C# tree need not exist at all. `derive_batch_tally` models `RunCategory`'s two filters IN THE RUNNER'S ORDER (`FilterSceneEligibleBatchCandidates` on scene first, then `PrepareBatchExecution` on `AllowBatchExecution=false` over what survived), which matters for attribution: a test failing both is counted once, in the scene bucket, exactly as the runner counts it, and double-counting it would inflate the floor and false-red a spec.

What is checkable and what is not: `total` is EXACT (`allTests.Count(Status != NotRun)` over the single-category batch counts filtered tests too, so it is just the category's method count). `skipped` is only a FLOOR - a test that clears both filters can still self-skip at run time via `InGameAssert.Skip` on live fixture state, which no static read can predict. `L1-passive-sandbox` is the standing proof: it pins `skipped=3` over a category whose attributes force ZERO skips, because `Mode = SANDBOX` self-skipping three pool-singleton tests IS its subject. So the four rules are `total == derived`, `skipped >= scene-skipped + batch-skipped`, `passed + failed <= batch-eligible`, and the runner's own `passed + failed + skipped == total`. A token written as a regex class rather than a literal is simply left unchecked (the honest interim form when no live tally has been measured; no committed spec uses one today), while a literal `total=` is REQUIRED of every batch owner, so the load-bearing check always applies. Stated rather than papered over: the floor still admits a NEAR-vacuous pin (`total=42 passed=1 failed=0 skipped=41` satisfies the exact total, the floor, the ceiling and the sum, and the anti-vacuity probes only cover the `passed=0 failed=0` family). Static analysis cannot close that - run-time `InGameAssert.Skip` is unbounded - so the answer is the MEASURED tally every committed pin now carries, and the mismatch message says `passed`/`skipped` must be re-measured off a live run rather than recomputed.

The parse is masked, not regexed: a single pass blanks comment bodies and string-literal interiors while preserving offsets, so the four forms the tree actually uses cannot fool it - multi-line argument lists (`IncompleteBallisticRuntimeTests`), `Category = <const>` resolved against a same-file `const string` (`RouteRewindTimelineRuntimeTests` declares `private const string Category = "RouteRewindTimeline"`, and a literal-only regex would file all 7 of its tests under `General`), `Description` values carrying commas / parens / attribute-shaped text (one Description literally reads `no [InGameTest] declares Scene=EDITOR`), and commented-out attributes. An attribute form the parse CANNOT resolve is kept with an `<unresolved:...>` marker and reds `test_every_declaration_resolves` rather than silently shrinking a category total.

That covers only forms the parse RECOGNISED and then failed to resolve, though - a spelling it never matches is simply absent, which is the one way this gate could fail open. So a second, independent scan (`unclaimed_ingame_attribute_tokens`) reports every occurrence of the attribute name inside an attribute bracket that looks like a use and that the parse claims nothing for, and the sweep asserts it is empty. **It caught a live undercount on its first run.** The original parse anchored on `[` immediately followed by the name, and the tree already carried FIVE `[Parsek.InGameTests.InGameTest(...)]` declarations (4 `Ledger`, 1 `Rewind`, all in `IncompleteBallisticRuntimeTests.cs`) that it dropped silently - and that a raw `[InGameTest(` grep drops identically, so the two agreeing on 534 corroborated nothing. The parse now models the attribute-section grammar (optional `method:` target, optional namespace/type qualifier, C#'s optional `Attribute` suffix, position anywhere in a comma-separated bracket), which also closes the three other spellings Roslyn binds and this parse used to miss.

Verified against the real tree: **539 declarations across 97 categories** (was reported as 534 / 96; `Ledger` was missing entirely and `Rewind` was short by one). No PINNED category moved - `Missions` 12, `Periodicity` 11, `GameActionsHealth` 4, `RouteRewindTimeline` 7, `RecordingInvariants` 2, `GhostPlayback` 42 - so no committed spec was wrong. Mutation-proved to bite in all three directions: adding a `Missions` test reds `total`, flipping one test's `Scene` to FLIGHT reds the `skipped` floor, and setting `AllowBatchExecution = false` reds it too - each message naming the offending `file:line member`. Also fixed alongside: the sweep's owner discovery now mirrors `validate_spec`'s RunTests-XOR-`[driver.autorun]` rule (an autorun-owned spec was never swept), and a `category=all` pin is derived over the whole assembly the way `RunAll` runs it instead of false-redding as a missing category. 64 new cells (`InGameAttributeParseTests`, `RoslynAttributeSpellingTests`, `UnclaimedInGameAttributeTests`, `DeriveBatchTallyTests`, `BatchTallyPinTests`, `BatchTallyMismatchTests`, `CommittedBatchTallySourceSyncTests`); harness lib 480 -> 544 green.

Bounded, and stated rather than papered over: the `scene=` token is taken from the pin, not derived from the fixture's `VESSEL` nodes through `DecideLoadRoute`. So a spec pinning the WRONG scene stays consistent with itself and this gate will not catch it - the anti-vacuity gate plus a live run are what pin the route. Deriving the route statically is a separate gate over the fixture saves.

**Class fix - `hlib.validate_spec` rejects a non-discriminating batch contract before KSP is ever launched.** Deliberately NOT a syntactic rule (a "must contain `total=`" check is itself a tautology - `total=\d+` pins nothing). The gate works by CONSTRUCTION: `vacuous_batch_complete_probes` synthesizes every `BATCH_COMPLETE v1` line a vacuous batch could emit for the spec's own driven selector, and `batch_contract_vacuity_gap` reports the first one the spec's `logContracts.required` patterns would ACCEPT. The vacuous family is one-parameter: the runner's tally always satisfies `total == passed + failed + skipped`, so `passed=0 failed=0` forces `total == skipped` - `skipped=0` is the empty batch, `skipped=N` the all-skipped one. Probes sweep every `GameScenes` token (the original defect WAS a scene surprise), both the prefixed and bare log-line forms, and `skipped` over 0..256 plus every integer literal the patterns themselves name (so a pin above the bound is still probed at exactly its own value). Opt-out is `batchVacuityOptOut = true` in `[expectations.logContracts]` with a REQUIRED non-empty `batchVacuityOptOutReason`; the key is also misplaced-key-guarded (outside that table TOML silently ignores it, and this one fails UNSAFE). 19 unit cells in `BatchVacuityGateTests`, including a repo-wide discovered sweep so a scenario added later is covered automatically.

**Exactly what the gate guarantees, and the three dodges review found and closed.** The first draft of this section claimed the gate "rejects ANY batch-owning spec whose contract a vacuous batch could satisfy". It did not; adversarial review built three passing counterexamples, none reachable from the 7 committed batch-owning specs but all reachable from a spec written tomorrow. All three are now closed, with a cell each in `BatchVacuityGateShapeTests`:

1. **A second `RunTests` step.** Only the FIRST category was resolved and probed, and `batch_owners` counts 1 for any n>0, so a whole-tally pin on the first batch left the second entirely ungated. Now a hard ERROR: a batch-owning spec must declare exactly one `RunTests` step.
2. **A multi-category selector** (`"A,B"` or an absent category, which is RunAll). The run-time gate is the `category=multi:<n>` AGGREGATE, whose tally sums the constituents, so "category B executed nothing" is not expressible; and a pin naming one constituent rejected the other's probes for the wrong reason (category-token mismatch), which read as "no gap". Now a hard ERROR, fail-closed, waivable through the same documented opt-out. `_batch_probe_categories` lost its multi branch with it - the probe family is single-category by construction now, not by assumption.
3. **Two `BATCH_COMPLETE` patterns satisfied by DIFFERENT log lines.** The gate ANDed its patterns against ONE synthesized probe, but `evaluate_expectations` `re.search`es each required pattern over the WHOLE log independently. `LogContractTests.BatchCompleteFormatValid` pushes a literal `BATCH_COMPLETE v1 total=42 passed=40 failed=1 skipped=1 category=RecordingInvariants scene=FLIGHT` through `ParsekLog.Info`, so one pattern could be satisfied by that decoy while another was satisfied by the vacuous tally. Discrimination now requires ONE single pattern to reject the whole vacuous family INCLUDING the known decoy, which `_BATCH_DECOY_BODIES` models and a unit cell cross-checks against the C# literal.

What the gate still does NOT do, stated plainly rather than left to be discovered: it blocks only `passed == 0`. The `passed=[1-9][0-9]*` form its own error message recommends accepts 1-of-42. That form is the honest placeholder for a spec whose exact split has not been measured; pin the whole tally as soon as a live run gives you one.

## Two D11 scenarios: the mission-loop plan and the periodicity solver, unattended [BUILT, branch `autotest-batch-coverage`]

D11 (missions / periodicity / re-aim, 18 registry cells) was **0/18 - entirely unclaimed** - while 23 in-game tests across the `Missions` and `Periodicity` categories already encoded a real mission-loop / re-aim oracle, needed NO fixture, and had never run unattended because no spec named their category. Two new daily specs on the committed `fresh-sandbox` fixture (vessel-less, so `LoadGame` settles at SPACECENTER, which is where the batch-eligible tests live) take it to 8/18; total registry coverage 69 -> 77 of 238.

- **`M1-mission-loop-unit`** (`RunTests category = "Missions"`) - pinned `total=12 passed=5 failed=0 skipped=7 category=Missions scene=SPACECENTER`. The 5 SPACECENTER tests build synthetic committed trees, register them through the REAL `RecordingStore.CommitTree`, and drive the REAL `MissionCrossTreeDock.FindLinks` / `MissionStore` include+normalize / `MissionLoopUnitBuilder.Build` against LIVE stock ephemerides. The 7 skips are the `Scene = GameScenes.FLIGHT` members (4 `RealSave_*` plus DescentHandoff / DockTree_Composition / the re-aim render canary) - the real-save and render half this spec explicitly does not claim. Claims D11 partner-journey, land-dock-dual-constraint, arrival-hold, multi-moon-config-hold, fail-closed-to-faithful.
- **`M2-periodicity-solver`** (`RunTests category = "Periodicity"`) - pinned `total=11 passed=7 failed=0 skipped=4 category=Periodicity scene=SPACECENTER`. The tally is deliberately NOT `total==passed`: `FilterSceneEligibleBatchCandidates` drops the 1 FLIGHT-scene member (`S4Restitch_...`), then `PrepareBatchExecution` drops the 3 `AllowBatchExecution=false` FeasibilitySweep diagnostics; both filters mark Skipped, so 4 land in the skipped tally. Claims D11 reaim-lambert, eccentric-inclined-targets, heliocentric-parking-departure, fail-closed-to-faithful.

**Scope discipline, stated in both spec headers:** these gate the mission-loop PLAN and the periodicity SOLVER. Neither observes a replaying ghost, a rendered map icon or polyline, a loop cycle boundary, or an elapsed period - nothing in either scenario advances the clock or spawns anything. That is why the loop-behavior D11 tokens (whole-mission-loop, self-overlap, clone, phase-lock, zero-drift-schedule, loiter-compression) stay unclaimed. Two specs rather than one because `run.py::_driven_category` resolves only the FIRST `RunTests` category and `hlib.resolve_batch_complete` treats per-category lines with no aggregate as a defined fault: one batch per spec. That rule is no longer only a convention - `validate_spec` now ERRORS on a second `RunTests` step or a multi-category selector (see the class-fix section above). No new seam verbs (`StartLoopPlayback` / `MissionConfig` stay RESERVED).

**`allowedAnomalies` placement, fixed for merge-order safety.** Both new specs originally wrote a bare `allowedAnomalies = []` after their `[expectations.logContracts]` header, where TOML scopes it to that sub-table and `run.py` (which reads `expectations.allowedAnomalies`) never sees it. Sibling branch `autotest-render-parity` promotes that placement to a hard spec-INVALID and relocates the key in all 28 pre-existing specs - but M1/M2 are new files it does not touch, so the two branches would have merged with ZERO conflict and turned two daily scenarios into never-launched INVALID-SPEC runs. Both new specs now carry an explicit `[expectations]` table ahead of the sub-tables, which is a no-op today (an empty list read or unread is identical) and makes the two branches order-independent.

**LIVE-PROVEN 2026-07-26.** Both flew, both batch tallies came back EXACTLY as derived (`M1 total=12 passed=5 failed=0 skipped=7 category=Missions scene=SPACECENTER`; `M2 total=11 passed=7 failed=0 skipped=4 category=Periodicity scene=SPACECENTER`), so both spec tallies are MEASURED and both PENDING-OPERATOR notes are closed. M2 was a full PASS on attempt 1.

**The run ledger, verbatim from `harness/results/summary.txt` - SIX runs, not five, and the first one was RED.** An earlier draft said "five live runs, all PASS", which contradicted its own next paragraph. The red is the point of the exercise, not a blemish to round away: it is the run that found the sidecar leak, and it found it because M1's `{0,0}` pin was written tight enough to see it.

```
2026-07-26T09:46:33Z PASS         B10-career-passive-safety  attempt=1 wall=78s
2026-07-26T09:47:33Z PARSEK-FAIL  M1-mission-loop-unit       attempt=1 wall=46s
2026-07-26T09:49:42Z PASS         M2-periodicity-solver      attempt=1 wall=46s
2026-07-26T10:10:18Z PASS         M1-mission-loop-unit       attempt=1 wall=59s
2026-07-26T10:12:12Z PASS         L1-passive-sandbox         attempt=1 wall=65s
2026-07-26T10:13:56Z PASS         S1.4-injected-playback     attempt=1 wall=89s
```

M1 flight 1's batch tally was exact; the PARSEK-FAIL was its `recordings.count = {0,0}` pin catching a real Parsek-side defect - see the next section. M1 flight 2 is the post-fix re-fly.

**M2's tally derivation was arithmetically wrong** in the first commit and is corrected here: the spec enumerated `ReaimEndToEndInGameTest 8, CrossParentReaimCanaryInGameTest 1, ReaimLandingCoincidenceInGameTest 1` = 10 while pinning `total=11`. `grep -c 'Category = "Periodicity"'` gives **9** for `ReaimEndToEndInGameTest` (6 batch-eligible SPACECENTER + the 3 `AllowBatchExecution=false` FeasibilitySweep diagnostics), so 9 + 1 + 1 = 11. The PIN was right and is unchanged - it matched the live line token for token; only the re-derivation trail a future maintainer would follow was wrong.

## In-game tests that CommitTree leaked orphan sidecars into the produced save [FIXED, branch `autotest-batch-coverage`]

Found 2026-07-26 by `M1-mission-loop-unit`'s first flight. The batch was perfect; the run still red'd `PARSEK-FAIL expectation: recordings.count 5 > max 0`. The `{0,0}` pin was RIGHT and is unchanged.

**Evidence** (`logs/2026-07-26_1247_M1-mission-loop-unit/`). The produced `persistent.sfs` carries ZERO `RECORDING_TREE` and ZERO `RECORDING` nodes - the in-memory teardown works exactly as its comment claims. But `parsek/Recordings/` holds 15 files: `mmis8-xdock-bd82494a-{A0,A1,AB,B0,B1}` x `.prec` / `.pann` / `.prec.txt`. `run.py::count_recordings` counts `.prec` files on disk, not save nodes, which is the only reason this was visible at all.

**Root cause.** `CrossTreeDockLoopUnitInGameTest` registers its two synthetic trees through the REAL `RecordingStore.CommitTree` - which is the point of the test, and which flushes `SaveRecordingFiles` for every recording it commits (`WriteBinaryTrajectoryFile` + the Pannotations sidecar + the readable mirror, all visible in that run's KSP.log at 12:47:18). Its `finally` drops both trees with `RecordingStore.RemoveCommittedTreeById`, which is memory-only BY DESIGN (the re-fly / restore paths depend on the files surviving the removal). Nothing then reaps the files, and once those ids are absent from `BuildKnownRecordingIds` the next load's `CleanOrphanFiles` preserves them forever as possible recovery candidates. Exactly the S0.5 discard-residue shape: a production write path driven by a test whose only teardown is in-memory.

**Fix - the shared path, not the one test.** `PersistenceSplitOptimizerTestCleanup` (which already solved this for the optimizer test, but was named and documented as one test's private helper) is generalized to `InGameTestSidecarReaper`, gains a per-caller log context, and gains a **known-ids guard**: the live entry point filters candidates through `RecordingStore.BuildKnownRecordingIds()` and REFUSES to unlink files for any id the store still owns, counting and logging every preservation. Same guard the S0.5 discard reap carries, for the same reason - a reap issued before the in-memory removal, or over an id that collides with live data, must delete nothing rather than destroy mission data.

**Completeness, corrected 2026-07-26 after review.** The first draft claimed the reaper was "wired into every in-game call site that drives the real `RecordingStore.CommitTree`". It was not: it covered the DIRECT `CommitTree` callers and missed the sites that reach a real commit INDIRECTLY through the production merge paths, all of which drop their tree through the second, unwired `RemoveCommittedTreeByIdForRuntimeTest` helper (both overloads). Those are now wired at the helper, the same way the Playback helper was. The table below is the enumerated set, produced by grepping every `CommitTree` / `Merge to Timeline` / `AddCommittedTreeForTesting` / `RemoveCommittedTreeById*` site under `Source/Parsek/InGameTests/`:

| Call site | How it reaches a real commit | Now reaps via |
|---|---|---|
| `CrossTreeDockLoopUnitInGameTest` (the M1 offender) | direct `RecordingStore.CommitTree` x2 (2 trees / 5 recordings) | explicit id list in the existing `finally`, after both `RemoveCommittedTreeById` calls |
| `RouteInterBodyBuilderShapeInGameTest` | direct `RecordingStore.CommitTree` (1 tree / 1 recording) | explicit id in the existing `finally`, after the removal |
| `RuntimeTests` keep-vessel playback canary, timeline-FF auto-record canary, RSC-warp auto-record canary (3 tests) | direct `RecordingStore.CommitTree`, 1 tree each | the shared `RemoveCommittedTreeByIdForPlaybackRuntimeTest` helper all three already call - ids collected before the removal, reaped after |
| `TreeMergeDialog_DeferredMergeButton_CommitsPendingTree` (`MergeDialog`) | clicks the REAL "Merge to Timeline" button on a stashed synthetic tree; the dialog's commit path calls `CommitTree` | **NEW** - `RemoveCommittedTreeByIdForRuntimeTest(treeId)`, now reaping like its Playback sibling |
| `EvaKerbalGhostHasVesselSnapshot` (`AutoRecord`) | real `StartRecording` + `FinalizeTreeRecordings` on a live tree | **NEW** - same single-argument helper |
| `ExitToSpaceCenter_DeferredMergeButton_CommitsPendingTree` (`SceneExitMerge`) | real recording + real pre-transition scene-exit merge dialog; asserts `committedTreesBefore + 1` | **NEW** - `RemoveCommittedTreeByIdForRuntimeTest(treeId, recalculateAfterRemoval: true)`, reaping AFTER `RunOptimizationPass` so the guard reads the final known-ids set |
| `ExitToSpaceCenter_DeferredDiscardButton_ClearsPendingTree` (`SceneExitMerge`) | Discard branch - commits nothing; the defensive removal is a no-op | **NEW** - same two-argument helper; the reap is a no-op when nothing was committed |
| `PersistenceSplitOptimizerTest` (2 tests, already reaping) | `RunOptimizationPass` flush | renamed call; now also known-ids-guarded |

**Deliberately NOT wired, and why.** `RuntimeTests.V13Debris_*` (`GhostPlayback`) and `ExtendedRuntimeTests`' KSC relative-anchor probe register their trees with `RecordingStore.AddCommittedTreeForTesting`, which is a bare `committedTrees.Add` with no `FinalizeTreeCommit` and therefore no `FlushDirtyFiles` - nothing is written to disk, so there is nothing to reap. `TreeMergeDialog_DiscardButton_ClearsPendingTree` clicks Discard, which is the production discard path the S0.5 reap already covers.

All the newly-wired sites are `AllowBatchExecution=false` / isolated-run-only, so - like the three Playback canaries - they never leaked into an unattended harness save. They leaked into whatever disposable FLIGHT session an operator ran them in, and the fix is free at the shared helper.

**Re-flown green 2026-07-26** with the `{0,0}` pin untouched, all seven verifiers PASS. What that proves and how: the re-fly's `recordings.count = {0,0}` expectation passed WHILE the batch tally `total=12 passed=5 failed=0 skipped=7` also passed, and `CrossTreeDockLoopUnit` is one of the 5 SPACECENTER `Missions` tests - so the committing test demonstrably executed AND the produced save's `Parsek/Recordings/` was empty. That is the substantive proof. Note that the run's own reap log line is NOT archived (`collectLogs: {ran: false}` on a PASS), and that a `preservedKnown=0` in such a line would prove nothing about the guard anyway - a fresh sandbox has no other recordings, so `BuildKnownRecordingIds()` returns an EMPTY set there. The guard's BEHAVIOUR is proven by the four xUnit cells below, which assert file existence rather than a log token. Coverage unchanged at 77/238.

**Coverage:** `InGameTestSidecarReaperTests` (11 xUnit cells, renamed from `PersistenceSplitOptimizerTestCleanupTests` and extended) - the four known-ids-guard cells are new: preserve-one-reap-the-other, null known-set reaps everything, all-known reaps nothing, and the counted/named log line.

## B1's parachute never opened, and its DOWN terminal could not tell [FIXED, branch `autotest-b1chute`]

B1-pad-hop was marked LIVE-PROVEN on 2026-07-19/20 with a documented Parsek surface of "chute-deployed ground-arrival recording (DOWN contract)". The chute never opened on any of those runs. Found 2026-07-25 while diagnosing the EVA-4 flight-1 failure; no new flight was needed to prove it, only the already-committed collected logs.

**Evidence.**

1. `logs/2026-07-20_1829_B1-pad-hop/parsek/Recordings/19e55f1ffe044543a84c2f5b8dea0294.prec.txt` - the pad craft's recording holds exactly ONE `parachuteSingle` part event, `type=0` (`PartEventType.Decoupled`) at `ut=119.70`, which is the ground-impact breakup. There is NO `ParachuteSemiDeployed` (type 33) and NO `ParachuteDeployed` (type 2) anywhere in the recording. Its last TrackSection is `ut=[14.64, 119.70]`, `alt=[65, 12031]`: the craft fell 12 km and broke apart.
2. `logs/2026-07-24_2210_EVA-4-atmo-chute` reproduced it on the same craft with per-frame telemetry - unchuted descent settles at TERMINAL -301 m/s by ~2,700 m and holds it; the chute was armed at 2,382 m / -301 m/s and 5.1 s later, at 855 m, the rate had moved 4.7 m/s. That recording also carries zero `Parachute*` events.

**Root cause (decompiled stock `ModuleParachute`, KSP 1.12.5).** The ACTIVE -> SEMIDEPLOYED transition requires `automateSafeDeploy >= (int)deploymentSafeState`, and `DeploySafe` returns SAFE only while `shockTemp <= chuteMaxTemp * safeMult` (it is a THERMAL test, so it tracks airspeed). The `b1-pad-craft` fixture persists `automateSafeDeploy = 0` - "open only while SAFE", the stock default - so a chute armed at terminal velocity in dense air waits for a SAFE reading that a craft at terminal velocity can never produce, because it never slows on its own. Arming LOW was INERT, not late. (The other gates are all satisfied and are not the cause: `minAirPressureToOpen = 0.04` atm is met from ~15 km down, and `IsMovingFastEnoughToDeploy` only needs \|v\| > 1 m/s.)

**Why it stayed green for four months.** The DOWN terminal's eligibility was `DESCENT && chute_deployed && last_finite_altitude <= downMaxAltMeters`, and `chute_deployed` is the machine's own COMMANDED latch - set the instant the deploy action is emitted, regardless of what the module does. A terminal-velocity impact satisfies all three conjuncts (it IS in DESCENT, it DID command the chute, and it very much reached the ground), so every run was awarded the "chute-deployed impact" success terminal. This is a fail-OPEN assertion: the machine asserted its own behavior instead of KSP's.

The 2026-07-21 review round predicted the damage was bounded ("the altitude gate now bounds the damage of a failed canopy to a sub-500m impact classification"). It was not - a failed canopy puts the craft at sub-500 m at 300 m/s, which is precisely the case the altitude gate waves through. The residual note below is corrected in place.

**Fix.** Both halves, because either alone is insufficient (fixing only the docs abandons the chute surface; adding only the observed gate reds a scenario whose chute genuinely cannot open):

- **Arm while slow.** The machine now arms on the first DESCENT frame whose \|vertical speed\| is within `chuteArmMaxRateMps` (30) - the apoapsis crossing - instead of at an altitude, and writes `chuteFullDeployAltMeters` (2500, pinned by the spec rather than inherited from the fixture's 1000 m PAW value) onto the parachutes on the SAME frame via the new `ACTION_SET_CHUTE_DEPLOY_ALTITUDE`. At the apex the airspeed is near zero, `DeploySafe` is trivially SAFE, and Kerbin is far above the 0.04 atm gate, so the canopy opens within a frame or two. COAST -> DESCENT now FALLS THROUGH into the DESCENT body on the same frame, because the rate only ever worsens (~10 m/s per poll) and one deferred poll eats the arming bound. This is the technique EVA-4 flight 2 LIVE-PROVED on this exact fixture and craft.
- **Gate on the observed canopy.** New opt-in telemetry channel `TelemetrySnapshot.craft_chute_state` (`KrpcMissionControl(read_chute=True)`, one RPC per poll, "" = fail-closed unread sentinel) carrying the live kRPC `ParachuteState`, most-deployed-port-wins. `B1State.craft_chute_full_seen` is a sticky OBSERVED latch set only when the canopy READS Deployed. `_b1_down_eligible` now requires that latch instead of the commanded one, and a new third assertion `craftCanopyObserved` applies to BOTH terminals, so a LANDED craft with an unopened chute also fails. DOWN-ineligible loss reasons now carry `craftChute=` / `canopyObserved=` / `armCommanded=`, so an inert chute reds as `craftChute=Armed` and names itself.
- **Parsek-side proof.** The spec now requires the `Part event: ParachuteSemiDeployed 'parachuteSingle` and `Part event: ParachuteDeployed 'parachuteSingle` log tokens (hence a new `verboseLogging` step), so the run proves not just that KSP opened the chute but that PARSEK RECORDED the two-phase event pair. B1 claims D7 `chute-two-phase` for the first time. Both tokens would have FAILED every B1 run flown before this change.
- Budgets: descent 240 -> 360 s, mission 600 -> 900 s, wall 900 -> 1320 s. The first draft of this fix raised them much further (600/1020/1440) on the assumption that a chuted descent would be several times longer than the ~56 s unchuted fall, sized against a pessimistic ~30 m/s semi-deployed crawl. EVA-4 flight 2 then measured the real numbers on this same craft and that assumption was wrong by ~8x: the SEMI-deployed Jumping Flea sinks at up to -236 m/s (peak ~5.7 km, still -223 m/s at 2500 m), and the full canopy needs ~5.6 s / 894 m to brake it to -23 m/s (measured: EVA-4 triggered at 2500 m and handed off at 1,606 m). EVA-4 timed apoapsis -> 1,606 m at 61.6 s; B1 adds the full-canopy leg to the ground (~1,536 m, 154-176 s integrated against the stock drag model), giving ~216-238 s expected and 360 as a ~1.5x backstop. (A first draft of this entry said ~180 s / 2x, from a linear 23 -> 8 m/s decay over the full-canopy leg; integrated against the stock drag model that leg is 154-176 s, not 110-130 s, because under a full canopy the relaxation time to terminal is v_t/2g ~ 0.45 s and the craft is AT terminal within ~2 s of the handoff.) The schema bounds on `chuteFullDeployAltMeters` were also tightened to [500, 5000]: they had been inherited from the deleted altitude-trigger param as `min = 0.0` with no max, and since the value is now WRITTEN ONTO stock's `deployAltitude`, a 0 there would make `ShouldDeploy()` permanently false and hold the chute SEMIDEPLOYED to a ~220 m/s ground impact - re-creating a variant of this bug through a value the schema accepted. Those same measurements are why `chuteFullDeployAltMeters` is 2500 and not the fixture's 1000: at 1000 m that 894 m brake would consume ~96% of the usable sky. CAVEAT: the flight-2 numbers are quoted from the EVA-4 spec on main, not from a collected log in this tree (`logs/` holds only EVA-4 flight 1), so they are second-hand evidence, not machine-verifiable here.
- B1 DE-LISTED from live-proven in `autotest-status.md` (its next nightly IS its re-prove). The fixture comment's "the stack ALWAYS breaks apart at touchdown (~9 m/s vs the booster's 7 m/s tolerance)" was never measured - every observed breakup was a ~300 m/s impact - and is corrected. Whether the stack survives a real canopy landing is open until the first run; LANDED and DOWN are both accepted ends.

**Merge note:** the chute telemetry channel (`CHUTE_STATE_*`, `normalize_parachute_state`, `TelemetrySnapshot.craft_chute_state`, `ACTION_SET_CHUTE_DEPLOY_ALTITUDE`, `_read_craft_chute_state`, the `chute=` telemetry-line field) is written BYTE-IDENTICAL to the same additions on the unmerged `autotest-eva4-chute` branch so the two resolve without conflict. The one expected textual conflict is the `KrpcMissionControl.__init__` signature line, where that branch also adds `read_crew`.

**Follow-up (not fixed here): B4 has the same latent bug.** `evaluate_b4_assertions`'s `chuteDeployed` is also a COMMANDED latch, `b4_decide` still deploys on an ALTITUDE trigger (`chuteDeployAltMeters = 3000`), and the B4 fixture `b2-lko-craft` carries the same `automateSafeDeploy = 0`. A reentry capsule at 3,000 m is at terminal velocity in dense air, so it should hit the identical wall. B4 is currently live-proven with a SPLASHDOWN, which means something did slow that craft - so this needs its own diagnosis from a B4 recording (check for `Parachute*` part events) before assuming it is broken, and its own fix if it is: a reentry has no apoapsis crossing to arm at, so the arm point has to come from the descent profile. Tracked as gate 7 in `autotest-status.md`.

## First live L-track ledger batch: two seam<->recorder fidelity fixes [BUILT, branch `autotest-career-fixtures`]

The first live run of the L-track ledger campaign passed 5/7 (B10, L1-dismiss, L1-passive-sandbox, L1-research-node-career, L1-research-node-science). Two reds, both real seam<->Parsek fidelity gaps (NOT the already-landed no-vessel LoadGame boot path):

- ~~**RED 1 - L1-hire: funds DOUBLE-DEBIT (ledger oracle hardDivergences=1).**~~ FIXED. Seed 500000 landed at 375774 = exactly 2x62113. Root cause (seam-side, proven by decompile): the hire verb manually `Funding.AddFunds(-cost, CrewRecruited)` to "mirror the stock debit", but stock ALREADY charges the recruit cost automatically - the `Funding` scenario module subscribes to `GameEvents.OnCrewmemberHired` (`Funding.onCrewHired -> AddFunds(-GetRecruitHireCost(count), CrewRecruited)`), which `KerbalRoster.HireApplicant` fires. The Astronaut Complex UI never calls AddFunds itself (only the CanAfford gate + HireApplicant), so the seam's mirror was a second charge. Parsek's ledger correctly counted ONE hire (running 437887); KSP funds got hit twice. Fix: removed the seam's manual debit AND its blocked-path refund (the KerbalHirePatch Prefix skips HireApplicant on block, so OnCrewmemberHired never fires and Funding never charges - nothing to refund). Kept the CurrencyModifierQuery CanAfford pre-gate. Manifest constant re-pinned `funds = -62113.0` (the recorder-observed cost for Verhat/Engineer at hired-count 4; seed 500000 -> 437887). Expected live re-run: save funds 437887, oracle hardDivergences=0.

- ~~**RED 2 - L1-upgrade: FacilityUpgraded never recorded (logContract mismatch; oracle PASSED).**~~ FIXED. The ledger funds math was already correct (-150000, hardDivergences=0), but `Game state: FacilityUpgraded` never fired. Root cause (Parsek-side): `GameStateFacilityRecorder` is scene-poll-only - `PollFacilityState` runs solely at `Subscribe()` (scene load), and the cold no-vessel load seeded an EMPTY baseline (`protoUpgradeables` facilityRefs not populated yet: "0 facilities"). A seam upgrade-then-FlushAndQuit never hits a subsequent scene-load poll AND had no cached "before" level to diff. Fix: subscribe the recorder to `GameEvents.OnKSCFacilityUpgrading` (fires SYNCHRONOUSLY inside `UpgradeableFacility.SetLevel` for BOTH UI and seam, before the level changes - unlike the coroutine/spawn-gated `OnKSCFacilityUpgraded`), emit `FacilityUpgraded` directly from the event args (no seeded baseline needed), forward to the ledger identically to the poll, and update the poll cache to avoid a later double. Guarded on `IsReplayingActions`/`SuppressResourceEvents` so recalc's `FacilityStatePatcher.SetLevel` (wrapped in `ResourcesAndReplay`) never re-records a Parsek-driven level patch. New pure helpers `ClassifyFacilityLevelChange` / `NormalizedLevel` (8 xUnit cells). Expected live re-run: "Game state: FacilityUpgraded 'SpaceCenter/TrackingStation'" present.

Suite: full xUnit 18478 (+8 new) green; harness lib 398 green (two stale `-24000`/`476000` hire-oracle cells re-pinned to `-62113`/`437887`).

## Fable review of the PR #1328 tail: applied + deferred items [BUILT, branch `autotest-s05-autorecord-pin`]

Second Fable review (commits 3-6; commits 1-2 were reviewed earlier). No blockers; both headline hazards defused with math (the frozen-telemetry detector CANNOT false-fire on a rails coast - altitude/vertical_speed are in the signature and cannot be bit-stable on any representable orbit; the B2 ascent-complete AND-gate degrades to an honest budget flake on every miss path). Applied: SF-1 warp mode/rate now derive from ONE rate sample (`_read_warp_state`; the two-RPC read let a ramp crossing 1.0 report mode NONE with rate above 1 = instant false flake) PLUS a two-consecutive-sample violation debounce (also closes SF-2's single-sample jitter-spike trip); SF-3 stale "B2 sets 2.0" docstring corrected; WATCH-4 B2 recordings window widened to max 8 (Kerbal X LES tower recording under staging-timing variance is unestablished); NIT-2 exception-state slot seeded at fly_loop entry (stale prior-mission state); NIT-5/6/7 comment/README corrections (golden-template ownership contract now in harness/README.md); NIT-8 wrong-origin refetch fetches tags with --force (same-named tag on both forks previously aborted the converge).

DEFERRED with rationale (all bounded, none false-PASS): NIT-1 an all-NaN frozen telemetry stream falls through to the phase budget as MISSION-FLAKE instead of the vessel-lost ASSERT-FAIL (misclassified but bounded; NaN breaks tuple equality); NIT-3 a kRPC drop during the settle tail retries a completed flight from boot instead of evaluating the frames already gathered (conservative), and a settle-tail read-fail streak's vessel_lost snapshot carries benign finite defaults into the evidence tail (edge-of-edge); NIT-4 the RAILS allowance is mission-scoped, not phase-scoped (stock cannot produce atmospheric rails warp); NIT-9 `_normalize_repo_url` lowercases the path (fail-loud on case-sensitive hosts, never silent); NIT-10 `plan_repair` classifies `krpcSettingsSha256` drift as full-reprovision while the SF9 re-stamp in practice converges it on every repair pass (observed repeatedly live 2026-07-20) - the classification wording is stale, the behavior is correct.

## First live B1 flown-mission run: kRPC config root cause + fixtures [BUILT, branch `autotest-s05-autorecord-pin`]

First-ever B1-pad-hop live attempts (operator fixture saves landed 2026-07-19). The mission connect timed out, then hung on the first RPC forever; the harness correctly classified every attempt INVALID (tooling-krpc / autopilot-flake), never PARSEK-FAIL. Root-cause chain, live-proven end to end:

- ~~**kRPC settings.cfg partial-file zeroing (THE root cause).**~~ kRPC 0.5.4's `ConfigurationFile` [Persistent] fields are UNINITIALIZED, so any key omitted from settings.cfg loads as zero/false, not the healthy `Configuration` ctor default (ctor defaults apply only when NO file exists). The provisioner's minimal three-key synth therefore silently set `maxTimePerUpdate=0` + `adaptiveRateControl=False`: a 0-microsecond executor budget means NO RPC ever executes while TCP handshakes still succeed (the client hangs forever in `partial_receive` on its first call). Additionally `Configuration.Servers` defaults EMPTY and the on-disk list format wraps entries in `Item` nodes (`servers { Item { ... settings { Item { key/value } } } }`); a wrongly-shaped node is silently ignored and kRPC adds its own random-GUID default server on each boot and re-saves, accumulating duplicates (three observed). Fix: `provlib.stamp_krpc_settings` now emits a COMPLETE golden template (every key at ctor default + the three hands-free overrides + one Item-wrapped fixed-GUID server on 127.0.0.1:50000/50001), ignoring prior contents by design; the SF9 idempotent-skip path RE-STAMPS instead of absorbing the on-disk hash (a stale config was previously unrepairable through the skip). Proven live: krpc.connect + GetStatus + active_vessel green, then a full B1 flight (ascent/coast/chute all phases reached). DISPROVEN suspects, for the record: KRPC.MechJeb (hang persists with the DLL quarantined), client protobuf version (hangs on 4.25.3 too), game pause/focus (UT ticking throughout), port ownership (listener PID = live KSP).
- ~~**Mission stdout lost on budget-kill.**~~ run.py spawned the mission subprocess without `-u`; python fully buffers stdout to a file, so a budget-expired kill lost every line and the hang site was undiagnosable. Spawn now passes `-u`.
- ~~**B1 DESCENT lacks a vessel-destroyed terminal.**~~ Shipped a two-signal terminal. (1) `KrpcMissionControl.read_snapshot` now wraps its body in try/except and tracks a consecutive read-failure streak; a one-off error still re-raises (transient path), but the 3rd consecutive failure (the active vessel handle gone invalid) emits a `TelemetrySnapshot(vessel_lost=True)` snapshot instead of hanging. (2) A pure `mlib` frozen-telemetry detector (`frozen_signature` / `advances_frozen`) catches the bit-identical-orbit-while-UT-advances stall directly: in the airborne B1/B2 phases only (never PRELAUNCH, where pad telemetry is legitimately static), N (`frozenTelemetrySamples`, default 10) consecutive frozen samples terminate the machine. Both signals resolve to `MISSION-ASSERT-FAIL` with a `loss_reason` (a destroyed vessel is a deterministic mission failure, not a flake), so the mission ends in seconds rather than burning its whole budget. ~~STILL OPEN for the operator: the B1 success contract itself (treat chute-deployed+impact as an acceptable end, or require a surviving craft via a fixture change) is unresolved.~~ DECIDED 2026-07-20 (operator, option A): a chute-deployed impact IS a successful B1 end - B1 succeeds when the hop FLEW, the CHUTE DEPLOYED, and the craft REACHED THE GROUND. Implemented as the `DOWN` terminal (branch `autotest-b4-reentry`): in DESCENT with `chute_deployed`, either vessel-lost signal (runner `vessel_lost` snapshot, or the frozen counter reaching its limit) ends phase `DOWN` with `done=True`, NO `loss_reason`, verdict None so the assertions decide; the `landedSituation` assertion accepts the DOWN end (result value `DOWN(chute-deployed impact)` + a `downTerminal` detail flag), and the fly loop skips the settle tail (`B1State.skip_settle_tail` - the vessel is gone, the tail would only gather garbage frames). Without the chute, and in every other phase, the ASSERT-FAIL loss terminals are unchanged. The craft-survives-intact contract moved to the new B4 mission (below); the B1 spec's `recordings.count` widened to `{1,6}` for the touchdown-breakup debris children (4 pids observed once; per-run variance unestablished).
- ~~**KRPC.MechJeb wrong fork for this stack.**~~ Re-pinned to the darchambault fork v0.8.1 (proven-pair table row in `provlib.PROVEN_KRPC_MECHJEB_PAIRS_V054`; release zip sha256-pinned). Live-boot CONFIRMED clean against MechJeb2 2.15.1.0: zero "MuMech.X not found" reflection errors, no InitInstance NRE, no per-boot popup, and the AscentAutopilot flew B2 to orbit. The re-pin also exposed and fixed a provisioner gap: `resolve_git_source` reused a cached clone whose ORIGIN still pointed at the old fork (fetches could never deliver the new tag), so a `cached-wrong-origin` branch now re-points origin before fetching. See `docs/dev/reference-krpc-automation.md` (umbrella docs) for the compatibility research.
- ~~**B2 never left the pad / died at circularize / flaked on MechJeb's own warp.**~~ First live B2 runs peeled four layers, each now fixed with a test: (1) MechJeb's AscentAutopilot engaged via kRPC does NOT ignite the first stage - the PRELAUNCH actions now end with ACTIVATE_STAGE; (2) the MJ-ASCENT -> CIRCULARIZE transition fired mid-burn on the apoapsis window and executed an EMPTY node list (server-side RPCError) - it now requires the autopilot's engaged-then-self-disabled completion latch (`mj_ascent_complete`) AND the apoapsis window, and the circularize action is guarded on a non-empty node list (MechJeb normally performs the burn itself); (3) MechJeb engages its OWN physics warp during ascent, escalating to the stock 4x ceiling, with no KRPC.MechJeb 0.8.1 toggle - the edge-7 warp guard gained a per-mission `max_physics_warp` bound (B2 = 4.0 + ramp allowance; B1 stays 0 = never); (4) diagnosability: post-connect kRPC errors now carry the exception message, and a mid-flight exception preserves the machine's phases_reached for the error result. RESULT 2026-07-20: B2-lko-ascent PASS (MISSION-OK, all verifiers green; attempt-1 MechJeb ascent-budget flake honestly ledgered via flakedThenPassed) - the first autopilot-flown, Parsek-recorded, fully-verified orbital flight, with the six dropped Kerbal X boosters recorded as parent-anchored debris children at analyzer RED=0 (the B2 spec recordings.count now models main + up to 6 debris).
- Fixture saves `harness/fixtures/saves/b1-pad-craft` + `b2-lko-craft` committed (operator-created 2026-07-19, pure-stock sandbox, Jumping Flea / Kerbal X in PRELAUNCH; dev-session `Parsek/` state dirs pruned). Instance profile now pins all volume keys to 0 (unattended, no background audio) and `harness/missions/requirements.txt` freezes the bootstrap-resolved `protobuf==7.35.1` (live-proven with krpc 0.5.4).

## B4 reentry + splashdown mission (b4_reentry) [BUILT + LIVE-PROVEN, branch `autotest-b4-reentry`]

LIVE-PROVEN 2026-07-20: B4-reentry-splashdown PASS (flakedThenPassed; the passing flight flew ascent -> orbit -> ~350s retrograde flip -> aligned deorbit burn -> service-stage drop -> warp hops -> chute at 2950m -> SPLASHDOWN, MISSION-OK, all verifiers green). Four flights peeled two live findings, both fixed with tests:
- **Time-only settle burned mid-flip (flight 1):** the fixed 10s wait throttled up while the Kerbal X was still turning (~180 deg flip needed; the ship was pointing RADIAL) - a radial burn raised apoapsis 84km -> 382km while pushing periapsis through the exit gate, then the warp hops looped forever on an orbit that never touches atmosphere. Fix: attitude AND-gate - the burn requires the kRPC AutoPilot pointing error (`TelemetrySnapshot.ap_error`, NaN when unreadable and NaN fails closed to the bounded deorbit-budget flake) at/below `maxAttitudeErrorDeg` (default 5) IN ADDITION to the minimum settle; apErr is now in the rate-limited telemetry line, and the AP gets heavy-stack deceleration tuning (15s, best-effort) against limit-cycling.
- **Deorbit budget under physical reality (flights 2-3):** the pod reaction wheel flips the whole fuel-heavy orbiter at ~0.5 deg/s (measured live: 175 deg to go = ~340s of game time), 40s over the 300s budget. Spec budgets resized: deorbit 600, mission 1600, run 1900.
Attempt-1-of-the-passing-run variance (a post-burn failure before splashdown) was absorbed by the retry per policy; the flake ledger tracks recurrence. NOTE also observed on B1 and B4 attempt 1: when the craft is DESTROYED (B1's DOWN end), Parsek AUTO-COMMITS the tree at destruction, so the spec's CommitTree step returns ERROR no-active-tree - non-gating after MISSION-OK by design, and the verifiers prove the committed recording is on disk; a surviving craft's CommitTree returns OK.

New flown mission owning the craft-survives-intact contract the B1 option-A decision released: fly the B2 ascent (same Kerbal X fixture `b2-lko-craft`, same MechJeb AscentAutopilot path, ascent-complete latch AND apoapsis window, staged launch, guarded circularize), then deorbit, reenter, and SPLASH DOWN INTACT. Any vessel-lost / frozen-telemetry terminal in ANY phase is an ASSERT-FAIL loss - B4 has NO DOWN-style success carve-out.

- Phases: PRELAUNCH -> MJ-ASCENT -> CIRCULARIZE -> ORBIT (waypoint, NOT terminal: next frame points retrograde and enters DEORBIT) -> DEORBIT (retro attitude settle `retroSettleSeconds` as a pure wait-in-phase condition, one throttle-up, burn until periapsis <= `deorbitPeriapsisMeters`, then cut + AutoPilot release + stage ONCE so the service stage becomes recorded debris) -> REENTRY (bounded rails-warp HOPS: one `ACTION_WARP_TO` = now + `warpHopSeconds` per decision frame, only above `warpAboveAltMeters` AND descending; chutes at `chuteDeployAltMeters`) -> SPLASHDOWN (chute descent; landed/splashed situation is the terminal, settle tail RUNS).
- Attitude control uses kRPC's NATIVE AutoPilot, NOT MechJeb SmartASS: `vessel.auto_pilot` with `reference_frame = vessel.orbital_reference_frame`, `target_direction = (0, -1, 0)` (that frame's y-axis is orbital PROGRADE per the client docs), `engage()` / `disengage()`. Surface names verified against the INSTALLED krpc 0.5.4 python client source in `harness/missions/.venv` (spacecenter.py: AutoPilot class, auto_pilot / orbital_reference_frame properties, warp_to(ut, ...)); live behavior PROVEN 2026-07-20 (retro hold + aligned burn + warp_to hops flew clean; see the proof summary above). The recordings-count window `{1,9}` (main + 6 boosters + LES margin + deorbited stage) held on the passing flight.
- Spec `harness/scenarios/B4-reentry-splashdown.toml` (tier nightly, runtime 1900 s / mission 1600 s / deorbit 600 s after the live re-size), shell `harness/missions/b4_reentry.py` + `b4_reentry.schema.toml`, machine `mlib.B4Params/B4State/b4_decide` + `evaluate_b4_assertions` (assertions: reached-ORBIT phase evidence, peak-apoapsis floor `target - apoError`, final landed situation, chute deployed - deliberately NO orbital precision post-deorbit, and B2's eccentricity/inclination params are absent as dead weight). Runner gained the `ap_point_retrograde` / `ap_disengage` / `warp_to` perform cases; the shared `spec.evaluate` seam now passes the terminated machine state (B1 DOWN detection + B4 phase/chute evidence ride it).
- Coverage-registry gap: no dedicated reentry/splashdown dimension value exists; the spec cites the nearest cells (D4 atmospheric / exo-propulsive / exo-ballistic, D14 kerbin + warp-rails). Add a proper value in a follow-up registry PR if reentry coverage should be first-class.
- B1 + B2 re-tiered `pending-fixture` -> `nightly` (fixtures committed 2026-07-19, both missions live-proven flyable; flown missions are too long for daily).

Review round applied 2026-07-21 (Fable review of the PR, no blockers; 4 SHOULD-FIX + 1 NIT taken as code):
- **SF-1 (B1 DOWN missing "reached the ground"):** the DOWN carve-outs now route through `_b1_down_eligible` - DESCENT + chute deployed + `last_finite_altitude <= down_max_alt` (new `B1Params.down_max_alt`, spec key `downMaxAltMeters`, default 500 m, schema-bounded 10-5000). `B1State.last_finite_altitude` tracks the last finite non-lost altitude sample; a chute-ripped loss at 1800 m is now an ASSERT-FAIL with "; last altitude 1800m" appended to the loss reason instead of a false DOWN PASS. Residual (documented, accepted at the time; ~~OVERTURNED 2026-07-25~~ - see the B1 parachute entry at the top of this file): `chute_deployed` remains COMMANDED evidence (the deploy action was issued and acknowledged), not observed canopy state - kRPC part-state polling per frame was judged not worth the RPC cost for a nightly smoke; the altitude gate now bounds the damage of a failed canopy to a sub-500m impact classification. **That last clause was wrong, and it was the load-bearing one:** a failed canopy leaves the craft at sub-500 m doing 300 m/s, so the altitude gate passes the exact case it was supposed to bound. The chute on this fixture NEVER opened on any B1 run, and the commanded latch certified every one of those impacts as a chute-deployed success. The DOWN gate now reads the OBSERVED `craft_chute_state`, and the one-RPC-per-poll cost that was declined here is what the whole surface depended on.
- **SF-2 (ap_error fail-closed):** `TelemetrySnapshot.ap_error` default changed 0.0 -> NaN. A runner that never populates it (or a read error) now FAILS CLOSED - the B4 attitude gate never opens on a phantom-aligned default, and the bounded deorbit budget converts it to a flake instead of a mid-flip burn.
- **SF-3 (warp hops exo-only):** `B4Params.warp_above_alt` default 45000 -> 70000 (spec updated too); rails-warp hops now happen only above the atmosphere, removing the in-atmosphere hop-overshoot variance band.
- **SF-4:** B4 spec header comments rewritten to the LIVE-PROVEN state with the real budget arithmetic.
- **NIT (runner):** `ACTION_AP_POINT_RETROGRADE` aborts any MechJeb node executor before engaging the kRPC AutoPilot, so the two controllers can never fight during the flip.
Both missions were LIVE RE-PROVEN post-review 2026-07-21: B1 PASS attempt 1 (DOWN fired at last altitude 0.5 m, gate satisfied, all verifiers green) -- ~~that B1 PASS was FALSE and is OVERTURNED 2026-07-25 (see the B1 parachute entry at the top of this file): the chute never opened, and the DOWN gate read the machine's own COMMANDED latch, so a ~300 m/s impact satisfied it~~; B4 PASS flakedThenPassed (attempt 2 flew the full chain with the 70 km warp floor and the craft survived intact, CommitTree OK). B4 attempt 1 stalled in REENTRY with a bit-identical ap/pe/ecc telemetry line (SUB_ORBITAL, warp=NONE) until the mission budget expired - the rate-limited line omits altitude so frozen-physics vs normal Keplerian coast is not distinguishable from the log; if this signature recurs in the flake ledger, add altitude/vspeed to the REENTRY telemetry line as the first diagnostic step.

## Flight 21 (2026-07-22): full-stack PASS, wall 551 s - the warp-audit BASELINE

PASS attempt 1 under the complete merged stack (native warp-to-UT Path A + the three-panel review round + finding 14): BOTH correction rounds burned for the first time (the 300 cap let round 2's ~170 m/s correction fly), flyby 956 km, free-return, all verifiers green, wall 551 s (prior hands-off record 824 s). Flight 20 died pre-flight on the review round's one runner-only defect (perform() picked up read_snapshot's cached control_handle out of scope - NameError at CORRECTION-BURN entry; headless-green because only the real kRPC control runs that line; fixed same-commit). This flight is the BASELINE for the operator's PR gate: no 1x coast segments - the no-1x-coast lane (aim-then-warp node arrivals, warped plan phases, 10x SOI leads, the --warp-audit evidence table) certifies against it.

NO-1X CERTIFICATION ACHIEVED (twenty-sixth flight, 2026-07-22): PASS attempt 1, wall 465 s, flyby 138.9 km, free return, every verifier green -- and `warp_audit.py --fail-on-violation` exit 0 with ZERO contiguous and ZERO cumulative 1x-coast violations under the strengthened (delta-review D1/D2) audit. The full stack live-proven in one flight: flameout staging (the deterministic dry-core pop, twice), the 250 km course-correct target (finding 16d: arrived +137.8 km after round 1, +138.9 after the trim -- the ~112 km planner bias landed inside the characterized range), the arrival-quality gate correctly QUIET above the floor (extraRounds=0), native warp end to end. Finding 16d root cause (flights 22-25, nextPe forensics): MechJeb's OperationCourseCorrection systematically under-prices low-periapsis targets by ~an order of magnitude (a 9.5 m/s in-window plan claiming 60 km moved the prediction +2.7 km where ~39 m/s was needed) while always burning the correct direction -- so the fix is target margin (60 -> 250 km, the contract is the 10 km FLOOR), with the arrival gate as the closed-loop guard. The twenty-fifth flight validated the high-precision window mechanics live (gate held silent through 5,000+ game-s of far coast, granted both extras inside tts < 3,600 s).

Live finding 16 (twenty-third + twenty-fourth flights = certification attempts with the finding-15 stack, 2026-07-22, both ASSERT-FAIL impact-certain): the finding-15 mechanisms validated live (staging popped at avThr=0 and the Poodle lit at 250 kN; the early terminal ended each mission at wall ~445 s with the warp audit CLEAN -- zero 1x-coast violations) but both flights arrived at pe -31.8 / -32.5 km despite both correction rounds executing to <1 m/s residual. The near-identical offset across two flights = a SYSTEMATIC ~90 km miss the blind altitude triggers cannot see (round-2 leverage at 6,000 km is ~12.8 km of arrival-pe shift per m/s, so small post-cut effects move the arrival tens of km). Fix: (a) ARRIVAL-QUALITY RE-CORRECTION - new `next_body`/`next_pe` telemetry (kRPC Orbit.NextOrbit patched-conic arrival body + periapsis, read only while an SOI change is predicted; `nextPe=`/`nextBody=` ride the telemetry line, `nextPe=` the gate lines); once the altitude rounds are exhausted, a sub-floor predicted arrival periapsis at the target body for ARRIVAL_BAD_DEBOUNCE_FRAMES (3) grants a bounded extra PLAN-CORRECTION round (MAX_ARRIVAL_EXTRA_ROUNDS 2) while > ARRIVAL_RECORRECT_MIN_TTS_SECONDS (600) remains to the crossing; every term fails closed. (b) the shared `_b5_enter_plan_correction` helper unifies the altitude-trigger and arrival-gate entries. (c) HIGH-PRECISION WINDOW (twenty-fourth flight, the first with the nextPe channel): the full causal chain became visible -- post-TLI -195 km, round 1 (107 m/s fully executed) only reaches -62 km (MechJeb's long-range course-correct plan under-delivers), round 2 -33.7 km, and the two extra rounds fired immediately at tts ~12,700 s moved the prediction just +4.4 and +1.7 km (at ~12.8 km/m/s leverage the 2.0 cut residual alone is +/-25 km) -- far-out extras cannot converge. The gate now also requires time_to_soi < ARRIVAL_RECORRECT_MAX_TTS_SECONDS (3600): the extras HOLD until the coast is inside the high-precision window (~3.6 km per m/s, cut residual +/-7 km vs the 10 km floor). The prediction is stable across the on-rails coast (patched conics are deterministic), so a held detection cannot rot.

Delta-review round (Fable, 2026-07-22, scope af9b5f5e5..HEAD, verdict SHIP): all seven follow-ups applied same-day. D1 (MAJOR, audit false-clean): warp_audit gains a per-phase CUMULATIVE 1x gate -- fragmentation (1x/warp sawtooth, ramp-sample splits, phase-boundary splits) can no longer dodge the 30 s contiguous threshold; `--fail-on-violation` fails on either gate. D2: 1x segments gate on max(wall, game-span) so sub-1 Hz polling cannot undercount a block. A1/A3: flameout staging runs AFTER the burn-exit/flake checks (an exit frame neither consumes a stage-budget slot for a dropped action nor stages a dead mission). A2: burn entry resets a stale flameout streak (debounce integrity). C1: aligned/flameout streaks capped at their debounce depths (kills the per-frame gate-line + window-dump bloat). B1: WarpService per-dispatch generation token + fresh cancel Event (a zombie thread from a timed-out cancel join can no longer store its stale connection over the live warp's or NaN the live target). E1: B7 spec/schema migrated off the retired nodeWarpLeadSeconds knob. E2 NIT: perform() reuses its cached control handle for node reads.

Live finding 15 (twenty-second flight = certification attempt 1, 2026-07-22, ASSERT-FAIL vessel-lost): the warp profile itself certified (1000x coasts, aim-then-warp arrival apErr 3.4 deg proving rails froze the attitude) but the flyby arrived on an impact trajectory and the descent rode 1x for 589 wall-s to physical destruction - the audit's ONLY violation. Root cause of the bad arrival is ONE latent vehicle defect, not the burner: the Kerbal X CORE stage ran dry mid-round-1 (LiquidFuel froze at exactly 720.0 = the full X200-16 upper tank unreachable behind its decoupler; the 13.2 m/s^2 at 0.25 throttle matches a near-dry Mainsail) and the machine has NO post-ascent staging - round 1 flamed out with 39 of 107 m/s unexecuted (no-progress give-up, working as designed), round 2 throttled a flamed-out engine (dv frozen at 11.087 the full 120 s window). Every earlier pass survived on ascent fuel-margin variance. Two fixes: (a) FLAMEOUT STAGING - new `TelemetrySnapshot.available_thrust` (kRPC Vessel.AvailableThrust; NaN fails closed) + `_b5_flameout_stage` in BOTH burn phases: commanded throttle above the epsilon + zero available thrust for FLAMEOUT_DEBOUNCE_FRAMES (2) pops ONE stage and re-stamps the no-progress anchor, bounded at MAX_FLAMEOUT_STAGES (2) per mission; telemetry gains `avThr=`, gate lines carry it, machine-state/diff lines carry flameoutStreak/flameoutStages. (b) IMPACT-CERTAIN EARLY TERMINAL - the TARGET-FLYBY impact guard sustained for IMPACT_TERMINAL_DEBOUNCE_FRAMES (5) now terminates ASSERT-FAIL immediately (loss reason names the sub-surface periapsis) instead of riding the descent at 1x to destruction; transients still only cost 1x polls, and the terminal cancels any active warp. Also removes the descent 1x-violation class from the audit.

## B5 Mun flyby / free-return mission (b5_mun_flyby) [LIVE-PROVEN 2026-07-22, branch `autotest-b5-mun`]

LIVE-PROVEN 2026-07-22, HONESTLY AND FULLY HANDS-OFF: the sixteenth flight (stair-down warp, zero operator touches) PASSED on attempt 1, wall 824 s, 1,138 km flyby - the unattended certification. The fifteenth flight's pass (below) was operator-warp-assisted through the finding-13 1x cliff. Fifteenth flight: PASS on attempt 1, wall 625 s, under the full finding-1..12 stack - smart-ass forced OFF + debounced aligned gate held until genuinely aligned, the round-1 correction burned REAL thrust pointed at the node (ap 11.48M -> 17.5M chasing MechJeb's own higher-energy intercept solution), round 2 stood down, 1,138 km flyby, free-return, every verifier green. (The ninth flight's earlier "pass" was retroactively found to be a lucky off-axis burn - finding 9 - and this re-proof replaces it.) Operator confirmed live: the Mun-SOI segment warped automatically and correctly; a manual Kerbin-SOI warp was absorbed by the self-healing emission (finding 12c). Residual: the set-instant throttle readback warn persists (MechJeb releases the hold asynchronously within the same tick) - cosmetic, the burn follows; and the flyby periapsis lands far from the 60 km steering target (the floor is the deliberate contract). The passing flight: MechJeb ascent, intercept-targeted TLI via the MechJeb executor, DIY native-AP correction burn (round 1 turned the impact arrival into a 1,142 km flyby; round 2 planned and stood down), rails-hop cross-SOI coast, Mun flyby, free-return into Kerbin SOI, settle tail on-rails, CommitTree OK, every verifier green; wall 1015 s. The flybyPeriapsisFloor assertion is deliberately a FLOOR (10 km): 1,142 km is a valid flyby - the 60 km courseCorrectPeriapsisMeters is a TARGET the best-effort rounds steer toward, never a window. Sampling-band caveat (review N-A3): the min-altitude evidence is polled (~every 50 game-s at the 100x periapsis cadence), so a true periapsis below the floor but above the terrain can slip between samples and still read as met - the floor certifies the sampled track, not a continuous minimum.

Third flown mission per the confirmed lane order (B4 -> B5 -> EVA): fly the B2 ascent (same Kerbal X fixture `b2-lko-craft`), target the Mun, plan a Hohmann transfer with the MechJeb ManeuverPlanner, execute the TLI burn with the autowarping NodeExecutor, refine the flyby periapsis with a course-correction plan, coast across the SOI boundary in bounded rails-warp hops, fly through the Mun SOI, and terminate when the SOI body is Kerbin again (the free-return). Coverage intent: cross-SOI recording (the cohesive ExoBallistic Kerbin->Mun->Kerbin coast), on-rails orbital checkpoints across warp, and the warp-reseed seams the known-bug taxonomy names. Survival is the contract (no DOWN-style carve-out).

- Phases: PRELAUNCH -> MJ-ASCENT -> CIRCULARIZE -> ORBIT (waypoint: set target body + plan transfer) -> PLAN-TRANSFER (wait for the node; bounded re-plan every `planRetrySeconds` while node_count==0 - a no-encounter plan throws server-side and is logged+swallowed by the runner; budget expiry FLAKES, no transfer = no mission) -> TRANSFER-BURN (NodeExecutor autowarp; exit needs node list EMPTY **and** apoapsis >= `transferMinApoapsisMeters` so a no-burn node consume cannot advance) -> PLAN-CORRECTION (same node logic; budget expiry FALLS THROUGH to the coast - the correction is a best-effort geometry refinement, `courseCorrectPeriapsisMeters` = 0 disables both correction phases) -> CORRECTION-BURN -> COAST-TO-TARGET (rails hops `coastWarpHopSeconds`, gated on body==home AND node_count==0) -> TARGET-FLYBY (smaller hops; tracks min finite altitude as flyby evidence) -> RETURN (entered+done when body==home again; settle tail RUNS on-rails).
- SOI gates dispatch on `TelemetrySnapshot.body` (orbit.body.name): body=="" (no reading) stays in phase with no hop; a REAL foreign body (Sun) inside COAST/FLYBY is the ejected ASSERT-FAIL terminal. `node_count` (len(vessel.control.nodes)) drives the plan/burn transitions. Both fields are new snapshot reads; the telemetry line now logs alt/vspd/body/nodes (the B4 attempt-1 diagnosability lesson applied preemptively).
- Assertions (all phase / machine evidence, never a golden trajectory): reachedOrbit, reachedTargetSoi (TARGET-FLYBY in phases_reached), flybyPeriapsisFloor (min altitude in target SOI >= `targetPeriapsisFloorMeters`, default 10 km vs the ~7 km Mun peaks; course-correct targets 60 km), returnedToHome (RETURN reached).
- New runner actions: `set_target_body` (Action gained a `text` field for the body name; `space_center.target_body = bodies[name]`), `mj_plan_transfer` / `mj_plan_course_correct` (ManeuverPlanner operations; make_nodes wrapped try/except), `mj_execute_nodes` (NodeExecutor autowarp=True + guarded ExecuteAllNodes). All KRPC.MechJeb surfaces verified against the pinned darchambault 0.8.1 source; SpaceCenter.TargetBody/Bodies against the pinned kRPC 0.5.4 source.
- Spec `harness/scenarios/B5-mun-flyby.toml` (tier nightly, runtime 2700 / mission 2400 ESTIMATED pending the first live flight), shell `b5_mun_flyby.py` + schema, machine `mlib.B5Params/B5State/b5_decide` + `evaluate_b5_assertions`. recordings.count {1,8} (no post-ascent staging: the TLI + correction burns fly the orbiter stage).
- Coverage-registry gap (same class as B4's reentry gap): no dedicated flyby/free-return value; the spec cites D4 cohesive-cross-body-coast + D3 orbital-checkpoint + D14 kerbin/mun/warp-rails.
- Live-tuning risks to peel on the first flights: whether MechJeb's Hohmann arrival geometry actually free-returns into Kerbin SOI on the Kerbal X (physics says a Hohmann-arrival Mun assist cannot eject - the post-flyby speed stays under escape - but the SOI-exit direction is untuned), the course-correction plan transiently seeing no encounter, and the coast/flyby hop sizes vs the warp guard.

Warp optimization (2026-07-22, branch `warp-optimization`; full findings with source citations in `docs/dev/research/warp-optimization-findings.md`): the machine now chooses only ACHIEVABLE rails factors and stops wasting wall time at 1x. Four changes: (a) every commanded rails factor is pre-clamped against the stock per-body altitude-limit tables (`mlib.STOCK_WARP_ALTITUDE_LIMITS`, ground-truth extracted from the install's serialized CelestialBody data; kRPC clamps a too-high set to RAILS at a LOWER rate and KSP never auto-raises it, so the old commanded-6 post-TLI coast silently ran at 50x), which also makes the on-change discipline escalate the factor as the vessel climbs; (b) the correction-burn attitude flip (the ~340 game-s 1x crawl, twice per mission) runs at 2x PHYSICS warp via the new `set_physics_warp` action (MechJeb's own WarpToUT physics cap; always dropped to 0 before throttle-up; `flipPhysicsWarpFactor=0` reverts); (c) "warp to maneuver node" is now a non-blocking TIME stair (`rails_factor_for_time` + new `node_ut` telemetry): a coast with a pending node warps toward node UT minus `nodeWarpLeadSeconds` (120) instead of forcing 1x, and a new `time_to_soi` bound (kRPC Orbit.TimeToSOIChange) stairs the coast down to the flyby factor before SOI entry so a 0.5 s poll can never blow through a small SOI (the B7 Duna hazard at 100,000x); (d) TARGET-FLYBY stairs up to `flybyMaxWarpFactor` (10,000x) on the outer SOI legs with the proven 100x floor kept through periapsis. Expected ~5-9 wall-minutes saved per B5 pass, more on B6; B7's factor-7 heliocentric coast becomes safe. Impact guard, self-healing emissions, NaN fail-closed semantics, and the MechJeb TLI executor path are all unchanged; pending nodes with unknown UT still hold 1x.

Warp optimization, Path A native warp-to-UT (2026-07-22, branch `native-warp`; research `docs/dev/research/native-warp-to-ut.md`, implementation notes appended to `warp-optimization-findings.md`): the long time-bound waits now use the GAME's own fire-and-forget warp. A runner-side `WarpService` owns a dedicated second kRPC connection on a daemon thread that sits inside the blocking `SpaceCenter.WarpTo` while the primary connection keeps polling (per-connection RPC serialization); cancel = close the warp socket + zero both factors from the primary; a watchdog clears a remotely-detected pause (`KRPC.Paused`) or cancels on a 10 s wall UT stall. New machine actions `warp_to_ut`/`cancel_warp` + snapshot field `warping_to` (NaN = idle, fail closed): pending-node coasts warp to node UT minus `nodeWarpLeadSeconds`, the post-correction coast and the flyby outer legs warp to now + timeToSoi minus `soiLeadSeconds` (60, new spec param; re-issue only on a >120 game-s SOI-estimate shift, self-heal bounded to once per 30 game-s), and the machine never emits a rails factor while a native warp is active (cancel first). The correction-trigger approach keeps the live-proven rails distance stair, and the stair + altitude tables remain the documented fallback, demoted to hints on the native legs per the queued table-free refinement (the game now owns factor legality there). Impact guard cancels the native warp and stays authoritative; RETURN entry and the trigger prelude cancel; all detectors/give-ups/debounces unchanged. 200 mission-lib tests green.

Live finding 1 (first flight 2026-07-21, both attempts flaked in TRANSFER-BURN): `OperationTransfer` planned the arrival/capture burn as a SECOND node, the executor consumed the TLI (nodes 2 -> 1, ap 11,341 km, healthy coast at 9600x autowarp) and warped toward the arrival node while the machine's node_count==0 exit parked until the burn budget flaked. The FLIGHT was healthy; only the exit contract misread it. Fixed two-layered: (1) the runner forces intercept-only planning (`capture` / `plan_capture` / `rendezvous` = False before make_nodes); (2) the BURN exits switched to "node_count fell below the planned handoff count (B5State.planned_node_count) AND the apoapsis floor", with stray leftover nodes aborted+cleared via the new `mj_abort_and_clear_nodes` action (NodeExecutor.abort + Control.RemoveNodes) so the coast hops are never suppressed by a pending stray and no unwanted capture burn ever flies.

B7 LIVE-PROVEN (seventh flight, 2026-07-22): PASS attempt 1, wall 752 s, every verifier green, analyzer RED=0, warp_audit --fail-on-violation exit 0 (zero contiguous + zero cumulative 1x-coast) -- the FIRST interplanetary mission: 700 km park, single-node phase-angle ejection (mid-burn flameout staged, executor resumed), the machine CREATED the Duna encounter in heliocentric space (round 1: no-encounter trigger + 108.7 m/s course-correct, arrival 18,435 km), the tts-500k round refined it to a 546 km flyby (28.3 m/s, the finding-16d under-delivery landing on the SAFE high side of the 50 km request), Duna SOI transit, Sun-exit free return. Spec now targets 300 km (16d margin) for future flights.

B7 first-flight campaign (flights 1-7, 2026-07-22, peel-one-layer): live-proves Q2/Q3/Q4 ALL ANSWERED GREEN on flight 4 -- ejection priced 797.6 m/s as ONE node (the 700 km park's energy discount vs the ~1,050 estimate), the executor held ~100,000x across the ~35-day window wait, and the burn completed with 544/720 LF still in the Poodle (margin comfortable). Findings, each committed with tests: (flight 1) ascentTimeoutSeconds 1200 -> 2400 (the 700 km apoapsis coast + MechJeb's at-apoapsis circularization run ~1400-1600 game-s; the flake hit with ap already in-window); (flight 2) ROUGH PARK WINDOWS +/-15 -> +/-150 km + circularize budget 600 -> 6000 (MechJeb's high-altitude circularization left ap 778 / pe 562 km with no node and nothing burning; the park's contract is warp legality >= 600 km + a stable ejection origin, not precision); (flight 3 = finding 17) flameout staging under the EXECUTOR's collapsed throttle -- the deterministic core-dry flameout hit mid-ejection at 476.9 of 797.6 m/s remaining and MechJeb zeroes the throttle when the engine dies, so the commanded-throttle gate was blind in TRANSFER-BURN; mid-burn evidence (orbit changed since entry + node still pending + zero available thrust, via _b5_track_burn_stagnation's new burned return) substitutes, and flight 4 proved the executor RESUMES on the fresh stage and finishes the burn; (flight 4 = finding 18) NO-ENCOUNTER EARLY TRIGGER -- the phase-angle ejection reliably produces NO Duna encounter (design Q5's contrary assumption refuted; tts NaN the whole heliocentric coast, triggers correctly closed, coast sailed past Duna to the budget flake), so a debounced (3-frame) encounter-less time-mode coast over a via body now fires the pending round early; flight 5 then proved MechJeb's course-correct PLANS WITHOUT an encounter (108.7 m/s accepted, Q5's throw assumption also wrong); (flights 5-6 = findings 19/19b) HIGH-RATE FRAMES ARE NOT ALIGNMENT TIME -- rounds granted from the 100,000x coast entered CORRECTION-BURN mid-rails-ramp-down and the GAME-time no-start budget (600 s) evaporated in ~2 polls with the plan unburned and apErr frozen ~110 deg; 19 re-anchored on rails frames, 19b re-keyed the re-anchor to the OBSERVED RATE > NOSTART_COUNTABLE_RATE_MAX (4.5) because commanding the flip mid-ramp flips TimeWarp.Mode to LOW immediately while CurrentRate still decays from 100,000 (kRPC truthfully reports PHYSICS at 5.32x). Plus the ec= ElectricCharge telemetry channel (a drained battery on the solar-panel-less Kerbal X presents exactly like the frozen apErr; next suspect if the flip still will not turn under an honestly-counted clock).

## B6 Minmus / B7 Duna flybys: Kerbal X feasibility survey + lanes [B6 LIVE-PROVEN, B7 LIVE-PROVEN 2026-07-22, branches `autotest-b5-mun` / `autotest-b7-duna`; **B6 CONFIRMATION RE-FLY PAID 2026-07-25** - PASS, wall 359 s, all seven verifiers green on HEAD, clearing the debt the B12 flight-1 entry opened (B6's prior proof predated the aim-then-warp change and it shared the budget that change broke; it was also the mission most exposed to the flight-2 coast warp-thrash, same long Minmus coast). B5/B7 exposure RE-ASSESSED 2026-07-25 after review: the "NOT-AFFECTED, the fixes gate on `capture_enabled` / opt-in snapshot fields" claim was WRONG for two of the four fixes (the correction-budget re-anchor and the coast native-warp latch are ungated shared-machine changes). **B5 CONFIRMATION RE-FLY PAID 2026-07-25** - PASS attempt 1, wall 468 s, all seven verifiers green on HEAD (`results/2026-07-25_0643_B5-mun-flyby.json`, repeated at 468.9 s), so the proxy argument is retired: B5 is ACTUALLY RE-FLOWN, not covered by B11. B7 was FLOWN at HEAD instead of argued about and is INTERMITTENT, not a flat fail - the 2026-07-25 sweep red'd attempt 1 (`MISSION-ASSERT-FAIL`, Ike capture, wall 736 s) and PASSED attempt 2 on the harness retry (wall 739 s), and an earlier run red'd both attempts; see the B7 Ike entry below. The failure is pre-existing, not a lane regression]

**B7 AT HEAD FAILS: the 300 km Duna approach gets captured by IKE (2026-07-25, OPEN).** The B7 row already carried the gate "HEAD's 300 km target has not itself flown (the pass flew 50 km); first nightly covers it". That flight has now happened - twice, as the harness retry - and it FAILS: `MISSION-ASSERT-FAIL reason=flyby ejected the craft off-course: body='Ike' (expected 'Duna' or exit 'Sun')`, wall 747 s / 735 s.

What actually happened (both attempts of that run identical in shape): the transfer and corrections worked and the 300 km target was HIT - the craft entered Duna's SOI and read `pe=310089` - but the inbound approach to a ~310 km periapsis has to transit the shell where Ike orbits, and Ike was there. `window[19] body=Duna alt=3,687,346` then `window[20] body=Ike alt=897,085`. Attempt 1 recorded a min sampled Duna altitude of 3,888,111 m and attempt 2 3,687,346 m.

**CORRECTION 2026-07-25 (later the same day): it is INTERMITTENT, not deterministic.** The first full daily+nightly sweep flew B7 again: attempt 1 INVALID (the Ike capture), attempt 2 PASS, terminal PASS at `attempts=2 wallTotal=1564s`. So the earlier reading of the two-attempt run - "the approach geometry, not a one-off timing fluke" - was WRONG as stated: the geometry puts the approach INSIDE Ike's orbital shell every time, but whether Ike is near enough to capture depends on its phase at arrival, and arrival timing varies run to run. B7 therefore reads as a FLAKY scenario (currently 1 of 2 sweeps needing the retry), not a hard red. That is worse for suite economics than a clean failure: it costs a full extra Duna flight (~780 s) whenever it fires and it silently doubles the scenario's cost, which is exactly what the new `scenario cost attempts=N wallTotal=Xs` line was added to surface.

NOT a regression from the ORBIT lane, on evidence: (a) `corrBudgetAnchorUt=none` - the correction-budget re-anchor never engaged, so the flight ran main's exact bound; (b) `phaseWarpIssues=1` - the coast native-warp latch worked, and that is WHY the flight reached Duna at all: every archived pre-fix B7 run (`2026-07-22_2052/2108/2122`) only ever logged `SOI change boundary suppressed in tree mode: Kerbin to Sun` and never reached the target SOI. The lane's changes moved B7 FORWARD, from "never arrives" to "arrives and gets grabbed by the moon".

Also worth recording: `flybyPeriapsisFloor` PASSED at 3,687,346 m while the true orbital periapsis was 310,089 m. That is the documented behaviour (the assertion certifies the min SAMPLED ALTITUDE inside the target SOI, a floor over a coarse poll, not the orbital periapsis) and it read the altitude at which the craft was yanked into Ike before it could descend further. Not a defect, but the name invites the wrong reading.

Fix is a B7 SPEC decision, not a machine one, and is deliberately NOT taken here: either accept an Ike encounter as a legitimate Duna-system arrival (it is arguably a BETTER Parsek test - a second SOI transition and more recording topology), or aim the approach to clear Ike's shell. Whoever takes it should decide which surface B7 is meant to exercise before re-tuning numbers.

Operator directive 2026-07-21: after B5, implement similar flyby missions for Minmus and Duna, checking what the Kerbal X can fly. Delta-v survey (orbiter stage holds ~1500-1600 m/s after the 80 km circularization, proven by the B5 TLI flights): Minmus transfer ~930 m/s FEASIBLE; Duna ejection ~1050-1080 m/s at the window FEASIBLE with correction margin; Eve ~1090 m/s feasible as a later clone of the Duna shape; Jool (~1900+ m/s) and Moho are NOT feasible on this craft.

Live finding 9 (first B6 flight + B5-pass forensics, 2026-07-22): B6's TLI to 46,164 km was perfect and round 1 handed off cleanly, but THREE defects showed in the correction burn. (a) SIGN BUG: kRPC's AutoPilot.error reads NEGATIVE in some regimes (B6: -178 deg mid-flip decaying to -0.04; B5's pass: the throttle opened at a POSITIVE 98-ish that flipped negative) - the `<=`-only gate passed while pointing the WRONG WAY, so B5's round-1 "correction" was a wild off-axis burn whose ap 11.5M -> 17.5M accident produced the 1,142 km flyby. THE B5 PASS WAS LUCKY and needs re-proof under the fixed gates. Fix: abs() on BOTH attitude gates (B5 corrections + B4 deorbit). (b) ZERO-THRUST BURN: B6's round-1 "burn" sat with nodeDv FROZEN at 14.038 for 2500 frames despite the throttle command - cause undiagnosable (dry tanks vs held throttle vs pointing) because fuel and throttle were not in telemetry. Fix: `TelemetrySnapshot.liquid_fuel` + `throttle` readback fields + `lf=`/`thr=` telemetry entries (diagnosability channels), plus a NO-PROGRESS give-up in the started branch (remaining dv not dropping within burnStagnantSeconds of throttle-up/last progress = nothing is burning; round consumed). (c) GLACIAL FLIP: 0.06 deg/s from near-anti-parallel with the (15,15,15) deceleration tuning - the correction gate default moved 5 -> 30 deg (the DIY burn chases the node's remaining vector, so a rough start self-corrects; overshoot + no-progress own the failure modes) and AP_POINT_NODE drops the deceleration override (kRPC defaults turn faster; B4's tuned retro hold keeps its own) (superseded by finding 11: the override is RESTORED - removing it was backwards on a low-torque craft).

Live finding 10 (second B6 flight, 2026-07-22; the finding-9 telemetry channels answered everything in ONE flight): `lf=720` (tanks fine) and `thr=0.000` AFTER the set_throttle command - MechJeb's thrust controller keeps ZEROING the throttle after the TLI executor runs, until the executor's full abort teardown releases it. That is why B4's deorbit SET_THROTTLE always worked: AP_POINT_RETROGRADE aborts the executor first. Fix: AP_POINT_NODE now runs the same `node_executor.abort()` before engaging the native AP (executor-poisoning is moot - corrections never touch the executor again). Also confirmed live: the flip runs ~0.5 deg/s again without the deceleration override, and the no-progress give-up bounded both dead burns at ~120 s each, exactly as designed.

QUEUED REFINEMENT ~~(largely DONE via Path A, branch `native-warp`, 2026-07-22: the sibling research found the fire-and-forget native primitive IS reachable through a second kRPC connection running SpaceCenter.WarpTo on a background thread, so the node-wait / SOI-coast / flyby-outer legs now delegate factor legality to the game natively; the rails stair + tables remain only for the correction-trigger approach and as the fallback - the periodic re-command idea is superseded on the native legs)~~ (operator design principle, 2026-07-22): replace the altitude-limit TABLES with table-free native adaptation. The operator's point: a native warp-to-point delegates factor legality to the game, which can never disagree with itself; our extracted timeWarpAltitudeLimits tables can drift (modded systems, KSP versions). KRPC.MechJeb 0.8.1 does NOT expose MechJebModuleWarpController (verified against the pinned source) and kRPC's warp_to blocks (banned wedge class), so the fire-and-forget native primitive is unreachable today. The table-free design that achieves the same: command the pure time/distance stair factor, let the SERVER clamp it (safe by construction), and add PERIODIC RE-COMMAND (~15 s cadence) because KSP clamps a too-high request but never auto-raises it as altitude climbs - the machine then always receives whatever the game currently permits, zero replicated tables, auto-adapting. The extracted tables demote to optional hints. Implement alongside the flyby-stair clamp tuning (RAILSx50 observed in Mun SOI on flight 17) and the wall-time attribution pass.

No-1x-coast (OPERATOR PR GATE, 2026-07-22, branch `no-1x-coast`): "we cannot have tests that run at 1x during coast" - every by-design 1x coast window in the B5/B6 machine is eliminated and the proof tool exists. (1) AIM-THEN-WARP: the correction burn now points FIRST (the proven 2x-physics flip + aligned debounce), then natively warps to node UT minus `nodeArrivalMarginSeconds` (15, new param; rails warp freezes vessel orientation so the burn vector holds), re-verifies the streak on arrival (a drifted attitude re-enters the flip, bounded by the give-up re-anchored at arrival - warp time is not alignment time), then throttles; `nodeWarpLeadSeconds` is retired (the coast's pending-node path uses the same margin, so 1x exists only inside the 15 s margin plus the ~2 re-verify frames). (2) PLAN phases ride `planWarpFactor` (2 = 10x, legality-clamped) between plan attempts and `planRetrySeconds` drops 30 -> 10 (3 attempts cost ~30 game-s, ~3 wall-s; make_nodes is an RPC and needs no 1x; the frozen detector is warp-gated since N-A4 so plan frames advance no staleness count). (3) `soiLeadSeconds` halves to 30 and the inside-lead fallback keeps the 100x flyby floor (never 1x); the correction-trigger stair floors at factor 2 (trigger overshoot at 10x is <= ~5 game-s, a refinement point not a wall). Invariant TESTED (`test_no_1x_coast_invariant`): in COAST/FLYBY the machine commands rails 0 ONLY on the impact guard, a pending node inside the arrival margin / unknown node UT, or a blank body/altitude reading. Measurement: `harness/warp_audit.py <mission_stdout_log>` renders per-phase warp segments (mode+rate bucket, wall + game estimates) plus a 1X-COAST VIOLATIONS section (coast-class phases, 1x >= 30 wall-s, with surrounding events); pinned against the committed pre-fix 1210 flight log (shows exactly the finding-14 277 s PLAN-CORRECTION violation) and a clean post-fix profile. Expected next-flight 1x profile: burns + <= 15 s arrival margins + nothing else.

Live finding 14 (seventeenth flight, 2026-07-22; operator observed the 1x block live): the PLAN-CORRECTION disqualification loop held 1x for the full 300 s plan budget - round 2 repeatedly priced at 169-171 m/s vs the old 150 cap, each plan removed runner-side, and the machine (which only sees node_count stay 0) re-planned every 30 s to the bitter end. Root cause is two-part: (a) no attempt bound - fixed with the PLAN_MAX_ATTEMPTS (3) give-up: the entry plan counts as attempt 1, each cadence re-plan increments, and the next cadence check after 3 attempts takes the timeout path early (PLAN-CORRECTION falls through + consumes the round; PLAN-TRANSFER flakes) - worst-case 1x drops ~300 s -> ~90 s; (b) the 150 cap now blocks REAL corrections - this flight's round-1 DIY burn genuinely worked (ap 11.48M -> 19.47M, ~250 m/s), so round-2 economics legitimately reach ~170 m/s; the cap's job is only the wild-burn insanity class (finding 2's 15,930 m/s) and the DIY burner + aligned debounce + no-progress give-up now own the wild-burn risk -> maxCorrectionDvMps 150 -> 300 in both flyby specs (schema max stays 2000).

PENDING-OPERATOR traceability note (review, 2026-07-22): findings 3, 10, 11, and 12b are runner-only kRPC-object manipulations (rendezvous/intercept planner flags, the executor-abort-before-native-AP ordering, the (15,15,15) deceleration_time override, Smart A.S.S. forced OFF) - they are LIVE-VERIFIED ONLY by design: no automated guard can exercise MechJeb's server-side controller state headless, so regressions in these four surface only in live flights. Treat any recurrence of their symptom signatures (no-encounter coast, zero-thrust burn, sub-0.1 deg/s crawl, throttle zeroed after set) as the first triage suspects.

Live finding 13 (operator report post-B5-pass, 2026-07-22): the slow-down band was a 1x CLIFF - the machine held 1x for the ENTIRE 2,000 km band below each correction trigger (~40 real minutes at coast speeds); the operator had to warp the Kerbin-SOI segment manually even on the passing flight. Fix: distance-scaled STAIR-DOWN (`rails_factor_for_distance`: the highest factor whose warped travel over a 1 s safety window fits the remaining distance - 1000x far out, 100x at ~100 km, 5x at ~5 km, 1x only in the last moments; speed floored at 10 m/s conservatively; `warpSlowdownMarginMeters` retired). The Mun-SOI segment's automatic warp was confirmed correct by the operator on the passing flight.

Live finding 12 (B5 re-proof attempt + B6 pass forensics, 2026-07-22): three-part batch for the fifteenth flight. (a) TRANSIENT-GATE WILD BURNS: the loud diagnostics caught `set_throttle 0.25 did not stick (readback 0.00)` AND yet a ~200 m/s off-axis burn happened at a true ~98 deg error - a SINGLE-FRAME transient attitude reading (slipping between rate-limited samples) opened the aligned gate; fix = ALIGNED_DEBOUNCE_FRAMES (2 consecutive in-gate readings; `B5State.aligned_streak`). (b) the crawl + throttle hold SURVIVED the executor abort and sas=False - the remaining holder is MechJeb's ATTITUDE controller; fix = Smart A.S.S. forced OFF (SmartASSAutopilotMode.Off + Update(false), pinned-source verified) before engaging the native AP, logged loudly. (c) SELF-HEALING WARP: the operator's manual warp changes (and KSP's own automatic drops) silently overrode the held factor and the on-change-only discipline never re-asserted - the re-proof coasted at 1x to the wall budget; fix = re-emit whenever the game is not rails-warping despite a nonzero command (idempotent).

Live finding 11 (third B6 flight, 2026-07-22; operator observed the 1x multi-hour coast live and flagged it): finding 9(c) was BACKWARDS. `deceleration_time` on a low-torque craft RAISES the angular-velocity cap (the AP only permits spin it can stop inside the window, omega_max ~ alpha * window); removing it dropped the flip to ~0.05 deg/s under kRPC's default ~0.5 s stopping profile (apErr -163 crawling ~0.05 deg/s live), so every correction round would starve to its give-up and the mission wastes hours at 1x. The (15,15,15) override is RESTORED in AP_POINT_NODE (B4's measured 0.5 deg/s flip runs it; 163 deg to the 30 deg gate then fits well inside the 600 s give-up). Flight-2's "faster flip" inference was wrong. SEPARATELY QUEUED (operator design critique, same session): replace the blocking warp_to COAST hops with non-blocking rails-factor control - the observed 100x/1000x sawtooth is the per-hop ramp-down/up seam; poll-driven warp would change speed only when an action is imminent, keep telemetry continuous through the coast, and structurally remove the blocking-RPC wedge class (finding 4's Flight Results dialog wedge).

- **B6 Minmus [BUILT]:** thin alias shell `b6_minmus_flyby.py` + schema copy over the body-parameterized B5 machine (zero machine changes - targetBodyName was a param from the start); spec `B6-minmus-flyby.toml` re-sizes the numbers (transfer floor 40,000 km vs the ~46,400 km Minmus orbit, flyby floor 6 km vs the ~5.7 km peaks, correction to 20 km under the same 100 m/s cap, ~9-day coast budget with 1-hour hops). Fly AFTER B5 passes; every B5 live finding transfers.
- **B7 Duna [LIVE-PROVEN 2026-07-22, branch `autotest-b7-duna`; design `docs/dev/design-autotest-b7-duna.md`]:** the design doc's mlib diff plan applied, ADAPTED to the no-1x-coast / native-warp machine (the doc's section 5 predates it). New `ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER` (runner sets WaitForPhaseAngle=True, same throw/log/swallow contract as operation_transfer) selected by `interplanetaryTransfer`; five new B5Params (viaBodyNames / returnBodyName / interplanetaryTransfer / ejectionEccFloor / correctionTriggerTimeToSoiSeconds), every default its B5-preserving value - the whole pre-B7 test suite passes unmodified as the byte-identical proof. Machine deltas, all param-gated through the new `_b5_*` pure helpers: hyperbolic ejection burn-done gate (home-frame ecc >= ejectionEccFloor in home SOI OR already-left-home replaces the apoapsis floor - an escape burn drives the home-frame apoapsis negative); via-body coast legality (Sun is a legal intermediate SOI, not the ejected terminal; the Sun row of STOCK_WARP_ALTITUDE_LIMITS - factor 7 needs >= 65,400 km, ground-truth extracted with the other 16 bodies - keeps the factor-7 heliocentric coast from clamping low); TIME-TO-TARGET-SOI correction triggers over via bodies (entry through the shared `_b5_enter_plan_correction`; the trigger approach warps NATIVELY to the trigger UT when far - time_to_soi falls 1:1 with UT so the target is exact and inherently SOI-safe - and rides the factor-2-floored rails time stair inside soiLeadSeconds, never passing a trigger un-polled; the home-SOI escape leg rides the existing native warp to the SOI exit); the finding-16 arrival-quality gate's body domain widened to home+via (stays quiet while rounds are pending or the predicted arrival is at/above the floor); returnBodyName RETURN terminal (a Duna flyby exits into SUN SOI; `returnedToHome` keeps its NAME, its value/`returnBody` detail now report the actual exit body). Shell `b7_duna_flyby.py` (thin alias, settle_frames=0 like B5/B6) + schema copy + spec `B7-duna-flyby.toml` (700 km park for the factor-7 ejection-window wait; correctionTriggerAltsMeters=[] + correctionTriggerTimeToSoiSeconds=[20000000, 500000] select time mode; budgets estimated, not measured). LIVE-PROVE items (design section 8) ALL ANSWERED GREEN by the flight-1..7 campaign (see the LIVE-PROVEN paragraph above): **#1 (Q2, highest risk)** read the post-ascent + post-ejection stage dv from the 700 km park - the correction margin tightens to ~350-450 m/s after the ~100-150 m/s net Oberth penalty; fallback = 600 km park or a single correction round, do NOT tune speculatively; **#2 (Q3)** confirm the NodeExecutor autowarp holds 100,000x across the ~200-day ejection-window wait (fallback = wire WARP_TO_UT for that leg); **#3 (Q4)** confirm OperationInterplanetaryTransfer plans exactly ONE node (the planned_node_count handoff + exit stray-clear degrade a two-node plan gracefully).

Live finding 2 (second flight 2026-07-21, both attempts, fully deterministic - TLI to ap 11.402M twice): intercept-only planning worked (single node, clean 1 -> 0 exit), but the course-correction MechJeb planned 0.5 s after the TLI was NOT a small tweak - executing it re-shaped the transfer (ap 11.4M -> 16.6M, pe 76k -> 152k) and the executor then sat wedged on the partially-burned node (no warp, no thrust, orbit frozen) for the whole 4000 game-second burn budget. Fix: `maxCorrectionDvMps` dv cap (default 100, spec param; Action gained a `limit` field) - the runner sums the planned nodes' `delta_v` (kRPC Node.DeltaV, pinned-source verified) and REMOVES an over-cap plan, so node_count stays 0 and PLAN-CORRECTION's designed fall-through coasts on the raw Hohmann intercept. A genuine correction under the cap still flies.

Live finding 8 (eighth flight 2026-07-22): the read-tolerance fix PROVED itself (the Mun impact ended as the honest vessel-lost ASSERT-FAIL with the flyby floor sampled at 203 m), but round 2 STILL no-started even without the external abort - hypothesis (a) of finding 7 REFUTED. The real mechanism, decompiled from 2.15.1 `StateWarpAlign`: with autowarp the executor warps only when `AlignedAndSettled()` (< 1 deg AND angular velocity < 0.001 rad/s - which the low-torque Kerbal X NEVER satisfies at 1x) OR when ignition is > 600 s away. Round 1's node sits ~2500 s ahead (the far-warp branch carries it); round 2's close-in correction node hits the un-meetable settle gate and parks forever. This is hardcoded MechJeb behavior. Fix: DIY CORRECTION BURNER - corrections no longer use MechJeb's executor at all. PLAN-CORRECTION hands off to `ap_point_node` (native kRPC AP at Node.ReferenceFrame, direction (0,1,0) = the burn vector, pinned-source verified; same deceleration tuning as the B4 retro flip), and CORRECTION-BURN is the B4-proven pattern: settle (`correctionSettleSeconds`) + attitude AND-gate (`maxAttitudeErrorDeg`), ONE low-throttle burn (`correctionThrottle` 0.25), cut when the node's remaining dv (`TelemetrySnapshot.node_dv`, new field + telemetry-line entry) reaches `correctionCutDvMps` (2.0) or starts RISING (overshoot); alignment-never-converges gives the round up at `burnNoStartSeconds`; every exit cleans up (cut/disengage/clear) and consumes the round. TRANSFER-BURN keeps the MechJeb executor (far-node autowarp is its proven regime) + the stagnation watchdog. Also: the tolerance override from finding 5 was a DUD (the KRPC.MechJeb wrapper never initializes its Tolerance backing object, so the set throws server-side into the best-effort catch) - removed with an honest comment. NOTE for later triage: this crashed flight's recording turned the analyzer RED with INV2-NO-DOUBLE-COVER (annotated triage-only since the driver failed) - potentially a REAL Parsek find from a Mun-impact recording; collected in ../logs/2026-07-22_*_B5-mun-flyby.

Live finding 7 (seventh flight 2026-07-22, both attempts; deepest run yet - entered Mun SOI): three sub-findings. (a) EXTERNAL ABORT POISONS THE EXECUTOR: perfect correlation across flights 6-7 - every fresh ExecuteAllNodes works (TLI, round 1), every execute AFTER our cleanup's node_executor.abort() never starts (round 2, twice; the no-start watchdog bounded it at 600 s). The decompiled 2.15.1 executor aborts ITSELF next frame when the node list is empty (OnFixedUpdate !_hasNodes -> Abort), so the cleanup now calls ONLY Control.RemoveNodes and lets MechJeb run its own abort path (its attitude/thrust user bookkeeping stays consistent). (b) FLY-LOOP READ TOLERANCE: the arrival was still an impact (round 1's wedge-cut partial burn stood, round 2 no-started) and the crash raised a SERVER-ANSWERED RuntimeError (maneuver-nodes read unavailable on the destroyed vessel, server stack trace attached) which killed the mission as MISSION-ERROR on the FIRST raise - the 3-strike vessel-lost streak was UNREACHABLE live because the fly loop had zero per-frame read tolerance (latent since M-B1; only ever exercised headless). The loop now tolerates non-transport read exceptions (warn + poll on; the streak escalates to vessel_lost within 2 more polls) and re-raises TRANSPORT drops immediately (new `mlib.is_transport_drop_exception` / TRANSPORT_DROP_EXCEPTION_NAMES, deliberately narrower than the FLAKE classifier's set: RPCError-class = server answered = vessel-state, not a drop). (c) the MISSION-ERROR-vs-vessel-lost mis-classification becomes moot under (b).

Live finding 6 (sixth flight 2026-07-22, both attempts): round 1 now works END TO END (plan, ~2500 s autowarp to the node, burn, wedge, WATCHDOG UNWEDGE - the finding-5 machinery proven live), and the 6M trigger fired round 2 on time. But round 2's executor NEVER STARTED: node planned instantly, execute issued, then ~2000 wall-seconds at 1x with the orbit untouched until the WALL budget died (the after-burn watchdog correctly does not fire pre-burn - that is its alignment discriminator). Likely mechanism: the round-2 residual correction is smaller than the executor's own tolerance, so it never engages. Fixed two-layered: (1) runner-side NEGLIGIBLE-CORRECTION SKIP - a plan under 0.5 m/s total dv is removed with an info log (the trajectory is already good; a sub-0.5 m/s residual is within the flyby floor's margin); (2) machine-side NO-START WATCHDOG (`burnNoStartSeconds`, default 600 > the ~340 s worst-case flip): orbit unchanged since burn entry + static at 1x for that long in CORRECTION-BURN = abort+clear + round consumed. TRANSFER-BURN keeps only the after-burn signal (the TLI executor started on all six flights; its phase budget owns a no-start).

Live finding 5 (fifth flight 2026-07-22, both attempts): the round-1 correction plan was accepted first try (under the 150 cap) and the executor BURNED it (Kerbin pe swung to -89 km - harmless on an outbound leg), but then held the completed node forever: nodes=1, warp NONE, orbit frozen, CORRECTION-BURN flaked on its 4000 s budget. Same executor-wedge class as finding 2 but decoupled from oversized plans - the executor chases its 0.1 m/s default tolerance with the Kerbal X's low-torque pod wheel and never declares the burn done. Fixed two-layered: (1) `node_executor.tolerance = 1.0` at execute time (a 1 m/s sloppier finish is irrelevant; the next correction round refines); (2) machine-side BURN-STAGNATION WATCHDOG (`burnStagnantSeconds`, default 120): once the orbit has CHANGED since burn entry (a burn happened) and then sat static at 1x with the node pending for that long, abort+clear and move on - in CORRECTION-BURN the round is consumed, in TRANSFER-BURN the floor decides (met = coast; unmet = bounded flake, the TLI under-burned). RAILS autowarp (static orbit, warp != NONE) and pre-burn alignment (orbit unchanged since entry) never count.

Live finding 4 (fourth flight 2026-07-21/22): `rendezvous=True` produced a REAL encounter (Mun SOI entered on the first apoapsis pass), but the arrival was an IMPACT trajectory (pe -28.7 km): MechJeb quoted 108-118 m/s corrections (just over the 100 cap, disqualified thrice), a fourth under-cap plan flew, and a ~1.5 m/s lateral executor residual over the remaining 14,000 s coast drifted the corrected +60 km periapsis to -29 km (a dead-center lunar aim is that sensitive). The crash then happened INSIDE a blocking warp hop: KSP's Flight Results dialog paused the game clock and the mission process wedged in the warp_to RPC until the mission budget reaped it (~17 wasted minutes; operator observed the dialog live). Fixes: (a) correction ROUNDS - `correctionTriggerAltsMeters` (default [0, 6000000]): COAST-TO-TARGET enters PLAN-CORRECTION once per trigger crossing, so a mid-coast refinement prices the residual at a few m/s (`B5State.correction_rounds_done`; the TRANSFER-BURN -> PLAN-CORRECTION direct branch is gone, rounds are coast-triggered); (b) cap raised 100 -> 150 so flight 4's legitimate 108-118 m/s quotes fly; (c) IMPACT-WARP GUARD - in TARGET-FLYBY, below 400 km with a sub-surface periapsis the machine polls at 1x instead of hopping, so a crash lands under live telemetry and the vessel-lost detectors end the mission in seconds, never a wedged RPC.

Live finding 3 (third flight 2026-07-21; the dv cap doubled as the diagnostic): the capped correction warns revealed MechJeb demanding 15,930-25,559 m/s immediately post-TLI, settling at a PERSISTENT ~403 m/s - and the coast then reached apoapsis 11.39M and fell BACK to Kerbin with no encounter. Root cause CONFIRMED in the decompiled MechJeb 2.15.1 `OperationGeneric`: `Rendezvous` is the TARGETED-INTERCEPT flag (it flows into `DeltaVAndTimeForHohmannTransfer` as the arrive-AT-the-target mode; the GUI pairs "Rendezvous" vs phase-blind "Transfer"), so finding 1's over-eager `rendezvous=False` degraded the plan to a phase-blind altitude-only Hohmann with a deterministic miss - and finding 2's monster "correction" (flight 2 executed a 15,930 m/s plan uncapped, wedging the executor on dry tanks) was MechJeb pricing the re-aim to CREATE the missing encounter. Fix: `capture=False` (the GUI's intercept-only checkbox is literally `Capture = !intercept_only`) + `plan_capture=False` + `rendezvous=True`. Also of note from the decompile: MechJeb itself warns that a plotted insertion burn to a celestial with an SOI is unsupported ("A Transfer-to-Moon maneuver needs to be written"), so flight 1's second node was an unsupported artifact regardless.

## B15 / B16 Eve interplanetary FLYBY + ORBIT missions - the first INWARD transfer, and the first capture after a heliocentric traverse (b15_eve_flyby / b16_eve_orbit) [B15 LIVE-PROVEN 2026-07-26 after SEVEN flights; B16 unblocked but NOT YET FLOWN - branch `autotest-eve-missions`, stacked on `autotest-landing-missions`]

**"ZERO NEW MACHINE CODE" WAS THE SHIPPING CLAIM AND FLIGHTS 1-3 REFUTED IT.**
The lane shipped as B7's exact param key set re-valued for Eve (B16 = that set
unioned with B11/B12's capture tail), with `mlib.py` and `mission_runner.py`
untouched, and `EveLaneIsAParameterChangeTests` machine-checking the subset
claim. Two of the three first-flight failures really were parameter defects. The
third was not, and the honest revision is recorded here rather than quietly
dropped:

1. **`ejectionEccFloor` 1.05 -> 1.001 (PARAMETER).** Copied from B7/Duna, which
   is an OUTWARD calibration. Flight 1 flew the ejection CORRECTLY - node
   planned, executor burned it (lf 998.455 -> 595.016), node consumed, a genuine
   hyperbolic Kerbin escape at `ap=-545,897,712 m ecc=1.004` - and an inward
   transfer needs less C3, so 1.004 is right and 1.05 is not.
2. **`viaBodyNames` `["Sun"]` -> `["Sun", "Mun"]` (PARAMETER).** An inward
   ejection burns at a different point and direction than an outward one, and
   this window's escape path crosses the Mun's SOI. B7 never does.
3. **The park round-out (MACHINE).** MechJeb's interplanetary ejection planner
   is only correct from a ROUND parking orbit, and nothing in the shared machine
   could produce one - `CIRCULARIZE` only WAITED on a periapsis window, it never
   ACTED. See the dedicated block below.

So the lane now carries ONE new param key (`parkTrimEccMax`) and three machine
changes, all param-gated so B5/B6/B7/B11-B14 are byte-identical.
`EveLaneIsAParameterChangeTests` was REWRITTEN, not deleted - deleting it would
have retired the guard at the moment it did its job. It now pins that B15 adds
EXACTLY ONE argued key, so key number two still fails a test.

**THE ROOT CAUSE, because it is a MechJeb property worth knowing suite-wide.**
Decompiled from the INSTALLED harness binary -
`automation/stock-minimal/GameData/MechJeb2/Plugins/MechJeb2.dll`, file version
2.15.1.0, the pin in `harness/provision/pins.toml`. NOT from the `mods/MechJeb2`
source checkout: that tracks a later refactor which no longer carries this method
under this name, so "decompiled MechJeb 2.15.1" is only resolvable against the
DLL.
`OrbitalManeuverCalculator.DeltaVAndTimeForInterplanetaryTransferEjection`
computes the required post-burn speed as
`sqrt(2 * (soiExitEnergy + mu / o.semiMajorAxis))` - at the parking orbit's
SEMI-MAJOR AXIS - then applies it at a `burnUT` its own ejection geometry places
at whatever true anomaly points the escape asymptote correctly. Circular park:
`r == sma`, exact. Eccentric park:
`v_inf^2 = v_ideal^2 - 2mu/SOI + 2mu/sma - 2mu/r_burn`, and near escape velocity
that radius error eats the C3 budget. MechJeb warns about park eccentricity
itself, but only above 0.2.

MEASURED, and only measured numbers here - an earlier draft quoted a 769.6 m/s
"correct ejection" that appears in no archive and does not reproduce. Flight 3
planned from a 562.354 x 778.184 km park (ecc 0.08495) and MechJeb priced the
ejection at **652.843 m/s**; flight 5, same planner and same window class but
from a ROUND 778 km park, priced the SAME ejection at **775.873 m/s**. The
**123.0 m/s** shortfall is the defect. The resulting heliocentric orbit was
pe 12,389,067,761 x ap 13,615,196,295 m against Eve's 9,734,357,699 -
9,931,011,389 m: the craft's PERIHELION sat 2.46e9 m above Eve's APHELION, the
two orbits never intersect, and `nextBody` read Eve zero times in 632 polls
because no encounter was geometrically possible. **This is not a coast-budget
problem and raising `coastTimeoutSeconds` would only have bought a longer route
to the same answer.**

DERIVED, with its assumption stated because `r_burn` is not recorded (the burn is
taken at PERIAPSIS, where the radius error is largest, so the achieved figure is
a lower bound): a Kerbin -> Eve Hohmann needs 778.997 m/s of SOI-EXIT velocity =
261,454 J/kg of post-burn specific energy = 723.1 m/s of v_inf at infinity. From
a round park at flight 3's apoapsis radius the formula prices that at
775.75 m/s, which matches flight 5's MEASURED 775.873 to 0.12 m/s - the check
that the model is right. Flight 3's 652.843 m/s at its periapsis leaves
8,310 J/kg: v_inf 128.9 where 723.1 was wanted, or SOI-exit 317.1 where 779.0
was wanted. Quote those as a PAIR in one convention or not at all; the original
"779 into 129" line mixed an SOI-exit number with an at-infinity one, which is
why it could not be reproduced.

**Three machine changes, each with its own reason:**
- **`parkTrimEccMax` (new key, B15/B16 only).** `CIRCULARIZE`, once the
  periapsis window is met, plans and burns a MechJeb circularize-at-APOAPSIS
  node until the observed eccentricity is at/below it, bounded at
  `PARK_TRIM_MAX_ATTEMPTS` with a named give-up. Apoapsis so the trim can only
  RAISE the park, preserving the factor-7 warp-legality argument for the
  ejection-window wait. `circularizeTimeoutSeconds` 6000 -> 12000 to cover it.
  **Flight 4 found a bug in the first shape of this and it is worth reading:**
  the verdict tested eccentricity FIRST, and a circularize burn sweeps the orbit
  continuously down through the window, so the gate fired MID-BURN
  (0.085 -> 0.044 -> under 0.02 across three polls), `CIRCULARIZE` exited with a
  live node, and `TRANSFER-BURN`'s `execute_all_nodes` re-burned it - leaving
  778 x 1,014 km, WORSE than the park the trim exists to fix. The rule is now
  node-first: **a pending node means the orbit is still being written, so do not
  read it.**
- **BOTH correction-round triggers' body domain** narrowed from every via body
  to the transfer-parent SOI (`_b5_correction_via_bodies`). Widening
  `viaBodyNames` for COAST legality silently widened the trigger domain too.
  This took two flights to close because there are TWO triggers and the first
  fix only caught one:
  - the debounced NO-ENCOUNTER early trigger (flight 3: both rounds spent
    inside the MUN's SOI on a flyby hyperbola, MechJeb pricing the correction at
    1464.1 m/s against the 200 m/s cap, both discarded); and
  - the primary TIME-MODE trigger `_b5_correction_round_ready`, which flight 5
    showed is the sharper of the two. `time_to_soi` is the clock to ANY SOI
    change, not to the TARGET's, so inside the Mun's SOI it reads the MUN-EXIT
    time - a few thousand seconds, trivially under round 0's 20,000,000 s
    threshold. B7/Duna never transits a moon on its way out, so for three green
    Duna flights that clock WAS the Sun -> Duna transition and the distinction
    never showed.
  Both are strictly narrowing and provably identical for B5/B6/B7 (return_body
  "Sun", via_bodies ("Sun",) - the same set).
- **The `TRANSFER-BURN` under-burn give-up is now NAMED.** It surfaced as
  "phase TRANSFER-BURN timed out" with only ~66% of the budget spent, which sent
  flight 1's investigation at the BUDGET when the burn-stagnation watchdog was
  what fired. Same class as the CORRECTION-BURN root cause (`mlib.py` ~926); it
  now names the watchdog and quotes the evidence that failed, choosing the
  ejection-eccentricity floor over the apoapsis floor on interplanetary lanes
  (an escape burn drives the home-frame apoapsis negative, so quoting it is
  actively misleading).

**The lane is no longer blind at plan time.** `mission_runner`'s interplanetary
plan action now reads the plan's OWN patched-conic chain (`Node.Orbit` ->
`Orbit.NextOrbit`) and logs a `reachesTargetOrbit=` verdict from the pure
`mlib.classify_transfer_reach` - an ANNULUS OVERLAP widened by the target's SOI
radius, deliberately direction-free, so one expression reads correctly for B7's
outward transfer and B15's inward one. A mis-aimed transfer is now named at
ut ~2,500 instead of ~11.8 million. Two things it learned the hard way:
- The nodes it inspects are the ones `make_nodes()` RETURNED, never
  `control.nodes[0]` - flight 4 proved a leftover node makes the latter report
  an entirely different burn (it dutifully described a 69.5 m/s circularization
  that never leaves Kerbin).
- The SOI term is not a refinement. Flight 5's corrected leg came out 6.96e6 m
  from Eve's orbit, which is 0.082 of Eve's 85.1e6 m SOI radius - an intercept.
  A bare band comparison labels that `beyond`, the SAME word it gives flight 3's
  2.46e9 m (28.9 SOI radii), and a reader trusting the word would go hunting for
  a defect in a transfer that is fine. Encounter means "enters the SOI", not
  "crosses the osculating orbit". An unreadable SOI degrades to 0.0, i.e. the
  strict band test, which can only be harsher than the truth, never softer.

**MEASURED, flight 5 (2026-07-26), the first flight with the fix:**

| | flight 3 (broken) | flight 5 (park trim in) |
|---|---|---|
| park at plan time | ecc 0.08495, 562 x 778 km | ecc 0.000, 778 x 778 km |
| ejection node | 652.843 m/s | 775.873 m/s (DERIVED requirement 775.75) |
| PLANNED heliocentric perihelion | 12,389,067,761 m | 9,937,970,000 m |
| ACHIEVED heliocentric perihelion | 12,389,067,761 m | 9,934,918,205 m |
| gap to Eve's orbit (achieved) | 2.46e9 m (28.9 SOI radii) | 3.91e6 m (0.046 SOI radii) |

The trim took ONE attempt and ~1,016 game seconds of its 12,000 budget. The
ejection conic chain also makes the flight-2 via-body finding visible AT PLAN
TIME rather than as a coast ASSERT-FAIL: `Kerbin -> Mun -> Kerbin -> Sun`.

**Flight 5 still red, and the distinction matters.** Its transfer GEOMETRY was
essentially perfect - the achieved heliocentric perihelion sat 0.046 Eve SOI
radii from Eve's orbit, a 629x improvement on flight 3 - but `nextBody` still
never read Eve, because crossing the target's orbit is not the same as arriving
when the target is there. That is a PHASE error, and phase is exactly what the
correction rounds exist to fix; both had been spent inside the Mun's SOI by the
time-mode trigger defect above. So flight 5 is the flight that separated the two
failures the first three flights had conflated: **the ejection was wrong (fixed
by the trim), and the corrections fire in the wrong place (fixed by the trigger
narrowing).**

**Flight 6: corrections finally in the right place, and the last number
measured.** With the trigger narrowed, both rounds fired on the heliocentric leg
(alt ~13.3e9 m in Sun SOI). MechJeb priced the fix at 378.5 / 378.3 m/s -
deterministic across rounds and across flights - and the 200 m/s cap discarded
both, so the coast again flew the raw intercept. That cap was calibrated on MOON
transfers (B5/B6 corrections are tens of m/s) and carried to B7 unchanged; it is
not a physical bound. The correction is fixing an ARRIVAL PHASE error caused by
MechJeb systematically under-delivering v_inf (`DeltaVAndTimeForInterplanetary
TransferEjection` treats the heliocentric dv as the velocity AT the SOI boundary
rather than at infinity), and fixing timing mid-coast is intrinsically dearer
than fixing it at the ejection. Flight 6 also MEASURED the affordability:
lf 527.912 at Sun-SOI entry is ~1,944 m/s remaining, so 378.5 leaves ~1,566.
`maxCorrectionDvMps` 200 -> 450 on both Eve lanes.

**FLIGHT 7: GREEN.** Full scenario PASS on attempt 1 - every verifier
PASS/SKIPPED, analyzer red=0, expectations 0 mismatches.

| | flights 1-3 | flight 7 |
|---|---|---|
| `nextBody == Eve` | 0 of 632 polls | **86** |
| phases | died in COAST-TO-TARGET | full chain through TARGET-FLYBY -> RETURN |
| `reachedTargetSoi` | never | **Eve** |
| `flybyPeriapsisFloor` | null | **22,032,532 m** (floor 100,000) |
| `returnedToHome` | never | **Sun** |

Both correction rounds flew: 378.32 m/s on the heliocentric leg, then a 3.35 m/s
fine-tune - which is exactly the shape the two-round design predicted, a big
early re-aim and a small late trim. PINNED from this run: recordings count
{8, 8} (was PROVISIONAL {7, 9}, expectation was exactly 8), mission wall 1,236 s,
scenario wall 1,286 s, and the ejection-window wait at **11,827,993 game s** -
0.80 of the derived 14,700,000 Kerbin-Eve synodic, which is why this lane spends
most of its wall clock in one warp.

**BE SKEPTICAL ABOUT THE VALUE. The specs are, and this entry is.** A plain Eve
flyby largely RE-EXERCISES B7's cross-SOI surface at a different body, and an Eve
capture RE-EXERCISES B11/B12's commit-in-foreign-SOI at a different body. Both are
D14 MULTIPLIERS, not new mechanisms, and NEITHER LANE CLAIMS A NEW REGISTRY VALUE
(D14 already carries `eve`; D1 already carries `commit-in-foreign-soi`), so the
growth rule is not triggered. What they actually buy:
- B15: a STABLE interplanetary regression subject. B7 is currently FLAKY - Ike
  grabs its 300 km Duna approach on roughly half of sweeps - so the only
  interplanetary lane in the suite cannot be trusted as a regression signal.
- B16: the capture tail run after a HELIOCENTRIC traverse. It has only ever run
  after a LUNAR transfer; here the committed tree's terminal orbit body is
  reached through TWO SOI transitions with an arrival v_inf ~4x the Mun's.

**THE SURFACE EVE COULD HAVE BOUGHT, AND DELIBERATELY DOES NOT - do not let a
later reader think this was an oversight.** Eve has the deepest atmosphere in the
stock system (90 km) and is the only non-Kerbin body the Kerbal X can reach that
has one, so a NON-KERBIN ATMOSPHERIC TrackSection is genuinely unclaimed coverage
(B13/B14 bought only the AIRLESS `Approach -> Surface*` path). Three stacked
reasons it is not claimed here, in increasing order of how hard they are to fix:
1. GEOMETRY. B15 passes at 1,000 km and B16 parks at ~5,000 km - 11x and 55x the
   atmosphere depth. Aiming lower is not a tweak: at Eve's arrival speeds an
   atmosphere-grazing periapsis is an aerocapture / breakup event.
2. RECORDING GATE. `FlightRecorder.OnPhysicsFrame` early-returns on `isOnRails`
   and `BackgroundRecorder.OnBackgroundPhysicsFrame` on `bgVessel.packed`. The
   whole in-SOI leg is packed under rails warp, so even a grazing pass emits no
   Atmospheric section unless the craft is loaded and OFF RAILS in the air.
3. OBSERVABILITY, the blocker that outlives the other two. **NO EMITTED LOG LINE
   PAIRS AN ENVIRONMENT CLASS WITH A BODY NAME.** The two candidates are
   `TrackSection started: env=Atmospheric ...` (`FlightRecorder.cs`) and
   `Environment transition: X -> Y at UT=...`
   (`EnvironmentDetector.EnvironmentHysteresis.Update`); neither carries a body,
   and since every Eve mission launches from Kerbin, both are satisfied by the
   ASCENT alone. A logContract asserting them would be unearned BY CONSTRUCTION.
   B13/B14's `Approach -> Surface*` trick cannot be cloned either - it works only
   because a vacuum class immediately before a surface class is impossible on an
   atmospheric body, and on Eve the pre-touchdown class is necessarily
   Atmospheric, which reads identically to a Kerbin landing.
   FOLLOW-UP VARIANT, if the atmospheric cell is wanted: add `body=` to the
   `TrackSection started:` format string (the recorder has `v.mainBody.name` in
   hand at the call site), mirroring the B13 `terminalOrbitBody` fix, then a
   rebuilt DLL, then `provision.py --profile stock-minimal`, then an aerobraking
   profile that is loaded and off rails through the pass. Different mission,
   different risk profile. `EnvironmentDetector.Classify` itself is fully
   body-generic (one line: `hasAtmosphere && altitude < atmosphereDepth`, with
   NO hysteresis into or out of the vacuum classes), so the classifier is not the
   problem - the logging is. NOTE also that the B13/B14 specs attribute that
   token to `EnvironmentDetector.ConfirmTransition`, a method that does not
   exist; the real emitter is `EnvironmentHysteresis.Update`. File is right,
   method name is stale.

**B16 FEASIBILITY - DERIVED, and it CLOSES with room.** This was the open
question, since Eve is far more massive than the Mun or Minmus.
- MEASURED (archived end-of-run saves of the three green B7 flights,
  `logs/2026-07-25_{0753,0806,1216}_B7-duna-flyby/saves/b2-lko-craft/persistent.sfs`):
  after a full 700 km ascent + interplanetary ejection + 2-3 corrections the
  17-part orbiter holds LF = 494.560 / 496.959 / 495.479 of the X200-16's 720.
- DERIVED from that with stock part cfgs + the rocket equation (orbiter dry
  ~7.7 t, 11.111 kg propellant per LF unit at the stock 9:11 ratio, Poodle
  Isp 350 s): **~1,825-1,880 m/s remaining at an interplanetary arrival.**
- CROSS-CHECKED: the same model turns the supplied lf 592.814 at the Mun park
  into ~2,101 m/s and a full tank into ~2,422 m/s, so the Mun's post-TLI tail
  spent ~321 m/s against B11's MEASURED 277.016 m/s capture node plus small
  corrections. It reproduces an independently measured burn to within tens of
  m/s.
- DERIVED Eve capture: `dv(r) = sqrt(v_inf^2 + 2mu/r) - sqrt(mu/r)`, minimised at
  `r = 2mu/v_inf^2` with `dv_min = v_inf/sqrt(2)`. Hohmann arrival v_inf is
  846 m/s, calibrated to ~931 m/s by the 1.10x factor B7's three MEASURED Duna
  arrival hyperbolas (sma -364,416 / -364,568 / -364,454) show against ideal.
  That gives **~735 m/s at the chosen 5,000 km park**, 688 at 8,000 km, and a
  658 m/s floor at 18,157 km. **A 2.5x margin**, re-checked against the raised
  cap (`maxCorrectionDvMps` 200 -> 450): ~2.0x after B15's MEASURED 378.5 m/s
  correction, and still ~1.3x in the pathological case of BOTH rounds landing on
  the 450 ceiling. It closes on the measured numbers and stays positive on the
  worst case, which is the number B16's first flight must re-read.
- **THE COMMITTED SURVEY IS PESSIMISTIC AND NOW WE KNOW WHY.** This doc's B6/B7
  section says the orbiter holds "~1500-1600 m/s after the 80 km
  circularization". That predates B5 finding 15, so it did not know the flameout
  watchdog lets the CORE fly the whole transfer, leaving the upper stage nearly
  full at the target. Both readings agree Eve closes; the MEASURED fuel states
  settle the size of the margin. The survey line is left as written - it is
  historically accurate about what was known then - but do not size a new lane
  from it without checking a measured `lf` first.

**GILLY IS NOT IKE, and the numbers say so.** B7's Duna lane is intermittent
because its ~300 km periapsis target makes the inbound leg transit Ike's shell.
Eve's one moon does not reproduce it, on four independent counts:
1. Gilly's SOI radius is ~126 km against Ike's ~1,050 km - 8.3x smaller in
   radius, ~69x smaller in cross-section.
2. Ike sits at 6.7% of Duna's SOI radius, DEEP in the funnel every low-periapsis
   arrival passes through. Gilly sits at 17-57% of Eve's (sma 31,500 km,
   e = 0.55, inside an 85,109 km SOI), far out where the inbound cone is wide.
3. Ike's inclination is 0.2 deg - coplanar with everything that arrives. Gilly's
   is 12 deg.
4. Unlike Ike, Gilly cannot be dodged by choosing a periapsis (it is at
   14,175 km minimum), so the spec does not try - it accepts the transit.
RESIDUAL RISK IS NON-ZERO, NOT ZERO. A Gilly capture is a NAMED ASSERT-FAIL
(`flyby ejected the craft off-course: body='Gilly'`), and Gilly is DELIBERATELY
absent from `viaBodyNames` - adding it would convert that into a silent PASS.
`retry.policy = "once"` absorbs one occurrence. Two unit cells pin the choice.
Gilly DOES set B16's park ceiling: the dv optimum (18,157 km) sits inside Gilly's
14,175 km periapsis shell, so `parkMaxApoapsisMeters` 13,000 km does double duty
as the captured-or-not evidence AND the Gilly exclusion, and the park is high for
that reason rather than for a fuel reason.

**THE INWARD TRANSFER IS THE HIGHEST-RISK UNKNOWN, and it is not ours.** Every
interplanetary case so far goes OUTWARD. AUDITED: mlib has no direction anywhere
- `_b5_transfer_burn_done` reads |ecc| >= floor in the HOME frame (an inward
escape is exactly as hyperbolic), `_b5_coast_bodies` / `_b5_warp_bodies` /
`_b5_return_body` are name comparisons, `_b5_correction_round_ready` in time mode
is a scalar clock, and `STOCK_WARP_ALTITUDE_LIMITS` already carries Eve (its
table is identical to Kerbin's) with the Sun's factor-7 limit five orders of
magnitude below Eve's heliocentric altitude. The assumption, if any, lives in
MechJeb's own `OperationInterplanetaryTransfer` (WaitForPhaseAngle), which NO
headless test can exercise (the standing PENDING-OPERATOR traceability note). If
it cannot plan an inner window it throws server-side, `node_count` stays 0, and
PLAN-TRANSFER flakes on the PLAN_MAX_ATTEMPTS give-up in ~90 game-seconds with a
named reason. **FLY B15 FIRST**: it prices this unknown and the ejection-window
wait for half of B16's cost.

**WALL COST, stated plainly because it is now a real constraint.** Suite p50 is
~219 minutes (`harness/coverage/duration.json`). Budgets are 3000 s / 4200 s WALL,
DERIVED from B7's MEASURED 767-779 s mission wall over 11.30e6 game seconds, its
MEASURED phase spans (ejection wait ~4,966,290 game s; heliocentric coast
~6,189,000), the DERIVED ~24,000 game-s-per-wall-s warped throughput, and the
DERIVED Eve game-time load. Expected actuals are ~800-900 s (B15) and
~1,900-2,200 s (B16), so the pair adds roughly 45-50 minutes to a sweep, about
+21%, and B16 would become the second most expensive scenario after B13's
MEASURED 2,825 s. IF THAT IS NOT WORTH THE ONE SURFACE B16 BUYS, the honest move
is to ship B15 alone and drop B16 - NOT to shrink the budgets.

PENDING-OPERATOR first-flight pins, each naming what closes it:
- `recordings.count` ships PROVISIONAL at {7, 9} on BOTH lanes. The expectation is
  EXACTLY 8 (B11/B12/B13/B14 and all three green B7 runs measured 8; 10 of 10
  archived foreign-SOI runs at or after commit 82398e157 read 8). It is a window
  and not a pin because `_b5_flameout_stage` is a CONDITIONAL watchdog and B15/B16
  fly a different burn profile at a different vehicle mass. Pin to {n, n} from the
  first green flight's `verifiers.expectations.observed.recordings.count`.
- Both wall budgets on both lanes, and the real ejection-window wait (the only
  budget term with a 14,700,000 game-second range; the fixture UT fixes it, so it
  is deterministic but unknown until flown).
- B16's `captureBurnTimeoutSeconds` 400,000 (DERIVED ~74,700 game s of
  SOI-edge-to-periapsis coast via hyperbolic Kepler onto the 5,700 km park
  radius, ~79,700 at the ideal-Hohmann v_inf; Eve's SOI edge is 38x the Mun's).
- The achieved capture geometry, which decides whether `courseCorrectPeriapsisMeters`
  5,000,000 lands where intended.

**B16 IS `tier = "operator"` UNTIL ITS FIRST GREEN FLIGHT, AND THAT IS A
DELIBERATE SCHEDULING DECISION, not a fixture gap.** It is the most expensive
scenario in the suite (`budgetSeconds` 4700) with `retry.policy = "once"`, so a
nightly rotation would spend up to ~2.6 hours on it EVERY night, and a SYSTEMATIC
first-flight failure - which is what all six pre-green B15 attempts were - reds
both attempts identically and reds the whole sweep nightly until someone flies
it. `operator` is in no cadence set, so B16 runs only on an explicit
`--tier operator` / `--id B16-eve-orbit`, which is how a first flight with
PROVISIONAL pins should be run anyway. **PROMOTE IT** to `nightly` (in the spec
AND in the `docs/dev/autotest-status.md` row) the moment it flies green and the
pins above are MEASURED.

**REVIEW FOLLOW-UPS APPLIED (2026-07-26), each one worth knowing on its own:**
- **The plan diagnostic sat inside `make_nodes()`'s `try`.** It is the FIFTH
  shared change on the interplanetary plan action and the only one that is NOT
  param-gated - it runs on B7's plans too. Inert by construction (read-only kRPC
  reads; the machine keys on `node_count`, which a log line cannot change), but
  a raise from its own unguarded tail would have been reported as
  `operation_interplanetary_transfer.make_nodes failed` - a false plan-FAILURE
  message on a plan that SUCCEEDED, i.e. exactly the misleading-message class
  this lane exists to remove. Now in its own `try` on the `else:` of the plan,
  with four cells pinning that a raising diagnostic costs the log line and
  nothing else.
- **The correction-approach WARP branch still read the raw `via_bodies`.** Both
  correction TRIGGERS were narrowed to the transfer-parent SOI; the warp branch
  was not, so on the Eve lanes a craft in the Mun's SOI entered it and computed
  `dt = 3,086 - 20,000,000`, failed `dt > soi_lead`, and fell to the floor-2
  rails stair. MEASURED on flight 7: **317 of 318 Mun-SOI frames at RAILSx10**,
  ~3,086 game s spread over ~308 wall s of a 1,236 s mission. Matched to the
  trigger domain, the transit takes the native warp-to-SOI-boundary branch.
  Provably a no-op off the Eve lanes (B7's via list IS its correction domain;
  the moon lanes have no time-mode triggers and never reach the branch).
- **`_b5_correction_via_bodies`' "strict subset" claim is a PRECONDITION, not an
  invariant.** It holds only while `return_body` is a member of `via_bodies`,
  which no code enforces. A future lane with `returnBodyName` outside
  `viaBodyNames` would make the narrowing ADD a firing opportunity. Now pinned
  by a cell over every interplanetary spec, and the docstring says the claim
  depends on it.
- **Two quoted "measurements" did not reproduce.** The 769.6 m/s "correct
  ejection" is in no archive at all; the 129 m/s achieved v_inf is derivable but
  only under a burn-at-periapsis assumption, and it was being compared against
  779 m/s, which is an SOI-EXIT figure rather than an at-infinity one. Both are
  now replaced by the MEASURED pair (652.843 vs 775.873 m/s of planned ejection,
  a 123.0 m/s shortfall) with the derivation and its assumption spelled out
  wherever a v_inf number still appears.
- **The MechJeb citation is now resolvable.** "Decompiled MechJeb 2.15.1" could
  not be checked by the next reader: the `mods/MechJeb2` checkout tracks a later
  refactor that no longer carries
  `DeltaVAndTimeForInterplanetaryTransferEjection` under that name. Every site
  now cites the INSTALLED binary,
  `automation/stock-minimal/GameData/MechJeb2/Plugins/MechJeb2.dll`, file
  version 2.15.1.0.
- **The test-local `*_PARAMS` dicts were not checked against the specs they
  claim to mirror**, and the lane's central claim
  (`set(B15_PARAMS) - set(B7_PARAMS)`) is computed over them. `B11_PARAMS` was
  missing SEVEN keys the B11 spec carries and `B7_PARAMS` carried FIVE stale
  values. All fixed, and `MissionParamsMatchTheSpecsTests` now diffs all nine
  dicts against their TOMLs on both key set and value, with an EXACT-MATCH table
  of the two remaining deliberate fixture divergences (a fixed divergence fails
  the test too, so the table cannot rot).

## B13 / B14 Mun + Minmus LANDING missions - powered descent, landed dwell, commit-on-the-surface (b13_mun_landing / b14_minmus_landing) [BUILT, NOT YET FLOWN - branch `autotest-landing-missions`, stacked on `autotest-orbit-missions`]
## B13 / B14 Mun + Minmus LANDING missions - powered descent, landed dwell, commit-on-the-surface (b13_mun_landing / b14_minmus_landing) [LANE CLOSED 2026-07-25: BOTH AXES LIVE-PROVEN, FULL PASS on flight 1 each; branch `autotest-landing-missions`, stacked on `autotest-orbit-missions`]

Roadmap item 3 ("Mun/Minmus LANDING missions - upper stage landed: landed-on-other-body recording, surface TrackSections off Kerbin, the landing FSM seam"). **ID note:** same reasoning as B11/B12 - B8/B9/B10 are taken in `automated-testing-scenario-catalog.md` section 2 and B3 is the EVA branch, so the LANDING lane takes the next free ids, **B13-mun-landing** + **B14-minmus-landing**.

**Why it exists (the Parsek surface, not the rocketry).** Nothing in the suite produces a recording that ENDS **LANDED ON ANOTHER BODY**. B11/B12 end parked in ORBIT around a foreign body; B1/B4 land on KERBIN. Four surfaces this lane reaches and nothing else does:
- terminal classification `Landed` for a tree that closes on foreign soil;
- SURFACE-class TrackSections OFF Kerbin - the environment classifier's AIRLESS path. `EnvironmentDetector.Classify` returns `Atmospheric` for anything below `atmosphereDepth`, so on Kerbin a descent ALWAYS enters the surface classes through `Atmospheric`; an `Approach -> Surface*` (or `Exo* -> Surface*`) transition is only possible on an airless body, which is exactly what the required log token asserts;
- landing-leg part events (3x `landingLeg1-2` on the upper stage, extended by MechJeb below 1 km AGL). The recording carries them, but NOTHING IN THE HARNESS READS PART EVENTS, so `D7 gear` is deliberately NOT claimed - the same discipline that removed `D5 bg-recording` from B11/B12;
- the landed-vessel ghost / playback surface the committed recording then carries.

New registry cells (added to `harness/coverage/registry.toml` in the same change, per the growth rule): **D1 `commit-landed-foreign-body`**, **D4 `surface-stationary`** (claimed by B13) and **D4 `surface-mobile`** (claimed by B14). No committed scenario claimed either D4 value before this lane, and each spec claims the class its OWN flight measured, gated by a class-specific log token so neither can be satisfied by the other's reading.

**Reuse, not reinvention.** Both missions are the LIVE-PROVEN `mlib.b5_decide` machine with ONE new param, `landingEnabled`, added on top of `captureEnabled`. PRELAUNCH through PARK is byte-identical to the five B11 flights and the six B12 flights. `landingEnabled` implies `captureEnabled` BY CONSTRUCTION: the only door into DESCENT is the capture lane's PARK dwell, so the flag alone is inert. B14 is a thin alias over the same machine, exactly as B12 is to B11 and B6 to B5.

**The new four-phase tail** (each phase names its vehicle configuration in the spec's MISSION PROFILE header):
- **DESCENT** - MechJeb `LandingAutopilot.LandUntargeted`. Its untargeted path (decompiled `MuMech.Landing.UntargetedDeorbit`) points retrograde-horizontal, burns at full throttle until `PeA < -0.1 * bodyRadius`, then hands to `FinalDescent`, which decelerates on its descent-speed policy and extends the gear below 1 km AGL. Configuration: `DeployGears` TRUE (3 real legs), `DeployChutes` **FALSE** (Mun and Minmus are AIRLESS - a parachute cannot deploy in vacuum and arming one would be a lie in the config), `RcsAdjustment` FALSE (the Kerbal X upper stage has no RCS thruster blocks). The phase is **warp-PASSIVE**: every MechJeb landing state calls `Core.Warp.WarpRegularAtRate` / `WarpToUT` / `MinimumWarp` gated on `Core.Node.Autowarp` - the NodeExecutor's SHARED GLOBAL flag - so the runner sets that flag EXPLICITLY at engage (the B-DOCK flight-12 lesson) and the machine issues no warp of its own. Two warp writers in one phase is the cancel/re-arm thrash that cost B12 flight 2 its whole wall budget.
- **LANDED-SETTLE** - rails DROPPED to 1x, throttle CUT, the autopilot RELEASED (`StopLanding`, idempotent - MechJeb stops its own module on the touchdown frame, but the dwell must not run with a module still holding the thrust/attitude user pools), SAS held, then `landedDebounceFrames` settled frames HELD across `landedDwellSeconds`. The settled gate is target body + landed situation + BOTH speed components under their floors. The dwell IS the recorded surface coverage, which is why it is deliberately not warped.
- **SURFACE-COMMIT -> SURFACE-COMMITTED** - the SAME route-1 mid-mission command-seam `CommitTree` B11/B12 fire (`ACTION_PARSEK_COMMIT_TREE`), fired while LANDED. OK is the terminal; ERROR/TIMEOUT flake.

**Liveness (budgets bound SLOW, watchdogs bound BROKEN; every give-up gets a DISTINCT NAME).** DESCENT's actor is a MechJeb module we do not own, so it carries FOUR named fast-fails on top of its GAME budget:
- `landing-autopilot-not-enabled` - **COMMANDED-vs-OBSERVED, treated as the DEFAULT failure mode rather than an edge case.** This lane's three sibling defects were all this shape (the CAPTURE-BURN NodeExecutor that was commanded and never verified; B1's chute, whose DOWN terminal checked the COMMANDED arm latch on a flight where the canopy never opened; EVA-4's ladder release). `LandingAutopilot.Enabled` IS readable - verified against the INSTALLED pin, `automation/stock-minimal/GameData/kRPC/KRPC.MechJeb.json` exports `LandingAutopilot_get_Enabled` / `_get_Status` alongside `LandUntargeted` / `StopLanding` / the four settings - so no derived proxy is needed for "did it engage". A debounced OBSERVED FALSE before touchdown re-issues the engage, bounded at `MAX_LANDING_AP_REISSUES`, then fast-fails in ~3 polls. **The touchdown hazard is real and the guarantee is in the ORDERING, not in the classifier** (wording corrected 2026-07-26): MechJeb's `FinalDescent` calls `StopLanding()` (clearing the module's user pool, i.e. disabling it) the frame it observes `Vessel.LandedOrSplashed`, so an observed FALSE after touchdown is the module reporting SUCCESS and reading it as a dead autopilot would fast-fail a PERFECT landing. `b5_decide`'s DESCENT block tests `_b5_touched_down` FIRST and leaves the phase, so `classify_landing_autopilot` is only ever called with `touched_down=False` in flight; its own touchdown conjunct is an ORDER-INDEPENDENT BACKSTOP for callers that do not own that ordering, deliberately kept (one comparison, and deleting it would move a load-bearing safety property into a call-site ordering constraint documented only in a comment) but NOT a live path. Cells: `LandingDescentTests.test_a_landed_frame_exits_whatever_the_autopilot_reads` pins the live ordering (seeded one frame short of DEAD, so the supervisor running first would fast-fail that exact frame); `LandingAutopilotClassifierTests.test_touchdown_reads_as_success_for_an_out_of_order_caller` pins the backstop. The earlier "required, not defensive" wording described the hazard correctly and the implementation incorrectly.
- `landing-no-progress` - the SECOND, independent channel, because an autopilot holding a useless attitude reads ENABLED forever. Surface altitude must shed `landingProgressMinDropMeters` within `landingProgressWindowSeconds` of GAME time, measured from a rolling ANCHOR (a healthy descent re-anchors every window and never runs one out). A non-finite altitude gets its OWN name, `altitude-unreadable`, because the operator response is "fix the channel", not "fix the trajectory".
- `landing-touchdown-timeout` - `descentTimeoutSeconds`. A GAME-time budget IS the right instrument here, unlike CORRECTION-BURN's (which the aim-then-warp it bounded spent for itself): the deorbit-to-touchdown fall is a BALLISTIC duration set by orbital mechanics, and warp changes only how much WALL time it costs. Sized at 3000 so the NAMED give-up fires BEFORE the generic mission wall reaper even at 1:1.
- `landing-vessel-lost` - a CRASH. Distinctly named so a lithobraked lander can never read as a generic timeout or, worse, as a success.

LANDED-SETTLE adds `landed-never-stable`, which (like PARK) distinguishes "never settled after touchdown" from "settled but not in-gate at the end of the dwell".

**Assertions (eight rows).** The four B11 rows through PARK are inherited, with `parkedStable` re-pointed from ORBIT-COMMIT to DESCENT (the ORBIT terminal is never entered in landing mode), then `landedOnTargetBody` (OBSERVED touchdown situation AND OBSERVED body, both frozen off the touchdown frame), `landedStable` (SURFACE-COMMIT entered and the settled gate was ever met) and `treeCommitted` (SURFACE-COMMITTED and the seam answered OK). **The EVA-4 lesson applied:** a crash terminates on `loss_reason` BEFORE the assertions are consulted, a never-landed run leaves three rows unmet, a landing on the wrong body fails the body conjunct, and a landed-but-tumbling run never enters SURFACE-COMMIT - so there is no path on which all eight rows are met and the craft is not sitting intact, settled, on the target body with its tree committed.

**FIVE shared-machine touches** (the lane originally claimed TWO; a review counted five, and the three unmentioned ones are additive-only). The two BEHAVIOURAL touches are landing-only by construction and each is pinned by its own unit cell in `LandingDefaultsPreserveOrbitAndFlybyTests`:
1. `_B5_IN_TARGET_SOI_PHASES` deliberately EXCLUDES the landing tail, so `flybyPeriapsisFloor` still certifies the arrival pass and the PARKED orbit and stops there. Including the descent would make the lane's own objective fail its own assertion - a landing drives the altitude to ~0 on purpose.
2. `_B5_FROZEN_EXEMPT_PHASES` exempts LANDED-SETTLE and SURFACE-COMMIT from the airborne frozen-telemetry vessel-lost detector. A settled rigidbody can report BIT-IDENTICAL `(altitude, vertical_speed, apoapsis, periapsis)` while UT ticks, which is precisely the dead-vessel signature; this is the ONLY scenario in the suite that polls a stationary craft for minutes (B1's DOWN and B4's SPLASHDOWN terminals both END the machine on the landed frame), so nothing else has ever been exposed to it. DESCENT is NOT exempt, and at most one frozen frame can slip through it because the touchdown handoff fires on the first observed landed situation against a limit of 10.

The other THREE are ADDITIVE and change no decision, but they DO change bytes other scenarios emit, so they are named rather than left to be discovered: (3) `mlib.snapshot_dict` emits three new keys (`landingApEnabled`, `landingApStatus`, `horizontalSpeed`) UNCONDITIONALLY, so every mission's snapshot dict grows - the values are the UNREAD sentinels (-1 / "" / NaN) everywhere `read_landing` is off, and nothing consumes them outside the landing machine. (4) `MACHINE_STATE_FIELDS` grows by 11 landing entries (12 after the 2026-07-26 `landingVspeedHolds` addition), so B5/B6/B7/B11/B12 machine-state lines and status files carry extra tokens; they render as the `-`/`0`/`False` defaults for a machine that never enters the landing tail. (5) `harness/status.py` gains a DESCENT branch AHEAD of the generic fallback, which changes the HEURISTIC TEXT B1 / B4 / EVA-4 show for their own suborbital DESCENT phase - the branch detects the landing machine fields via `machine_number` and falls through to the pre-existing wording when they are absent, so the change is the branch order, not the output, and `test_status.py` pins both arms.

**Fixture:** the already-committed `b2-lko-craft` (shared with B2/B4/B5/B6/B11/B12). No forge run, no new fixture, no separation phase - **the upper stage IS the lander**, verified part by part from `automation/stock-minimal/Ships/VAB/Kerbal X.craft`: `mk1-3pod`, `HeatShield2`, `parachuteLarge`, `Rockomax16.BW` (X200-16), `liquidEngine2-2.v2` (Poodle, 250 kN, Isp 350), 3x `landingLeg1-2`, 2x ladder, 2x solar panel. **Delta-v, MEASURED at PARK on the last green orbit flights rather than estimated:** Mun `lf=592.814` at a 141 km circular park (~2,100 m/s) against ~650 m/s needed; Minmus `lf=650.592` at a 38 km circular park (~2,250 m/s) against ~200 m/s needed. Margins are 3x and 10x, so NO delta-v assertion was added; the derivation is recorded in both spec headers so a future fuel-related failure reads as a REGRESSION rather than a known-tight budget.

**FLIGHT RESULTS (both axes, 2026-07-25).** B13-mun-landing FULL PASS attempt 1, wall 2,747.9 s (`harness/results/2026-07-25_1626_B13-mun-landing.json`); B14-minmus-landing FULL PASS attempt 1, wall 2,083.9 s (`harness/results/2026-07-25_1543_B14-minmus-landing.json`). Every verifier green on both, including the commit-terminal tokens `terminalState=Landed terminalOrbitBody=Mun` / `=Minmus` and the airless-only `Approach -> SurfaceStationary` / `Approach -> SurfaceMobile` environment transitions. MEASURED, and quoted here because the knobs below were closed from these numbers: DESCENT game span 1,353.9 s (B13) / 1,381.3 s (B14); touchdown vertical -0.279 / -0.253 m/s; touchdown horizontal 0.195 / 0.063 m/s; landed dwell 120.1 / 120.5 s; first no-progress window drop 69,622.5 m / 21,730.6 m (read off the periodic machine-state anchor lines, not the rate-limited telemetry stream - B14 has no telemetry sample at the PARK -> DESCENT frame the machine actually anchored on, which is where the earlier 21,749.8 m came from); `warp=NONEx1.000` on all 1,303 / 1,330 DESCENT telemetry samples (MechJeb never warped the descent under the installed 2.15.1 pin, so the warped-descent branch of the wall budget is fiction for this profile).

**POST-FLIGHT STATUS of the five first-flight pins - ALL FIVE CLOSED (four re-tuned or re-pinned from measured data, the fifth confirmed AS SIZED):**
1. ~~**`recordings.count` is PROVISIONAL at `{min 8, max 9}`**~~ **CLOSED - both windows PINNED at `{min 8, max 8}` from measurement.** Both flights read `verifiers.expectations.observed.recordings.count = 8`, so the one-slot allowance for a touchdown-shed fragment was never needed. The topology matches B11/B12 exactly - 1 root + 6 radial boosters + 1 flameout-staged ascent core - with ONLY the root's terminal changed, which is the discriminator this lane exists to move. The count stays COMMIT-BLIND (sidecars are written for the active tree too), so it guards recording TOPOLOGY while the log tokens guard the landed terminal.
2. ~~**`descentTimeoutSeconds` = 3000 is DERIVED, not measured**~~ **CLOSED - `descentTimeoutSeconds` trimmed 3000 -> 2200 on BOTH specs.** MEASURED spans 1,353.9 s (B13) / 1,381.3 s (B14) against the Kepler derivation of 1,367 s / 1,289 s, so the derivation was good to ~7%. 2200 leaves 1.59x margin on the worse of the two, which is the smallest margin this suite accepts on a single-sample measurement. The value is kept IDENTICAL across both bodies deliberately: the spans differ by 2%, and a knob that differs per body for no measured reason is a knob nobody can reason about.
3. ~~**The wall budgets are the honest pessimistic case**~~ **CLOSED as sized - B13 `missionBudget` 5000 / `runtime` 5600 and B14 4200 / 4800 are KEPT, now against measured runs rather than an estimate.** B13 finished at 55% of its mission budget and B14 at 50%. The pessimistic 1:1 assumption turned out to be the ONLY case: MechJeb issued zero warp commands across both descents, so the "~1,650 s expected if MechJeb warps" arm of the original arithmetic has been removed from the B13 spec rather than left as a plausible-looking number nobody will ever observe. **B13 is now the most expensive scenario in the suite** (2,825 s in the duration ledger against BDOCK-1's 2,164 s), so a nightly rotation probably cannot afford B13 + B14 + BDOCK-1 on the same night.
4. ~~**`landedMaxVerticalSpeedMps` / `landedMaxHorizontalSpeedMps` = 1.0 are PROVISIONAL**~~ **CLOSED, and NOT symmetrically - the standing 'raise BOTH together' rule was about RAISING a floor that reds, and neither red.** VERTICAL stays 1.0: measured -0.279 / -0.253 m/s is already 3.6x inside it, and vertical is the axis a harder touchdown moves first. `landedMaxHorizontalSpeedMps` tightened 1.0 -> 0.5, because 1.0 m/s is 3.6 km/h - a real slide, i.e. exactly the failure the conjunct exists to catch, so it was certifying what it was written to reject. Measured horizontal across both 116-sample dwells: worst sample anywhere 0.195 m/s (B13's touchdown frame), worst GATE-EVALUATED sample 0.052 m/s (the touchdown frame is the phase-entry frame and is never gate-evaluated), both settling to 0.000-0.004 within ~6 frames. 0.5 leaves 2.6x on the worst sample and 9.6x on the worst evaluated one. Deliberately NOT 0.25: that would leave 1.28x over B13's 0.195, far too thin for a single-sample basis, and a real slide accelerates at up to 1.63 m/s^2 on the Mun so it cannot hide under 0.5 for a 120 s dwell anyway.
5. ~~**`landedDwellSeconds` = 120 is PROVISIONAL**~~ **CLOSED at 120.** Measured 120.1 / 120.5 game seconds; both committed recordings carry the airless surface-class transition and enough SurfaceStationary / SurfaceMobile coverage to be a useful playback fixture, so there is nothing to raise.

**~~FINDING (Parsek-side): the commit-terminal log line cannot name the body for a LANDED terminal.~~ FIXED IN THIS LANE (Parsek source change, `ParsekFlight.FormatCommitTerminalLine`).** The original finding was correct and is kept for the trail: `ParsekFlight.CaptureTerminalOrbit` (`ParsekFlight.TerminalOrbit.cs`) writes `TerminalOrbitBody` ONLY for situations ORBITING / SUB_ORBITAL / FLYING / ESCAPING - a LANDED vessel returns early - and the finalizer's terminal-orbit refresh block is gated on `UsesTerminalOrbitMetadata` (`ParsekFlight.cs`), which covers Orbiting / SubOrbital / Docked and EXCLUDES Landed, so a landed recording's body lived only on `rec.TerminalPosition.body` (`CaptureTerminalPosition`) and `FormatCommitTerminalLine` emitted the literal `(null)`. Confirmed at the time against `logs/2026-07-25_1216_B7-duna-flyby/KSP.log`, which shows `terminalState=Destroyed terminalOrbitBody=(null)` on all six boosters and a body only on the Orbiting / SubOrbital lines. **THE FIX WAS TAKEN, not deferred:** `FormatCommitTerminalLine` (and the over-cap `FormatCommitTerminalSummaryLine`) now resolve the terminal body TERMINAL-STATE-AWARE - `TerminalPosition.body` first for the SURFACE terminals (`Landed` / `Splashed`), `TerminalOrbitBody` first otherwise, each falling back to the other. The state-awareness is load-bearing rather than cosmetic: NOTHING ever CLEARS `TerminalOrbitBody` (`CaptureTerminalOrbit`, `CopyTerminalOrbitFromSegment` and `IncompleteBallisticSceneExitFinalizer` only ever write it), so a craft that orbited the Mun, came home and landed on KERBIN would print `terminalState=Landed terminalOrbitBody=Mun` under a plain orbit-body-first fallback - confidently wrong in the one line an operator reads to answer "where did this recording end". Covered by nine cells in `Source/Parsek.Tests/CommitTerminalVerdictLoggingTests.cs`. Both landing specs therefore require the BODY-NAMING token `CommitTreeFlight terminal: rec=\w+ terminalState=Landed terminalOrbitBody=<body>`, and both MATCHED LIVE on flight 1. **WHAT THE FLIGHTS DO AND DO NOT PROVE (review round 2, 2026-07-26):** they prove the post-fix line is WRITABLE and MATCHES. They CANNOT discriminate the fix, because both craft orbited the SAME body they then landed on, so even a stale `TerminalOrbitBody` would have produced a byte-identical token; and no KSP.log was archived on either flight (`collectLogs.ran = false`), so there is no before/after log either. The claim that the old code could not name the body rests on the code read above plus the nine cells, not on a live comparison. The SOI-crossing token and the machine-side `landedOnTargetBody` assertion are kept as independent corroboration, not as substitutes.

**KNOWN LIVENESS GAP, stated rather than papered over: DESCENT has no WALL-time bound of its own.** The pure machine has no wall clock by design, and the fly loop's `warp-liveness-starved` floor is armed ONLY while the MACHINE has a native warp command outstanding (`state.warp_to_cmd is not None`) - which DESCENT deliberately never issues, because MechJeb owns the warp there. Arming that floor across the whole phase was considered and REJECTED: `WARP_LIVENESS_MIN_RATIO` is 5.0 game-s per wall-s over a 180 s window, and a legitimate MechJeb descent spends long stretches at 1x (`MinimumWarp` in `FinalDescent`), so it would false-flake a healthy landing. The runner's UT-freeze warp-stall watchdog is also inactive here (it only fires while OUR `WarpService` warp is active). So a MechJeb warp that WEDGES during DESCENT is bounded only by the generic mission wall budget. The clean fix, if a flight ever needs it, is a machine-published "an actor I do not own is expected to be warping" flag with its OWN ratio floor - deliberately not built on speculation. **Flight status:** still OPEN and still not built on speculation - but note that MechJeb issued ZERO warp commands across both descents (`warp=NONEx1.000` on all 1,303 / 1,330 DESCENT samples), so on the current 2.15.1 pin there is no MechJeb warp to wedge. The gap is theoretical for this profile until a pin bump makes the landing states warp.

**COVERAGE HONESTY - what the two green flights did NOT exercise.** The DESCENT autopilot supervisor's debounce / re-issue / DEAD ladder (`classify_landing_autopilot`, `landing-autopilot-not-enabled`) has NO LIVE COVERAGE: neither flight emitted a single non-zero `landingApDownStreak` or `landingApReissues` line, because `LandingAutopilot.Enabled` read 1 on every DESCENT frame THE SUPERVISOR EVALUATED - not on every polled frame: the archived telemetry reads `landAP=0` on B13's PARK -> DESCENT entry frame (ut 21,734.345, decided in PARK before the engage went out) and on the TOUCHDOWN frame of both flights (B13 ut 23,088.285, B14 ut 278,581.702), which is MechJeb disabling its own module on the landed frame and is exactly why `b5_decide` checks touchdown BEFORE the supervisor. The same holds for `landing-no-progress` (both flights closed exactly ONE window - B13 re-anchored at 64,963.5 m / ut 22,634.365, B14 at 16,099.0 m / ut 278,100.482, and touched down 453.9 s / 481.2 s later with 446.1 s / 418.8 s of the next window unspent - so the give-up itself never came close to firing), for `landing-touchdown-timeout`, for `landing-vessel-lost` and for `landed-never-stable`. What IS live-proven is the happy path end to end plus the two exit gates it actually crossed (touchdown detection on the frame MechJeb disables its own module, and the settled dwell). Do not read the status row as if the give-up ladder had flown.

**NO-PROGRESS GIVE-UP: BAND DISARMED + DEBOUNCED (review round 2, 2026-07-26).** `landing-no-progress` was the one liveness gate in `b5_decide` with NO debounce - it flaked on the FIRST frame past the window, while its siblings all require 3 (autopilot-down, capture-executor-down, capture-arm, park-stable, landed-stable) or 5 (impact-certain). The first fix covered the near-ground case with a VERTICAL-SPEED RESCUE (withhold the give-up while `vspd < 0`), and the flight data does not support that: on B14 flight 1's Minmus final descent MechJeb hops, and **23 of the 1,330 DESCENT frames read `vspd >= 0`** - the craft physically CLIMBS, peak +1.305 m/s, in runs of up to FIVE consecutive frames (ut 278,489.7 -> 278,493.8, alt 92.711 -> 96.370 m) - so the rescue is absent exactly on the frames that need it. Replaced with two guards that do not depend on that channel: (1) the window is **DISARMED** whenever its ANCHOR sits below `landingProgressMinDropMeters` AGL, because there the drop it demands does not exist below the craft and `descentTimeoutSeconds` is the instrument for slow; (2) above the band the give-up is **DEBOUNCED** over `LANDING_STALL_DEBOUNCE_FRAMES = 8` consecutive frames of proof - 8 rather than the house 3 because a HEALTHY landing was MEASURED producing five, so any depth <= 5 would still fire on a good descent. The vertical-speed channel is KEPT but demoted to what it actually is: corroboration ABOVE the band, counted as the "these knobs are mis-sized for this body" signal. Both holds are counted (`landingVspeedHolds`, `landingUnsatHolds`) and the countdown rides the machine-diff channel (`landingStallStreak`), so an operator sees the give-up coming. **NEITHER guard has been exercised live** - both flights' anchors stayed far above the band (64,963.5 m / 16,099.0 m) - and what actually kept the two flights green was anchor geometry, not either guard. Cells: 3 depth cells replaying the five MEASURED hop frames verbatim (`test_mlib.LandingStallDebounceDepthTests`), 3 fly-loop cells proving the countdown reaches the log (`test_shells.LandingNoProgressDebounceFlightTests`), plus the reworked verdict / descent cells. Non-vacuous by mutation: dropping the depth to 1, 3 or 5 reds 2 cells, removing the band disarm reds 3, removing the debounce entirely reds 7.

**LADDER COVERAGE CLOSED HARNESS-SIDE (2026-07-26), which is NOT the same as live-proven.** The ladder's coverage used to be pure-`mlib` plus one end-to-end shell cell that scripted the module disabled on EVERY frame - which cannot distinguish a 3-frame debounce from a 1-frame one, says nothing about whether the gate lines an operator would grep are ever emitted, and never exercises a transient. `test_shells.LandingAutopilotLadderFlightTests` (6 cells) now drives the WHOLE ladder through the real `mission_runner.fly_loop` against a scripted control: the debounce DEPTH (a channel flickering two-down-one-up forever must land normally and never re-issue), the re-issue ACTION arriving at the seam carrying the spec's `(touchdownSpeed, gears, chutes, rcs)` configuration, the bound on how many can be issued, the DEAD give-up's NAME (and that it is not the touchdown-timeout or no-progress name), the `landingApDownStreak` / `landingApReissues` gate lines in their exact order including the reset on each re-issue and the capped final rung, that a healthy always-enabled descent leaves both channels completely SILENT (the shape both live flights flew, so their silence now reads as evidence of health rather than of a dark channel), and that the -1 UNREAD sentinel never climbs the ladder. Proven non-vacuous by mutation: forcing the debounce to 1 frame reds 2 cells, removing the re-issue bound reds 3. Three `mlib` cells were added alongside (the re-issue re-anchors the no-progress window; a re-issue frame with an unreadable altitude or UT KEEPS the old anchor rather than writing NaN into it; the landed-frame ordering above). **What is still missing is the only thing a harness test cannot supply: MechJeb's own behaviour when it refuses to arm.** The cheap way to buy it is a live fault-injection flight (engage a landing with the module deliberately blocked); nobody has flown one, and until somebody does, the ladder stays NOT LIVE-PROVEN.

**REVIEW FOLLOW-UPS TAKEN 2026-07-26 (post-flight; harness plus one Parsek formatter).** (a) `landing-no-progress` was UNSATISFIABLE below `landingProgressMinDropMeters` AGL and had no debounce - the first frame past a window that had not shed the drop flaked immediately, so a lander descending at exactly the COMMANDED `landingTouchdownSpeedMps` near the ground was named "provably not descending". B13/B14 escaped this only by geometry, not by any guard. **The first fix (a vertical-speed rescue) was REPLACED in review round 2 - see the NO-PROGRESS GIVE-UP entry above for the current shape (band disarm + 8-frame debounce, with the vertical-speed channel demoted to corroboration above the band) and for the flight frames that falsified the rescue.** (b) `Flight.HorizontalSpeed` was a silent dark channel in `mission_runner.read_snapshot` - a bare `except` to NaN, and `mlib.landed_stable` fails CLOSED on a non-finite horizontal speed, so a settled lander would sit out the whole `landedTimeoutSeconds` dwell and flake `landed-never-stable` with nothing in the log naming the channel; it now carries the same latched Warn the NodeExecutor / periapsis / landing-autopilot channels have. (c) `FormatCommitTerminalLine`'s body precedence is now terminal-state-aware (see the FINDING above). (d) The DESCENT no-progress anchor heals its ALTITUDE half separately, so one non-finite altitude frame on phase entry no longer pins `landing_alt_ref` at None forever - without re-stamping the window clock, which would have made the named `altitude-unreadable` give-up unreachable on a permanently dark channel. (e) `b5_params_from_dict` now REJECTS `landingEnabled` without `captureEnabled` instead of silently degrading to the flyby machine and its flyby assertion rows. (f) The `touchdown_speed` read-back is compared NUMERICALLY (a float carve-out had excluded it from the mismatch check entirely). (g) `hlib.merge_durations` keeps the committed entry when the fresh one is more thinly sampled, so a worktree that flew one scenario no longer replaces a well-sampled committed entry with its own `n=1` and switches `duration_regressions` off for exactly the scenario that just ran.

## B11 / B12 Mun + Minmus ORBIT missions - capture, park, commit-in-foreign-SOI (b11_mun_orbit / b12_minmus_orbit) [LANE CLOSED 2026-07-25: B11 and B12 BOTH LIVE-PROVEN, B6's owed confirmation re-fly PAID. FOUR findings, all ROOT-CAUSED + FIXED + confirmed live (B11 flight 1 no-start watchdog vs MechJeb's pre-ignition hold; B12 flight 1 CORRECTION-BURN game-time budget; B12 flight 2 COAST warp thrash; B12 flight 3 TARGET-FLYBY warping past periapsis); branch `autotest-orbit-missions`]

Roadmap item 2 ("Mun/Minmus ORBIT missions - capture burn + commit-in-target-orbit terminal"). **ID note:** the roadmap called this lane "B8", but `automated-testing-scenario-catalog.md` section 2 already assigns B8 (loop the B7 tree as a mission), B9 (crash / rewind / re-fly) and B10 (career passive safety, a committed spec), and B3 is the EVA branch - so the lane is **B11-mun-orbit** + **B12-minmus-orbit**, and both the catalog and `autotest-status.md` now carry the mapping so nobody trips on the informal label again.

**Why it exists (the Parsek surface, not the rocketry).** B5 (Mun flyby), B6 (Minmus flyby) and B7 (Duna flyby) all fly THROUGH a foreign SOI and come back or continue. NONE of them ends its recording parked in another body's SOI. That end-state is the whole point: the commit path, the terminal classification, and the background-recording handoff for a tree whose recording CLOSES while the vessel is in orbit around a FOREIGN body. New registry cell `D1 commit-in-foreign-soi` (added to `harness/coverage/registry.toml` in the same change, per the growth rule). The lane originally also claimed `D5 bg-recording` on the argument that the committed vessel keeps existing in the target SOI; that claim was REMOVED 2026-07-25 because nothing gates it - `CommitTreeFlight` sets `backgroundRecorder = null` and `Patches.PhysicsFramePatch.BackgroundRecorderInstance = null` before returning, no assertion or required log token covers a handoff, and `settle_frames = 0` terminates the mission on the commit frame. BDOCK-1 is the honest claimant of that cell (two vessels, a real split).

**Reuse, not reinvention.** Both missions are the LIVE-PROVEN `mlib.b5_decide` machine with ONE new param, `captureEnabled`. Ascent, circularization, target selection, the ManeuverPlanner Hohmann transfer, the autowarped TLI, the dv-capped DIY course corrections, the arrival-quality re-correct, the flameout staging and the entire native/rails warp policy are byte-identical to the 26 B5 flights; with the flag OFF (the default) none of the new code is reachable, which the pre-existing suites prove by passing unmodified. B12 is a thin alias over the same machine, exactly as B6 is to B5.

**The new four-phase tail** (each phase names its vehicle configuration in the spec's MISSION PROFILE header):
- **PLAN-CAPTURE** - MechJeb `operation_circularize` with `TimeSelector.TimeReference = Periapsis` (surface verified against the pinned `mods/KRPC.MechJeb/Maneuver/OperationCircularize.cs` + `TimeSelector.cs`; the class doc says verbatim "To match apoapsis to periapsis, set the time to TimeReference.Periapsis"). Planning at SOI entry rather than chasing periapsis with our own warp stair is deliberate: MechJeb picks the burn UT and the NodeExecutor's autowarp flies the coast down to it, so there is no way to warp past the burn. NO target-altitude knob - the capture circularizes at whatever arrival periapsis the correction rounds produced, and a WIDE park window judges the result.
- **CAPTURE-BURN** - the NodeExecutor with autowarp set EXPLICITLY runner-side (the B-DOCK flight-12 lesson: the executor's autowarp is shared global state), SUPERVISED from an OBSERVED channel since flight 1 (see the flight-1 forensics below). Done evidence is a BOUND orbit: `0 < apoapsis <= parkMaxApoapsisMeters` AND `periapsis >= parkMinPeriapsisMeters` AND `eccentricity <= parkMaxEccentricity`. A still-hyperbolic approach reads a NEGATIVE apoapsis in the target frame, so a fly-past cannot be certified as a capture.
- **PARK** - the LIVE-PROVEN `forge_lko` held-dwell gate re-pointed at a foreign body: throttle CUT, every node CLEARED, attitude HELD (SAS stability-assist + RCS), rails DROPPED to 1x and self-healed there, then `parkDebounceFrames` in-gate frames HELD across `parkDwellSeconds` of game time. The dwell IS the recorded in-foreign-SOI coverage, which is why it is deliberately not warped.
- **ORBIT-COMMIT -> ORBIT-COMMITTED** - the mid-mission command-seam `CommitTree` (the B-DOCK route-1 reserved-command-id bridge, `ACTION_PARSEK_COMMIT_TREE`). OK is the terminal; ERROR/TIMEOUT flake.

**The failure that is not a success:** from TARGET-FLYBY onward, reading the home body (or any other real body) is an ASSERT-FAIL, not B5's RETURN terminal. The SOI-EXIT native warp is suppressed in capture mode for the same reason - warping toward the exit is warping toward the failure - while the rails stair still floors at `flybyWarpFactor`, so the no-1x-coast invariant holds.

**Liveness (budgets bound SLOW, watchdogs bound BROKEN).** Every actor-dependent phase carries a GAME budget AND a distinctly named fast-fail: `capture-executor-not-enabled` (MechJeb `NodeExecutor.Enabled` OBSERVED false past the bounded re-issues), `capture-window-missed` (the node's burn window passed unburned and the bounded re-plan is spent), `capture-executor-no-start` (no node / no readable node clock while static at 1x past `burnNoStartSeconds`), capture `under-burn` (a burn ran, the executor wedged, the orbit is still unbound), no-capture-node after `PLAN_MAX_ATTEMPTS`, PARK "never reached a stable park" vs "stabilized but never HELD stable through the dwell", and `tree-commit seam returned ERROR|TIMEOUT`. `B5State.flake_reason` is new so `resolve_flight_verdict` reports these instead of the generic "phase X timed out".

**Assertions.** In capture mode `returnedToHome` is REPLACED (the mission must not return) by `capturedInTargetOrbit` (the orbit read at PARK entry, carried), `parkedStable` (the dwell completed and the park was ever in-gate) and `treeCommitted` (the seam answered OK). `flybyPeriapsisFloor` is kept and now tracks the WHOLE in-SOI stay, so it also certifies the parked orbit's periapsis. Flyby mode returns its four rows byte-identically.

**Fixture:** the already-committed `b2-lko-craft` (shared with B2/B4/B5/B6). No forge run, no new fixture. Delta-v survey: the stage holds ~1500-1600 m/s after the 80 km circularization; Mun = ~860 TLI + up to ~340 corrections + ~150-220 capture (capturing HIGH is CHEAPER, and B5's certified arrivals were 956-1,142 km), Minmus = ~930 TLI + corrections + ~70-90 capture. Both fit, with the flameout-staging watchdog reaching the X200-16 upper tank if the core dies mid-burn.

**POST-FLIGHT STATUS of the five first-flight pins (both missions have now flown green; all five are CLOSED):**
1. ~~**PIN `recordings.count`**~~ **CLOSED 2026-07-25 - both windows PINNED at `{min 8, max 8}`.**
   The numbers are MEASURED, not derived: B11 flight 4 (wall 1,271 s,
   `results/2026-07-25_0400_B11-mun-orbit.json`) and B12 flight 5 (wall 580 s,
   `results/2026-07-25_0349_B12-minmus-orbit.json`) are the first passes of each carrying
   `verifiers.expectations.observed.recordings.count`, and both read **8**. The enumeration behind
   the 8, for `fixtures/saves/b2-lko-craft` (the stock Kerbal X B2/B4/B5/B6 also fly): **+1**
   root/main recording (mandatory - `autoRecordOnLaunch` true and the `Recording started`
   logContract already fails without it); **+6** radial boosters as parent-anchored debris
   children, LIVE-ESTABLISHED by the first live B2 flight (2026-07-20: exactly 7 sidecars,
   analyzer RED=0); **+1** the FLAMEOUT-STAGED ascent core, which is the 8th and the one that
   separates this profile from B2's 7. Ruled out: no `launchEscapeSystem` / escape-tower part
   exists anywhere in the fixture's part list (verified by reading the PART blocks out of
   `persistent.sfs`), the 3 `launchClamp1` parts contribute 0, and the SECOND `Decoupler.2`
   (upper stage -> pod) never fires because B11/B12 have no separation phase and end parked with
   the stack intact.
   **`min = 8` is measured too.** Across every archived run that actually crossed into a foreign
   SOI (selector: the `SOI change boundary suppressed in tree mode: Kerbin to` token in the
   collected KSP.log), the count is 8 on EVERY run at or after commit `82398e157` ("Live finding
   15: flameout staging", 2026-07-22 18:30) - 10 runs of 10 (B5 x4, B7 x4, B11, B12) - and 7 on
   the five clean runs before it; the first 8 came from the run whose `git-state.txt` reads
   exactly `82398e157`. The 7s are an OLDER BUILD, not run-to-run variance. `min = 7` was
   considered and REJECTED: 7 is the exact count a single dropped recording would produce, so a
   floor of 7 would blind the only numeric guard to the regression class it exists to catch.
   **Residual risk, stated honestly:** `_b5_flameout_stage` is a CONDITIONAL watchdog (it pops a
   stage only when a commanded burn reads zero available thrust for `FLAMEOUT_DEBOUNCE_FRAMES`),
   so the 8th recording is deterministic for THIS vehicle's fuel budget rather than guaranteed by
   the machine. A future change to ascent efficiency or MechJeb autostage that removes the
   flameout WILL red this window, and an operator must then re-derive it from a fresh measured
   count.
   **THE COUNT IS COMMIT-BLIND - do not read it as commit evidence.** `run.py count_recordings`
   counts `.prec` sidecars, and `ParsekScenario` OnSave writes sidecars for the ACTIVE
   (uncommitted) tree too (`EnsureRecordingFilesCurrentForSave`, `treeKind="active tree"`). Two
   archived runs (`logs/2026-07-25_0253_B11-mun-orbit`, `logs/2026-07-25_0538_B12-minmus-orbit`)
   flew the full ascent, crossed into the target SOI, NEVER committed (zero `CommitTreeFlight`
   lines, zero `committree committed=true`) and still produced exactly 8 `.prec`. The window
   guards recording TOPOLOGY; the commit is guarded by the logContract tokens, which now include
   the foreground SOI-crossing line and a per-recording terminal verdict naming
   `terminalOrbitBody`.
   **What made the pin possible:** `hlib.observed_expectation_facets` returns the measured count
   from verifier 7 and run.py persists it at
   `verifiers.expectations.observed.recordings.count` in `results/<runId>.json`, on PASS as well as
   FAIL. Before this, a PASS ran no collect-logs and the produced save was transient, so a green
   run's count was unrecoverable post-hoc.
2. **The post-mission CommitTree question - CLOSED (no change needed).** Neither spec declares one and none was added: all five FULL-PASS flights (B11 flights 2, 3 and 4; B12 flights 4 and 5) greened with the mid-mission commit alone, so nothing forced the question. The measured pin settles it in the other direction too: at exactly 8 there is NO post-commit recording, so Parsek does not open a fresh tree for the still-orbiting vessel. For the record: the seam's `CommitTreeImpl` returns ERROR `no-active-tree` (`ParsekTestCommandAddon.cs`) with no live tree, which is the expected state after the mid-mission commit. If flight 1 shows Parsek opening a FRESH tree for the still-orbiting vessel after the commit, add a post-mission `CommitTree` step THEN - a speculative guess reds the run either way.
3. ~~**A foreground SOI-change log token - STILL NOT ASSERTED**~~ **CLOSED 2026-07-25 - it IS the suppressed-in-tree-mode line, and both specs now assert it.** The original reasoning was wrong about which branch is production: `ParsekFlight.HandleSoiChangeSplit`'s suppressed branch gates on `ShouldSuppressBoundarySplit(activeTree)`, which returns `activeTree != null`, and always-tree mode (#271) gives EVERY recording a tree - so a foreground SOI change in a recorded flight ALWAYS emits `SOI change boundary suppressed in tree mode: <from> to <to>`. Verified present exactly ONCE per flight in `logs/2026-07-25_0253_B11-mun-orbit/KSP.log` (`Kerbin to Mun`) and `logs/2026-07-25_0538_B12-minmus-orbit/KSP.log` (`Kerbin to Minmus`). Without it the lane could green a mission that never left Kerbin's SOI. **A second token landed with it:** `CommitTreeFlight terminal: rec=<id> terminalState=<state> terminalOrbitBody=<body>` (`ParsekFlight.LogCommitTerminalVerdicts`, one bounded line per committed recording, summary above 20). This is the headline claim's only real evidence - `CommitTreeFlight: starting tree commit at UT=` is logged unconditionally on entry and `committree committed=true` only proves the seam call returned, so before this a tree that ended around KERBIN produced byte-identical evidence to one that ended around the MUN. Both specs now require `terminalState=Orbiting terminalOrbitBody=Mun` / `=Minmus`. **FLOWN AND PROVEN 2026-07-25, in both directions:** `results/2026-07-25_0545_B12-minmus-orbit.json` red'd PARSEK-FAIL on exactly this token (`logContracts.required not matched: CommitTreeFlight terminal: rec=\w+ terminalState=Orbiting terminalOrbitBody=Minmus`) because that flight ran the PREVIOUS DLL, and the very next run `results/2026-07-25_0600_B12-minmus-orbit.json` is a PASS with `mismatches=[]` (mission wall 580.826 s); `results/2026-07-25_0611_B11-mun-orbit.json` is the matching B11 PASS (mission wall 1270.195 s), and the same emitter later carried the LANDING lane's `terminalState=Landed terminalOrbitBody=<body>` token once its body resolution was made terminal-state-aware. The other four tokens are unchanged and still sourced from their call sites: the `Recording started` / `Recording stopped` pair, `CommitTreeFlight: starting tree commit at UT=` (`ParsekFlight.cs:13136`) and `committree committed=true` (`ParsekTestCommandAddon.cs:1520`).
4. **Re-time the budgets - CLOSED, and both are now measured end to end.** B11 flies in **1,269 wall seconds** against 3000/3500 and B12 in **580** against 4200/4700, so both carry 2-8x headroom on FULL runs (not just to the capture instant). The B12 number is the one worth remembering: the coast + periapsis fixes turned the longest-coast mission into the CHEAPEST of the pair. Budgets deliberately left as-is - they are wall-clock envelopes, not targets. Original entry: ~~B11 3000/3500 and B12 3600/4100 are pure estimates~~ - flight 1 MEASURED B11 at 1064 wall seconds to the capture ignition instant, so B11's 3000/3500 now stand on measurement (~1,300 nominal end to end, ~2,100 with the one bounded stale-node re-plan) and B12's were raised 3600/4100 -> 4200/4700 because MechJeb's ~600 s pre-ignition hold is a real, now-known wall cost. Still to measure: PARK + ORBIT-COMMIT (never reached), and `captureBurnTimeoutSeconds` 60000/200000 (flight 1 used 5,030 game seconds of the 60,000).
5. **Confirm the capture-window sizing - CLOSED, and the window proved BOTH directions.** Every green capture landed deep inside it (B11 eccentricity 0.000127, B12 0.00026, both far under the 0.25 ceiling), and B12 flight 3's 325 x 5.3 km grazing orbit was CORRECTLY rejected as an under-burn rather than greened - so the window is neither too tight nor a rubber stamp. Original sizing note: `parkMaxApoapsisMeters` 2,000 km (Mun; SOI edge ~2,230 km) and 1,500 km (Minmus; SOI edge ~2,187 km) are deliberately wide because the arrival periapsis is MechJeb's business. An arrival above the ceiling produces a NAMED under-burn flake, not a hang - but it means the window, not the flight, is wrong.

### Harness reporting gap - the expectations verifier judged the recordings count but never RECORDED it [FIXED, branch `autotest-orbit-missions`]

**The gap.** Verifier 7 (`hlib.evaluate_expectations`) compares the measured `.prec` count against
`expectations.recordings.count` and records the VERDICT, the mismatch strings and the reserved
blocks - but never the NUMBER. On a PASS the harness deliberately runs no collect-logs (design
results layout / edge 18) and the produced save is transient, so a green run's measured count is
gone the moment the run ends. That is precisely the number needed to turn a PROVISIONAL count
window into an honest pin, which is why B11 and B12 are the only flown scenarios still carrying an
underived window while every EVA case is pinned exactly (EVA-1 4, EVA-2 2, EVA-3 7, EVA-4 3). The
alternative - deliberately failing a run to trigger collect-logs, or re-flying with a manual save
grab - costs a 580-1,269 s flight to learn one integer.

**The fix (pure side in hlib, two lines in run.py).** New pure `hlib.observed_expectation_facets`
builds the MEASURED-facet dict and `evaluate_expectations` carries it on a new optional
`ExpectationResult.observed` field; run.py's verifier-7 glue adds `"observed": dict(exp.observed)`
to the `detail["expectations"]` block it already builds, which is what lands in the result JSON's
`verifiers` object. Read it at `verifiers.expectations.observed.recordings.count` in
`results/<runId>.json`. `recordings.count` is the only facet the recordings block declares, so it
is the only one observed; the shape mirrors the `[expectations.*]` spec surface
(`{"recordings": {"count": 7}}`) so a future measured facet slots in beside its spec counterpart
without a format break.

**Contract details that are load-bearing.**
- ADDITIVE and BACKWARD COMPATIBLE: the field defaults to empty, old positional constructions of
  `ExpectationResult` still build, and results written before this change simply lack the key. A
  consumer must read ABSENT as "this run predates the measurement", NEVER as zero.
- Recording is UNCONDITIONAL on the spec: a scenario declaring no count window still gets its
  measured count, which is how a NEW scenario earns its first honest window.
- 0 is a MEASUREMENT (a no-recording scenario legitimately produces none) and is recorded; only a
  `None` count (save unreadable / not counted) omits the key.
- Recorded on FAIL as well as PASS - "9 > max 8" only tells you the window is wrong if you can see
  the 9.
- The SKIPPED branch (short-circuit / driver-invalid) was deliberately left alone: those runs are
  non-PASS, so collect-logs already preserves their save, and touching that branch would have
  widened the run.py diff for no gain.

Cells: 7 in `harness/lib/test_hlib.py` (`ObservedExpectationFacetsTests`) plus two round-trip
assertions in `test_run_smoke.py` - one reading the persisted `results/<runId>.json` on the seam
happy path, one on the flown autopilot path.

### B11 flight 1 (2026-07-24) - CAPTURE-BURN flake: OUR watchdog collided with MechJeb's own pre-ignition hold [FIXED, branch `autotest-orbit-missions`]

**What flew.** `phasesReached` = PRELAUNCH, MJ-ASCENT, CIRCULARIZE, ORBIT, PLAN-TRANSFER, TRANSFER-BURN, COAST-TO-TARGET, PLAN-CORRECTION, CORRECTION-BURN, COAST-TO-TARGET, PLAN-CORRECTION, CORRECTION-BURN, COAST-TO-TARGET, TARGET-FLYBY, PLAN-CAPTURE, CAPTURE-BURN. Wall 1064 s. Everything worked - the ship reached the Mun and the capture plan issued cleanly (`[Capture] capture plan issued (circularize at periapsis); nodes=1`, nodeDv 277.016 m/s at nodeUt 21549.027) - and the run fast-failed BY NAME exactly as designed: `phase CAPTURE-BURN: capture-executor-no-start`. Full stdout: `harness/results/b11-flight1.out`.

**The evidence.** From CAPTURE-BURN entry (ut 16518.6, alt 2,179 km, ap -1,560,099 = hyperbolic) the craft coasted to periapsis (~137 km) over ~5,000 game seconds with `nodes=1 nodeDv=277.016 thr=0.000` on EVERY frame - the node intact and unburned, the throttle never leaving zero. Warp ran RAILSx29 -> x1000 (MechJeb's autowarp, not ours: the machine issues NO warp action inside CAPTURE-BURN) and then dropped to `NONEx1` at ut ~20939, where it stayed for exactly 600 game seconds until the give-up at ut 21539.434 with `burnStaticAge=600.18`. The node was at 21549.027, i.e. **9.6 seconds later**.

**ROOT CAUSE (source-cited, not inferred).** Decompiled MechJeb2 2.15.1.0 `MuMech.MechJebModuleNodeExecutor.StateWarpAlign()`:

```
else if (_ignitionUT - VesselState.time > 600.0) {
    Core.Attitude.SetAxisControl(pitch: false, yaw: false, roll: false);
    Core.Warp.WarpToUT(_ignitionUT - 600.0);
} else {
    Core.Warp.MinimumWarp();
    SetAttitude();
}
```

with `_ignitionUT = node.UT - halfBurnTime` (`CalculateIgnitionUT`), WARPALIGN -> LEAD at `_ignitionUT - LeadTime` (default 3 s) and LEAD -> BURN at `_ignitionUT`. The high-warp branch is taken only while `AlignedAndSettled()` - angle < 1 deg AND `vessel.angularVelocity.magnitude < 0.001` rad/s. The flight read `angV=0.003` through the whole hold, so the craft never SETTLED, MechJeb never re-warped, and the executor sat at 1x with a zero throttle and an unchanged orbit for the **full 600 game seconds it is designed to**. That is byte-for-byte the "orbit unchanged and static at 1x" signature `_b5_track_burn_stagnation` fires on, and `burnNoStartSeconds` defaults to the SAME 600.0 - so the watchdog expired at the exact ignition instant and killed a perfectly healthy executor one poll early. The executor was never dead; we were.

**Why TRANSFER-BURN survived the same mechanism.** Identical shape, luckier ship state: in LKO the craft DID settle (warp dropped to 1x at ut 1265, re-warped RAILSx2.68 -> x50 at ut 1321), so the static run was ~55 s, nowhere near the bound. TRANSFER-BURN also deliberately IGNORES the no-start signal (`_nostart` is discarded there - "a no-start TLI has produced no transfer, and the phase budget owns that outcome"), so the B5/B6/B7 flyby family was never exposed and is untouched by the fix.

**The missing observation.** We COMMANDED `mj_execute_nodes` and had NO channel that OBSERVED whether the executor engaged - the same commanded-vs-observed gap that produced the B-DOCK docking-AP and the EVA-4 ladder-release defects. Added: `TelemetrySnapshot.node_executor_enabled`, a TRI-STATE int fed from KRPC.MechJeb `NodeExecutor.Enabled` (the inherited `MuMech.ComputerModule.Enabled` property - the pinned `NodeExecutor.cs` binds Autowarp / LeadTime / ExecuteOneNode / ExecuteAllNodes / Abort and nothing else, so MechJeb's own `State` field (WARPALIGN / LEAD / BURN / IDLE) is NOT reachable and `Enabled` is the only observable executor channel; its `Tolerance` property is outright broken - `InitInstance` never initializes the backing object). Opt-in via `KrpcMissionControl(read_node_executor=True)`, ON for B11/B12 only, `-1` UNREAD fail-closed sentinel everywhere else, so every other mission's snapshot, compact window line and telemetry line stay byte-identical.

**The fix (two pure classifiers, both unit-celled).**
- `mlib.classify_capture_executor` - the OBSERVED side. `Enabled` read FALSE for `CAPTURE_EXECUTOR_DISABLED_DEBOUNCE_FRAMES` (3) consecutive frames with a node pending re-issues `mj_execute_nodes` (re-stamping the progress anchor so the fresh attempt earns a full window), bounded at `MAX_CAPTURE_EXECUTOR_REISSUES` (2), then fast-fails `capture-executor-not-enabled` - seconds after the evidence instead of 600 s later. An UNREAD channel grants NO verdict; no pending node is never an executor fault (the executor legitimately self-disables once it consumes the node: decompiled `OnFixedUpdate` -> `!_hasNodes` -> `Abort()`).
- `mlib.classify_capture_nostart` - the node-clock side, which works even with the channel unread. MechJeb ignites at `node.UT - halfBurnTime`, i.e. never LATER than `node.UT`, so any static-at-1x frame before `node.UT + CAPTURE_BURN_WINDOW_GRACE_SECONDS` (90 s) HOLDS. Past that window with the node still pending and the orbit still untouched, the craft arrived LATE: the machine clears the stale node and re-plans ONCE (`MAX_CAPTURE_REPLANS`) rather than fly a node whose window has passed, then names `capture-window-missed`. A non-finite node clock fails CLOSED to the original `capture-executor-no-start` name and burns no re-plan budget. Total liveness cost of the whole guard: 600 + 90 game seconds, never the 60,000 s phase budget.

**Flight 2 (2026-07-25): FULL PASS, attempt 1, all seven verifiers green, wall 1,268 s, analyzer red=0.** The predicted signature matched exactly: CAPTURE-BURN held through MechJeb's 1x pre-ignition window without flaking, the burn ran, and the apoapsis flipped -1,560,099 -> +138,789 m at eccentricity 0.000127 (an essentially perfect circular Mun orbit); PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED with all six assertions met. The fix is LIVE-PROVEN.

**Flight 3 (2026-07-25): FULL PASS again, on the CHANGED profile - the confirmation re-fly the B12 flight-3 periapsis bound owed.** Wall 1,269 s, all six assertions met, capture eccentricity **0.000127** - the same number flight 2 read, so re-pointing TARGET-FLYBY at the periapsis clock did not move the capture quality by a digit. TARGET-FLYBY itself collapsed from **8,213 game seconds on 2 warp commands** (the B12 flight-3 signature this mission would have inherited) to **27 game seconds on 2 commands**: the machine now warps the approach itself and hands MechJeb a craft already ~900 s from periapsis, instead of arming 87 game seconds after SOI entry and letting the executor's autowarp fly a ~5,000 s coast down. B11's LIVE-PROVEN mark is honest at HEAD.

**Predicted flight-2 signature (for the record, all of it observed).** CAPTURE-BURN entered at the SOI-edge coast; `nodeExec=1` on every capture frame; MechJeb autowarps down to ~`node.UT - 610`, holds 1x for ~600 game seconds with `burnStaticAge` climbing past 600 and NO flake; the throttle then rises, `nodeDv` collapses from ~277 to ~0, apoapsis flips POSITIVE, and the machine enters PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED. `captureExecDownStreak=0 captureExecReissues=0 captureReplans=0` throughout on a clean run.

### B12 flight 1 (2026-07-25) - CORRECTION-BURN timed out: the aim-then-warp wait spends the phase's own GAME budget [FIXED in the SHARED machine, branch `autotest-orbit-missions`]

**This is a PRE-EXISTING B5/B6-family defect, not a B12 one.** Saying it plainly: B5 and B11 pass it on luck of geometry, B6 has simply not re-flown since it was introduced, and B12 is the first mission whose geometry makes it deterministic.

**What flew.** `MISSION-FLAKE`, reason `phase CORRECTION-BURN timed out`, wall 286 s. phasesReached = PRELAUNCH, MJ-ASCENT, CIRCULARIZE, ORBIT, PLAN-TRANSFER, TRANSFER-BURN, COAST-TO-TARGET, PLAN-CORRECTION, CORRECTION-BURN. The TLI was perfect (ap 48,823,627 m, a clean Minmus transfer) and the correction node planned fine and tiny (`nodes=1 nodeDv=13.301`). Full stdout: `harness/results/b12-flight1.out`.

**The evidence, frame by frame.** CORRECTION-BURN entered at ut 475.337. The flip ran under 2x physics warp and CONVERGED: `alignedStreak 0->1` at ut 490.2 (apErr 25.9), `1->2` at 491.3 (apErr 22.8), physics warp dropped to 0. At ut 492.668, aligned + settled + warp NONE, the AIM-THEN-WARP branch fired: `gate warpToCmd none->74193.288` + `action warp_to_ut value=74193.288` (that is `node_ut - nodeArrivalMarginSeconds`, so the node sits at ut 74,208.288). The native warp then ran up to RAILSx10000 and the ship coasted ut 492 -> 8,427 (alt 636 km -> 8,386 km), orbit constant, node never burned. At ut 8,427.354 - 7,952 game seconds into a 4,000 s budget - `_b5_stay_or_flake` fired inside the warp-hold branch, which is exactly the `gate warpToCmd 74193.288->none` + `action cancel_warp` pair the log ends on.

**ROOT CAUSE.** The no-1x-coast PR (commit `4219832b6`, 2026-07-22, "aim-then-warp corrections") changed the DIY correction burner from "aim, then burn NOW" to "aim, then natively warp to `node_ut - nodeArrivalMarginSeconds`, re-verify, then throttle". That made the phase's completion time depend on WHERE MECHJEB PUT THE NODE - and left it bounded by `transferBurnTimeoutSeconds`, a GAME-time budget that the warp itself spends, because warping advances game time.

Measured, same machine, same params, two bodies:

| | phase entry | node UT | wait needed | budget | outcome |
|---|---|---|---|---|---|
| B11 (Mun, flight 2) | ut 1,898.5 | 4,907.7 | 2,994 s | 4,000 s | PASS on 25% margin |
| B12 (Minmus, flight 1) | ut 475.3 | 74,208.3 | 73,733 s | 4,000 s | 18x over, impossible |

B7 is unaffected only because its interplanetary spec already carries `transferBurnTimeoutSeconds = 25000000`.

**Is B6 exposed? YES.** `B6-minmus-flyby` shares the machine, the correction params (`courseCorrectPeriapsisMeters = 20000`, `maxCorrectionDvMps = 300`, `correctionTriggerAltsMeters = [0, 20000000]`) AND the 4,000 s budget, and its live-proven flights (findings 9/10/11, 2026-07-22) all describe the PRE-aim-then-warp burner that threw the throttle immediately. B5 carries an explicit post-change re-certification (flight 26, NO-1X CERTIFIED at HEAD config); B6 carried none. **B6 owed a confirmation re-fly on the fixed machine, and PAID it 2026-07-25: PASS, wall 359 s, all seven verifiers green** - so the shared correction fix is live-proven on the flyby side as well as the orbit side, and B6's LIVE-PROVEN mark is honest at HEAD.

**The fix (shared machine, two pure decisions).** A game-time budget is the wrong instrument for a ballistic wait, and it is also structurally incapable of bounding the failure it was reached for: a STALLED warp advances no game time, so a game-time bound never fires on one. The runner's own warp-stall watchdog and the mission WALL budget own that class.

- `mlib.correction_budget_expired` - CORRECTION-BURN's budget now bounds the BURN. It is SUPPRESSED entirely while an aim-then-warp is in flight toward a still-future node, and it re-anchors at the warp ARRIVAL (`corr_budget_anchor_ut`), the same seam that already re-anchored `corr_nostart_anchor_ut` and the aligned streak under the comment "warp time is not alignment time". A round that never aim-warps is UNCHANGED (the clock still runs from phase entry). Post-anchor the phase needs ~22 game seconds (B11 round 1: arrival 4,892.8 -> exit 4,914.7) against 4,000, so the bound went from 1.3x on one body and negative on the other to 100x+ on both - no spec number needed changing.
- `mlib.classify_correction_timeout` - the naming gap. A budget expiry is now a NAMED flake: `correction-burner-no-start` (node pending, burner never throttled, orbit unchanged - the B12 case) or `correction-burn-incomplete`. It can never again ride the generic "phase X timed out".
- `corr_giveup` - every correction ROUND give-up (`node-gone` / `cut-reached` / `overshoot` / `no-progress` / `align-no-start`) now names itself on the machine state and the machine-DIFF line. A round exit used to be indistinguishable from a clean cut in the log, and the whole B12 diagnosis hinged on knowing which one fired.

**Blast radius.** The change is confined to the `B5_CORRECTION_BURN` branch (`_corr_stay_or_flake` replaces `_b5_stay_or_flake` there; every other phase is untouched) and it strictly RELAXES a bound on the paths B5/B11 already pass. All 538 mission cells, 427 harness cells and 203 provision cells pass unmodified, as does the C# suite.

**CONFIRMED by flight 2 (2026-07-25).** Both correction rounds cleared: round 1 ran ut 475.315 -> 74,195.733 as ONE continuous aim-warp (73,720 game seconds) with no flake and no `cancel_warp`, round 2 completed at ut 74,228, `rounds=2`, and the machine reached COAST-TO-TARGET. The fix is LIVE-PROVEN. (Flight 2 then died on the wall budget inside that coast - a DIFFERENT shared defect, see the next entry.)

**Predicted re-fly signature (for the record, observed).** CORRECTION-BURN round 1: flip converges in ~17 game s, `warpToCmd none->74193.288`, the native warp runs at up to RAILSx10000 with NO flake and NO `cancel_warp`, arrival re-anchors both clocks (`corrBudgetAnchorUt` appears on the machine line), the attitude re-verifies, `set_throttle 0.25` fires and `nodeDv` collapses 13.3 -> under `correctionCutDvMps`, then `corrGiveup=cut-reached` and back to COAST-TO-TARGET with `rounds=1`. Round 2 the same at the 20,000 km trigger, then the Minmus SOI, PLAN-CAPTURE, the ~600 s MechJeb pre-ignition hold, the capture burn, PARK, ORBIT-COMMIT, ORBIT-COMMITTED.

### B12 flight 2 (2026-07-25) - COAST-TO-TARGET cancelled its own warp on every blind conic read [FIXED in the SHARED machine, branch `autotest-orbit-missions`]

**Also a shared B5/B6/B7 defect, and also metastable.** B11's Mun coast escapes it by luck; B12's long Minmus coast latches into it and never escapes.

**What flew.** `INVALID` autopilot-flake `mission-budget-expired (no result)`. The correction fix from flight 1 WORKED - both rounds cleared (`rounds=2`, round 1 ran ut 475.315 -> 74,195.733 as one continuous 73,720 game-second aim-warp) - and the run then burned its entire 4,200 s wall budget inside COAST-TO-TARGET, reaching ut 225,990 with the coast target at 267,644.669, i.e. **41,655 game seconds still to go**. Full stdout: `harness/results/b12-flight2.out` (51 MB, 182,205 lines).

**The evidence.** Inside COAST-TO-TARGET: **3,603** `warp_to_ut` issues against **3,602** `cancel_warp`, in an endless four-line cycle. (Scope matters and the two numbers get confused: the COAST phase issued 3,603, the MISSION total is 3,604 because CORRECTION-BURN issued one aim-warp of its own; all 3,602 cancels are in the coast. Counted 2026-07-26 from the per-phase `action` lines of `results/2026-07-25_0103_B12-minmus-orbit_mission.stdout.log`. Every phase-scoped figure elsewhere in this doc and in `mlib.py` is the 3,603.)

```
gate warpToCmd 267644.669->none | ... nextPe=nan   warp=RAILSx2.680
action cancel_warp
gate warpToCmd none->267644.669 | ... nextPe=38305 warp=NONEx1.000
action warp_to_ut 267644.669
```

The two alternating frames differ in exactly one input, and the crosstab over the sampled COAST telemetry frames is total:

| | warp=NONE | warp=RAILS |
|---|---|---|
| `time_to_soi` finite | 2,451 | 7 |
| `time_to_soi` NaN | 0 | 1,154 |

Every blind SOI read happened while rails-warping; every unwarped frame read it fine. The rails rate never escaped ~2.7x because every ramp was cancelled before it could climb, and the coast averaged ~40 game seconds per wall second against a span that needs 193,416 of them (it would have needed ~5,240 wall seconds at that rate).

**ROOT CAUSE (code citation).** `mlib.b5_decide`, the COAST-TO-TARGET warp policy, derives the native target from a DERIVED OBSERVATION on every single poll:

```python
elif (_is_finite(snapshot.time_to_soi) and _is_finite(snapshot.ut)
        and snapshot.time_to_soi > state.params.soi_lead):
    native_target = snapshot.ut + snapshot.time_to_soi - state.params.soi_lead
else:
    desired = state.params.coast_warp_factor      # rails fallback
...
if native_target is not None:
    return _b5_native_warp(stayed, snapshot, native_target)
if stayed.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
    return _b5_cancel_native_warp(stayed, snapshot)   # <-- revokes the command
```

A NaN `time_to_soi` fails the `elif`, falls into the rails fallback, leaves `native_target` None, and the "never two warp writers" cancel then revokes the armed native warp. KSP cannot read the patched-conic SOI time while it is re-patching under a warp ramp, so **the cancel destroys the very observation the command depends on**, the next (unwarped) poll re-reads it finite, re-arms, ramps, goes blind, and cancels again. Of the candidate hypotheses: it is a state-vs-observation mismatch, it is not a per-poll unconditional cancel, no gate toggles, and the runner does treat an in-flight warp as active (`warping_to` was finite on the cancel frames - that is one of the two conditions the cancel fires on).

**Why B11 (and B5) survive.** The loop is metastable, not deterministic. B11 flight 2's Mun coast issued `warp_to_ut` exactly ONCE with ZERO cancels: its first post-issue read happened to be finite (30 of 30 warping COAST frames read finite), the warp locked in at RAILSx1000 and the coast flew. B12 hit a NaN inside the first ramp and never escaped. B7 is insulated by enormous budgets rather than by geometry.

**Why the no-1x-coast certification did not catch it - AND STILL CANNOT. This is a real gap in an EXISTING gate, not a missing new instrument.** Two independent reasons, both worth writing down because both survive the fix:
- `test_no_1x_coast_invariant` is a MACHINE-COMMAND invariant: it asserts the machine only COMMANDS rails 0 in named cases. Here the machine never commanded 1x. The game was at 1x because the machine had just CANCELLED its own warp, which the invariant does not model at all. A command-side assertion is structurally blind to a defect whose whole signature is the game's OBSERVED rate disagreeing with what we commanded.
- `warp_audit.py`'s 1X-COAST VIOLATIONS rule needs a CONTIGUOUS 1x window of >= 30 wall seconds. Flight 2's 1x is frame-INTERLEAVED with ~2.7x rails (issue, blind read, cancel, re-issue), so no contiguous window ever forms. 3,603 warp commands and ~78.5k frames at 1x produced ZERO audit violations.
Net: B5 flight 26 is NO-1X CERTIFIED at HEAD config and the metastable thrash would have passed that certification too. The gap is a UTILISATION gap, not a mode gap. It is covered for now on the MACHINE side (the `coast-warp-thrash` fast-fail at `MAX_PHASE_WARP_ISSUES` = 500 plus the per-phase `warpUtilisation` block, which named the very next defect at a glance) - but the AUDIT itself is still blind to the class, and closing it properly means a utilisation rule in `warp_audit.py` that replaces the contiguous-1x heuristic. Filed under the mission time-accounting task; recorded here as a KNOWN GATE, mirrored in `autotest-status.md` known-gates item 8 (item 7 is the commanded-vs-observed class the B1 chute fix opened).

**The fix.**
- `mlib.coast_native_warp_hold` (pure) - the native coast target is an ABSOLUTE UT and does not need `time_to_soi` to stay readable. A blind read while the game IS warping (rails mode, a live `warping_to`, or any rate above 1x) HOLDS the armed command and emits nothing. A blind read with the game NOT warping still cancels - that is the honest "the encounter really is gone" frame. A readable frame always belongs to the normal policy (retarget through the existing asymmetric hysteresis, or the inside-the-lead rails handover). Scoped to the SOI-coast fallback branch only, so the pending-node and both correction-trigger warp modes keep their exact prior cancel behaviour and a correction trigger can never be warped past.
- `MAX_PHASE_WARP_ISSUES` (500) + the NAMED `coast-warp-thrash` fast-fail, counted on `phase_warp_issues` (also on the machine-state line as `phaseWarpIssues`). A healthy coast issues 1; flight 2 issued 3,603. The cap fires at roughly a seventh of the wall cost that flake took. Post-review the counter RESETS per phase entry (so it bounds a single warp episode, which is the failure mode it describes, instead of accumulating across a whole mission) and the same guard is armed at the other two native-warp sites under distinct names, `correction-aim-warp-thrash` and `flyby-warp-thrash`. A separate `warp-liveness-starved` give-up bounds a warp that is running but not actually warping with a NAMED failure instead of the generic wall reaper.
- **What `warp-liveness-starved` does and does not cover (measured 2026-07-26; the earlier wording here implied it was a second bound on flight 2, and it is not).** It could NOT have caught flight 2: that thrash cancelled the command every other frame (3,603 arms against 3,602 cancels), and the fly loop resets the liveness episode whenever `warp_to_cmd` clears, so the episode never lasted two frames. The thrash cap is what bounds flight 2. The floor bounds the POST-FIX RESIDUAL - `coast_native_warp_hold` removed the cancel half of the cycle, so the command now stays armed continuously, while flight 2's other half (a rails rate that never escaped 2.76x) is untouched by that fix. It also does NOT read the `warpUtilisation` row: it judges an EPISODE-LOCAL ratio computed in the fly loop from the arm frame. On flight 2 that reads 1.41 while the PHASE row reads ~39 (see the metric note below), and only the episode number can name the defect. Mechanism covered by `test_shells.WarpLivenessRealMachineTests`, which drives the real b5 machine over flight 2's post-fix telemetry; never fired in the field, and reaching it there would mean reintroducing the defect.
- **The floor's terminal now tears the warp down, and the old rationale for not doing so was false (2026-07-26 review round 2).** The give-up shipped with a comment claiming "nothing drives the game afterwards: run_mission evaluates, closes, and run.py kills the process for the retry". It does: `hlib.plan_unmet_mission_tail` drives the TAIL_ROLE_CLEANUP verbs (`StopRecording`, `FlushAndQuit`) after ANY unmet mission, and MISSION-FLAKE maps to the `autopilot-flake` subkind exactly like an ASSERT-FAIL - observed live as `mission UNMET verdict=... driving cleanup [0006:StopRecording, 0008:FlushAndQuit]`. Since this is the ONE fly-loop terminal that can only fire while a warp is armed, it was also the one guaranteed to hand the seam a rails-warping game, which is exactly what `mlib._b5_stop_all_warp` exists to prevent on the machine side. The terminal now performs `cancel_warp` inline, wrapped in a bare `except` so a dying connection can never convert the NAMED give-up into a generic post-connect drop, and the returned state clears `warp_to_cmd` / `warp_cmd`. Pinned by `test_shells.WarpLivenessRealMachineTests.test_the_floor_terminal_tears_the_warp_down_before_returning` plus a cell that raises from the cancel and asserts the named verdict survives.
- **KNOWN RESIDUAL: the other two fly-loop terminals still return without a warp teardown.** The wall-budget reaper and the unexpected-warp flake (`_fly_loop_body`) both return a done state without cancelling, and the unexpected-warp flake fires with a warp active BY DEFINITION. They are untouched here deliberately - they sit on the live-proven B1/B2/B4/B5/B6/B7 lanes and this PR is the bottom of a three-PR stack - but they are the same hazard and should get the same guarded cancel. Not urgent: both are already terminal INVALID attempts whose save is discarded, and the cost is a warping game handed to `FlushAndQuit`.
- **A THIRD site of the same class, on the machine side this time, fixed 2026-07-28 - and it was TWO sites, not one.** The B5-family PARK branch builds its 1x self-heal teardown into a local `warp_actions` list AND mutates the state (`warp_to_cmd=None`, `warp_cmd=0`) to record it, then both `_b5_over_budget` give-ups returned `[]` - shipping a state that CLAIMED the warp was down while no cancel ever reached the runner. A PARK that times out on the same frame a stray native or rails warp is detected therefore flaked with the game still warping, and `hlib.plan_unmet_mission_tail` drives the CLEANUP tail next, which is the exact hazard `_b5_stop_all_warp` exists to prevent. (Note the verb list: after an unmet mission that planner drives `StopRecording` / `FlushAndQuit` ONLY - `CommitTree` is `TAIL_ROLE_WORLD_MUTATING` and is skipped, per `SKIP_TAIL_ON_UNMET_MISSION_DEFAULT`. The "StopRecording / CommitTree / FlushAndQuit" phrasing describes the PRE-2026-07-24 tail and should not be copied forward again; it was in the first draft of this entry.) **`B5_LANDED_SETTLE` (B13/B14) is a verbatim copy of that branch and had inherited the bug identically** - found by sweeping for the pattern instead of fixing only the reported line, which is the whole lesson: the copy was made AFTER the PARK block existed, so the defect propagated with it. Both give-ups in BOTH branches now return `warp_actions`. The local block is kept in both rather than routed through `_b5_stop_all_warp`, because it is strictly stronger (it also drops a rails factor the game reports via `snapshot.warp_mode == WARP_RAILS` when `warp_cmd` is already 0, which `_b5_stop_all_warp` does not). The settled-1x dwell that both branches almost always fire on builds an EMPTY list, so the common case is byte-identical. Pinned by six cells - `test_mlib.B5ParkTests` and `test_mlib.LandedSettleTests` each get the native-warp give-up, the rails give-up, and a negative control that a non-warping dwell still times out silently. All four positive cells were confirmed to FAIL against the pre-fix machine.
- **KNOWN RESIDUAL (widened 2026-07-28 by the sweep above; NOT fixed here).** The same audit enumerated the terminals in `b5_decide` that return without a warp teardown but whose state stays HONEST (they do not claim a teardown they did not emit) - a milder class than the two fixed above, since the bug there was the LIE, not the missing cancel. Each candidate was then put to independent refutation, and only these SURVIVED: `_b5_left_target_soi` and its 7 call sites (two of which, PLAN-CAPTURE and CAPTURE-BURN, ride a deliberate `planWarpFactor` rails hold); the COAST-ejected-from-home ASSERT-FAIL; TWO of the five CAPTURE-BURN give-ups (`capture-executor-not-enabled` and the phase-budget flake - the other three were not sustained); `_b5_plan_phase`'s budget-expiry terminal; and, in a non-warp variant of the same shape, the CORRECTION-BURN post-throttle budget expiry, which returns `[]` instead of routing through `_corr_exit` and so ends with the engine still throttled and nodes uncleared. Also noted, the INVERSE divergence: the CORRECTION-BURN pre-burn flip emits `SET_PHYSICS_WARP 0` while leaving `phys_warp_cmd` set, so the machine-state line misreports a warp the machine just dropped. All are left alone deliberately - they sit on the live-proven B5/B6/B7 lanes, this change is a targeted fix to a self-inconsistency, and widening it would broaden the risk surface across flown missions. They want the same guarded teardown as a deliberate, separately-flown change.
- **REFUTED, recorded so it is not re-found (2026-07-28).** `_b5_hold_blank_body`'s vessel-lost terminal LOOKS like the worst case of the residual class - the helper's whole contract is to HOLD an armed warp across blank-body frames, so reaching its `frozen_sample_limit` terminal with a warp running seems guaranteed by construction. It is UNREACHABLE in production: a blank `snapshot.body` has exactly one producer, `mission_runner`'s read-fail escalation, and that snapshot also carries `vessel_lost=True`, which `b5_decide` terminates on at the top of the function ~500 lines before either `_b5_hold_blank_body` call site. The live read path sets `body` from a kRPC celestial body inside a single try/except, so there is no partial-read frame with a blank body and otherwise-live fields. Do not re-open it without first showing a producer of `body == "" and vessel_lost == False`. Two further candidates were left CONTESTED rather than confirmed: the TARGET-FLYBY off-course ASSERT-FAIL (the refutation turns on the MET-vs-UNMET tail plan, since the neighbouring RETURN exit it was compared against is a `verdict=None` MISSION-OK terminal that continues into the settle tail) and `_b5_plan_phase`'s attempt-cap terminal.
- **The episode reset has a cell now (same review, BLOCKER-2).** `mission_runner._fly_loop_body`'s `if not armed: wl_wall_start = None; wl_ut_start = None` is the single line that makes the ratio EPISODE-local instead of cumulative, and replacing it with `pass` survived the entire mission suite. It matters concretely: nine archived PARK rows run 180.2-180.6 wall-seconds at ratio 0.999, so a stale baseline carried across a deliberate 1x hold bills that hold against the ratio and the floor false-fires on a healthy park. `test_a_healthy_rearmed_warp_is_judged_on_its_own_episode_not_the_1x_hold` drives the REAL b5 coast machine through arm -> arrival-disarm -> 600 wall-seconds of 1x -> re-arm-and-warp and fails when that line becomes `pass`; `test_the_rearmed_episode_is_still_judged_after_the_reset` is the negative control that the reset restarts the window rather than disarming the guard.

**The warp-utilisation metric (the queued telemetry task's cheap slice, emitted here).** The mission result now carries a per-phase `warpUtilisation` block - `{phase, wallSeconds, gameSeconds, gameSecondsPerWallSecond, warpCommands}` - built by the pure `mlib.warp_utilisation_row` and accumulated in the runner's fly loop. `gameSecondsPerWallSecond` IS the diagnosis: a warping coast reads hundreds to thousands, flight 2's thrashing coast reads ~40 while issuing 3,603 warp commands. That ~40 is a PER-PHASE average and it is correct as such - the phase covered 151,763 game seconds in ~3,890 wall seconds - but note what dominates the mean: one successful warp burst (146,070 game seconds in 7 frames) preceding the thrash inside the same phase. The thrashing EPISODE itself reads 1.41. Anything judging a warp episode must compute its own episode-local span, which is exactly what `warp_liveness_starved` does; fed this ~40 it would be silent on the defect. The block is ADDITIVE ON EVERY FLOWN RESULT (an earlier note here claimed it was omitted when no rows accumulated, so pre-existing results stayed byte-identical - that was wrong: the runner passes the rows unconditionally and any mission entering the fly loop closes at least one row). The RICHER version (per-warp-mode segments, whole-run wall attribution, and a `warp_audit.py` utilisation rule to replace its contiguous-1x heuristic) still belongs to the mission time-accounting task.

**Blast radius (honest).** The change alters the shared coast decision on exactly one frame class: a blind `time_to_soi` while the game is warping with a native warp armed. B11 flight 2 never took that frame (0 cancels in its coast), so its FULL PASS profile is unaffected. B5/B7 have no logs on hand to prove the same, but the change can only stop a valid warp being thrown away - it never makes a coast slower or lets one warp past a boundary (the target is still `soi_arrival - soi_lead`, and arrival still hands back at 1x). One pre-existing cell (`test_coast_rails_intent_cancels_active_native_warp_first`) constructed exactly the flipped frame; it now exercises the blind-and-NOT-warping frame, and the new `test_coast_blind_soi_read_under_warp_holds_the_command` covers the flipped one.

**CONFIRMED by flight 3 (2026-07-25), spectacularly.** COAST-TO-TARGET went from never-finishing to **26 wall seconds** for 194,704 game seconds (ratio 7,543) on **3** warp commands, and the mission reached a capture burn for the first time. The new `warpUtilisation` block earned its keep immediately: it named the NEXT defect at a glance (TARGET-FLYBY at ratio 5,341, see the next entry). `phaseWarpIssues` behaved.

**Predicted re-fly signature (for the record, observed).** COAST-TO-TARGET after round 2 (ut ~74,228): ONE `action warp_to_ut 267644.669`, `coastWarpIssues=1`, ZERO `cancel_warp`, rails climbing past 1000x toward the 100,000x tier that is legal at those Kerbin altitudes, and the coast's ~193,400 game seconds passing in the low hundreds of wall seconds. Then TARGET-FLYBY in the Minmus SOI, PLAN-CAPTURE, CAPTURE-BURN (MechJeb's ~600 s 1x pre-ignition hold, `nodeExec=1`, no flake), the capture burn flipping the apoapsis positive, PARK, ORBIT-COMMIT, ORBIT-COMMITTED. The result's `warpUtilisation` block should show COAST-TO-TARGET at hundreds of game-seconds per wall-second with `warpCommands` in single digits.

### B12 flight 3 (2026-07-25) - TARGET-FLYBY warped straight through periapsis [FIXED in the SHARED machine, branch `autotest-orbit-missions`]

**The third shared B5/B6/B7 defect this lane has surfaced, and the first one whose fix CHANGES a live-proven mission's profile (B11 owed a confirmation re-fly - PAID on flight 3, FULL PASS).**

**What flew.** The coast fix worked: the mission cleared both correction rounds, crossed to Minmus and reached a CAPTURE-BURN. It then failed the capture window with `capture under-burn (executor wedged with the node still pending; ap=324973 pe=5267 ecc=0.710 is not a bound orbit inside [pe>=10000, ap<=1500000, ecc<=0.25])` - a bound but wildly eccentric 325 x 5.3 km orbit that grazes Minmus (radius 60 km). **The machine did not lie:** the bound-orbit gate did exactly its job and rejected it.

**The metric found it in one glance.** The `warpUtilisation` block added the previous day:

| phase | wall | game | game/wall | warpCmds |
|---|---|---|---|---|
| CORRECTION-BURN | 40 s | 73,723 s | 1,834 | 1 |
| COAST-TO-TARGET | 26 s | 194,704 s | 7,543 | 3 |
| **TARGET-FLYBY** | **2 s** | **8,213 s** | **5,341** | **2** |
| CAPTURE-BURN | 138 s | 152 s | 1 | 0 |

A flyby phase warping 8,213 game seconds at 5,341x is the whole diagnosis on one line.

**The evidence.** The entire TARGET-FLYBY phase was FOUR polls:

```
phase COAST-TO-TARGET -> TARGET-FLYBY ut=268934.528 alt=1902523.981 ap=-223466.427 vsurf=-236.331
gate captureArmStreak 0->1 | ut=272841.058 alt=976633.488  warp=RAILSx10000.000
action set_rails_warp value=5.000
gate captureArmStreak 1->2 | ut=276530.313 alt=98531.240   warp=RAILSx5860.077
action set_rails_warp value=5.000
phase TARGET-FLYBY -> PLAN-CAPTURE     ut=277147.541 alt=41609 vsurf=+92   (CLIMBING)
```

The FIRST poll after entry advanced **3,907 game seconds** on its own; the second advanced 3,689 more while the commanded factor-5 rails was still ramping down from 10,000x. By the time the 3-frame arming debounce completed, periapsis was behind us.

**ROOT CAUSE, two compounding parts, both in the shared machine.**

1. **The COAST -> TARGET-FLYBY handoff emitted no warp cleanup.** `if snapshot.body == state.params.target_body: return _b5_enter(state, B5_TARGET_FLYBY, snapshot.ut, peak), []` - so the craft crossed the SOI boundary still running the coast's native warp (the fixed coast now legitimately reaches RAILSx10000, which made this latent hazard fatal).
2. **Capture mode had no periapsis bound at all.** With the SOI-EXIT native warp suppressed (`not state.params.capture_enabled`), the branch fell through to the rails flyby stair:

```python
pe_ref = (max(snapshot.periapsis, 0.0) if _is_finite(snapshot.periapsis) else 0.0)
stair = rails_factor_for_distance(snapshot.altitude - pe_ref, snapshot.vertical_speed,
                                  state.params.flyby_max_warp_factor)
desired = min(max(state.params.flyby_warp_factor, stair),
              max_legal_rails_factor(snapshot.body, snapshot.altitude))
```

That is an ALTITUDE-DISTANCE stair with a factor FLOOR (`max(flyby_warp_factor, stair)`), and nothing in it consults the periapsis CLOCK. At the rates a cross-SOI arrival carries, and around a small body whose warp-altitude limits permit high rates close in, it cannot brake in time - and the floor forbids it from reaching 1x even if it wanted to.

**Why B11 (Mun) passed - forgiving geometry, not a different code path.** B11 flight 2 entered TARGET-FLYBY at ut 16,411.0 and reached PLAN-CAPTURE at ut 16,497.8: **87 game seconds**, because its coast handed over at a modest rate rather than 10,000x, so the 3-frame arming debounce cost almost nothing. Same code, same hazard, luckier entry state. That is exactly the question worth asking of every "it passed" - and the answer here was "by luck".

**The fix.**
- `mlib.capture_flyby_warp_target` (pure) - inside the target SOI in capture mode the ONLY legitimate warp target is `periapsis_ut - CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS`, computed from the ORBIT's own clock (`Orbit.TimeToPeriapsis`, surface-verified against the installed krpc 0.5.4 client, opt-in `read_periapsis` so every other mission's snapshot stays byte-identical). Past the bound, or with the clock unreadable, it returns None and the machine does NOT warp at all - fail closed, 1x is slow but correct and the flyby budget bounds it. This REPLACES the rails stair in capture mode.
- The lead is **900 s**, sized to cover in order: our 3-frame arming debounce + the PLAN-CAPTURE RPC (tens of game seconds on the 10x plan hold), MechJeb's ignition lead (`_ignitionUT = node.UT - halfBurnTime`, ~10-60 s for this burn class) and MechJeb's own 600 s pre-ignition WARPALIGN hold. The asymmetry is deliberate: stopping early costs a little low-warp coast that MechJeb's executor autowarp then flies, stopping late loses the pass outright.
- The COAST -> TARGET-FLYBY handoff STOPS the inherited warp on the transition frame in capture mode (cancel a native warp, else drop a held rails factor). Flyby missions keep the byte-identical no-action handoff - passing periapsis IS the point for B5/B6/B7.
- The arrived-late backstop from flight 1 (re-plan once, then `capture-window-missed`) is unchanged and stays the genuine exception path it was meant to be.

**Blast radius.** Both changes are gated on `capture_enabled`, which is False for B5/B6/B7, and the new snapshot field is opt-in, so the flyby family is byte-identical (a cell asserts the unchanged handoff). **B11 IS affected and owed a confirmation re-fly - PAID 2026-07-25 (flight 3, FULL PASS):** its TARGET-FLYBY now warps to `periapsis_ut - 900` instead of riding the stair, so PLAN-CAPTURE moves from ~87 game seconds after SOI entry to ~900 s before periapsis. The predicted outcome held exactly: same capture eccentricity (0.000127, digit for digit), all six assertions met, and TARGET-FLYBY down from 8,213 to 27 game seconds. Wall came out 1,269 s vs flight 2's 1,268 - the approach warp we took over from MechJeb's executor autowarp is a wash on wall cost, not the drop predicted, which is worth remembering before pricing a warp change as a speedup.

**On MechJeb's pre-ignition hold (CAPTURE-BURN 138 wall / 152 game, ratio 1).** Not a bug, and deliberately NOT fought. The hold ends when `AlignedAndSettled()` is true, which is `Aligned()` (angle < 1 deg) AND `Core.vessel.angularVelocity.magnitude < 0.001` rad/s - and that angular velocity is driven by MechJeb's OWN attitude controller. Nothing we can do makes the craft settle faster without taking attitude control away from the executor, which is precisely fighting it. The one clean, non-fighting lever is enabling RCS so MechJeb's controller has finer authority to null the residual rate (B11 flight 1 sat at 0.003 rad/s, 3x the threshold), but that costs monopropellant and can only be proven by a live A/B - it is a dedicated experiment, not a change to make on speculation. What the periapsis bound DOES guarantee is that the hold is now deterministically at most ~600 game seconds instead of an open-ended cost.

**CONFIRMED by flight 4 (2026-07-25): FULL PASS, all six assertions met, wall 580 s.** Capture eccentricity **0.00026** - a clean circular Minmus orbit - then PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED. Both of this lane's shared-machine warp fixes held on the same flight: COAST-TO-TARGET flew **194,543 game seconds in 26 wall seconds** (ratio **7,535**) on **3** warp commands, and the periapsis-bounded TARGET-FLYBY armed the capture on the orbit's own clock instead of blowing through it. At 580 s wall B12 is now the CHEAPEST of the two orbit cases (B11 costs 1,269 s), which makes the Minmus axis the better default for a fast regression check of the shared capture machine. B11 flew its owed confirmation on the same changed profile and also passed, so the lane's re-fly debt is clear on both axes.

**Expected re-fly signature (B12 flight 4) - OBSERVED, flight 4 was a FULL PASS.** TARGET-FLYBY entered just inside the Minmus SOI: ONE `action cancel_warp` on the transition frame, then a single `action warp_to_ut` at `periapsis_ut - 900` and `ttPe=` visible on the telemetry line. The phase's `warpUtilisation` row should read a HIGH ratio with 1-2 warp commands but END ~900 game seconds before periapsis with the craft still DESCENDING (vsurf negative). Then the arming debounce at low warp, PLAN-CAPTURE with a healthy periapsis clock, CAPTURE-BURN with `nodeExec=1` and MechJeb's ~600 s hold, the burn flipping the apoapsis into the capture window, PARK, ORBIT-COMMIT, ORBIT-COMMITTED. `captureReplans=0` (the arrived-late path should now be the exception it was designed to be).

## B-DOCK station + interceptor dock / transfer / undock (bdock_dock_transfer) [BUILT, pending forge run + first flight, branch `autotest-bdock-impl`; design `docs/dev/design-autotest-bdock-missions.md`]

The FIRST two-vessel Parsek autotest and the entry point to logistics-route recording verification: a pre-placed Station flies the B2 ascent to a ~110 km park and COMMITS its tree mid-mission via the command seam, then the SAME docking Kerbal X launches again as an Interceptor into a ~90 km phasing orbit, MechJeb rendezvous closes, MechJeb docking hard-docks, two kRPC `ResourceTransfer`s move LiquidFuel one way and MonoPropellant the other, an undock splits the pair, and both trees commit. It exercises the cross-tree Dock branch, the authoritative `onVesselsUndocking` split, and the `RouteConnectionWindow` whose recorded resource deltas are checked against the commanded transfers.

Built per the design (implementation-PR decisions taken as recommended):
- **Machine (mlib):** a NEW `bdock_decide` / `BDockParams` / `BDockState` / `BDOCK_*` phase enum (18 phases, incl. the two stage-separation phases below) in `mlib.py`, NOT a B5 extension - its transitions key on target distance, relative speed, docking-port state, transfer completion, and the two-vessel launch sequence (a mostly-disjoint branch set). Reuses the shared ascent actions + connect/warp/frozen/give-up/result infrastructure. Ten new `ACTION_*` (launch_vessel, capture_station, parsek_commit_tree, set_target_vessel/docking_port, mj_enable_rendezvous/kill_rel_vel/enable_docking/disable_docking, start_resource_transfer, undock) + eleven new fail-closed `TelemetrySnapshot` fields (target_distance/rel_speed, docking_state, target_set, mj_rendezvous/docking_enabled, vessel_count, transfer_complete/amount, monopropellant, seam_commit_result). The pre-B-DOCK suites pass unmodified (the fail-closed-defaults proof).
- **Seam bridge (route 1):** the mid-mission Station commit is `ACTION_PARSEK_COMMIT_TREE` -> `KrpcMissionControl` writes a `CommitTree` command with a RESERVED command-id (the mission step's own `step_id`, which run.py threads in via `--seam-commands`/`--seam-responses`/`--seam-commit-id`; the mission step consumes an index but writes nothing to the channel, so its id is free + collision-safe + monotonic) and BOUNDED-polls the response channel; OK -> `seam_commit_result="OK"` (machine advances), ERROR/TIMEOUT flakes (driver-INVALID, retryable, never PARSEK-FAIL).
- **Runner:** the new action cases + the opt-in docking telemetry reads (`read_docking=True`, so B1/B2/B4/B5/B7 read_snapshot is byte-identical) + handle-caching (P9/Q4: the Station vessel + top docking port + per-resource tanks captured while reachable; never name/pid) + `configure_seam`.
- **Give-ups (section 5.3):** per-phase game budgets, a rendezvous no-progress detector, a docking monoprop-out give-up (P2), a transfer-stall bound, and the shared frozen/vessel-lost terminal (excluded during the INT-LAUNCH reload where a vessel_lost is a transient).
- **Delta observability (MAJOR 6, C# side):** `RouteProofCapture` now emits an Info `Route window delta:` line at undock completion with the per-resource `undockTransport-dockTransport` net (+ endpoint mirror) - the ONLY observable surface the offline oracle checks the commanded transfers against; the pure `FormatRouteResourceDelta` helper is xUnit-covered.
- **Spec:** `BDOCK-1-station-interceptor.toml`, tier `pending-fixture` (the fixture is forgeable-but-not-yet-forged), `recordings.count = {min=2, max=20}` (two trees, MINOR 13), coverage cells exactly as revised (D5 cross-tree-foreign-dock/undock-split/bg-recording, D7 dock-undock/rcs, D8 route, D10 candidate-detection/ksc-origin/dock-producer/delivery/pickup/mixed-direction/resource-cargo), logContracts asserting the recording lifecycle + the Dock/Undock branch lines + the new delta line.

**FIXTURE-FORGE (the new headless-state-generation capability; 2026-07-22 operator-principle override REPLACING the operator fixture flight):** `forge_station.py` + a tiny `forge_decide` / `ForgeParams` / `ForgeState` machine (argued the simplest correct shape: a two-phase mlib machine reusing the shared runner, generic over craftName + crew so it later forges the EVA-3 pad fixture too) boots the committed bootable base (`bdock-forge-base` = a gloops-airshow flight state + the docking `Kerbal X.craft` in Ships/VAB, so LoadGame passes on its active vessel), launches the craft onto the pad via `launch_vessel`, settles PRELAUNCH, and exits MISSION-OK; the scenario's post-mission SaveGame + FlushAndQuit persist it. `harness/tools/harvest_bdock_station.py` copies the produced save, prunes Parsek state, normalizes the title, and writes `harness/fixtures/saves/bdock-station-pad/`. Spec `FORGE-bdock-station.toml`, tier `operator` (argued: reuse the existing non-cadence readiness tier rather than add a `forge` tier - it NEVER runs on a cadence, no hlib change).

**REMAINING to first flight (in order):** (1) run `FORGE-bdock-station` on the provisioned instance (`python harness/run.py --id FORGE-bdock-station`), (2) `python harness/tools/harvest_bdock_station.py --instance <ksp-instance>` + commit the produced `bdock-station-pad` fixture, (3) re-tier `BDOCK-1` from `pending-fixture` to `nightly` and run the first live flight (retry-once). LIVE-PROVE items P1-P9 (rendezvous/dock stage-dv margin, monoprop budget, the pre-placed fixture booting into flight, Station-tree survival across the launch reload, the same-craft pid outcome, rendezvous-AP robustness, launch_vessel auto-record, session length, handle survival across the merge) are all first-flight questions; every numeric budget is ESTIMATED arithmetic and re-timed against the first run.

**Flight-3 finding + fix (2026-07-24): docked a full stack.** ~~Flight 3 reached MATCH-VELOCITY with BOTH vessels still FULL STACKS - the spent lifter never separated (MechJeb autostage only fires on EMPTY stages, and the Kerbal X core keeps residual fuel after circularize, so nothing autostaged), and docking a ~20 t full stack on pod RCS is broken.~~ FIXED (branch `autotest-bdock-flight`): two explicit post-circularize stage-separation phases, `STATION-SEPARATE` (STATION-CIRCULARIZE -> STATION-ORBIT) and `INT-SEPARATE` (INT-CIRCULARIZE -> INT-PHASING-ORBIT), each entry-emitting EXACTLY ONE `ACTION_ACTIVATE_STAGE` and completing on a `vessel_count` increase past the phase-entry baseline (the spent core spawns as a NEW vessel) debounced `BDOCK_SEPARATION_DEBOUNCE` (=`DEFAULT_DEBOUNCE_K`=3) frames; fail-closed on an unread count (default 0), bounded give-up `separationTimeoutSeconds` (default 120) -> a named FLAKE ("no separation observed (vessel_count did not increase)"), retryable. EXACTLY ONE activation, never a loop, because the OTHER Kerbal X `Decoupler.2` jettisons the pod's HEAT SHIELD; the orbital-stage thrust sanity (`available_thrust > 0`) is logged on the transition line, not hard-gated (NaN fails closed + the read can transiently fail). New `stationSeparated` / `interceptorSeparated` result assertions (met = the SEPARATE phase entered AND advanced to its park); the machine is 18 phases. GENERAL PRINCIPLE (operator directive, now in the design doc + the spec's MISSION PROFILE header): per-phase vehicle-configuration contracts are part of every mission design - each stage's configuration must make sense for that part of the mission, and the full ordered step list (incl. configuration transitions) is written down.

**Flight-4 follow-up (2026-07-24): separated but never ignited.** ~~Flight 4 flew the SEPARATE phases: separation WORKED (vessel_count evidence fired, both cores shed), but the mission flaked in RENDEZVOUS with nodeDv=17.038 stuck and avThr=0.000 for 38+ no-progress frames - the orbital stage engine was NEVER IGNITED. The exactly-one-activation contract dropped the core but left the engine unlit, because the orbital LV-T45 sits in a LATER stage than the separation decoupler (craft istg: separation `Decoupler.2`=2, `LV-T45`=1 in its own later stage, heat-shield `Decoupler.2`=0 and MUST never fire).~~ FIXED (branch `autotest-bdock-flight`): each SEPARATE phase is now an evidence-chained TWO-step contract. STEP 1 (drop core): the entry `ACTION_ACTIVATE_STAGE` drops the spent core, confirmed on the debounced `vessel_count` increase (as before). STEP 2 (ignite orbital engine): AFTER the split, if `available_thrust` is not already debounced-positive, emit EXACTLY ONE more `ACTION_ACTIVATE_STAGE` to light the orbital stage; completion = `available_thrust > 0` debounced K frames. HARD CAP: at most 2 activations per SEPARATE phase (`separate_activations`), so a third never fires the istg=0 heat-shield decoupler; a craft whose decoupler + engine share a stage completes with NO second activation (thrust already up). Fail-closed: NaN `available_thrust` is never treated as ignited. The give-up now distinguishes "no separation observed (vessel_count did not increase)" from "separated but no ignition (available_thrust stayed 0)". New state fields `separate_thrust_streak` / `separate_split_confirmed` / `separate_activations`. ~~Still pending flight 5 to confirm the orbital engine lights and the orbital stages dock.~~ Flight 5 CONFIRMED: two-step SEPARATE worked (avThr=250 kN, engine burns, RENDEZVOUS completed and latched off within approach distance).

**Flight-5 follow-up (2026-07-24): MATCH-VELOCITY ate the wall.** ~~Flight 5 entered MATCH-VELOCITY at ut 8668 and sat ~4300 GAME seconds until the 4800 wall killed the mission with nodeDv=17.038 stuck. Two root causes: (1) the phase had NO game-time budget (the comment even said "fast transitions") and NO per-frame diagnostics, so an unmet gate silently ate the whole wall; (2) the runner's `ACTION_MJ_KILL_REL_VEL` used `operation_kill_rel_vel` with the DEFAULT time selector = closest approach, which -- after the rendezvous AP has already closed to ~approach distance -- lands the node nearly a full orbit ahead, so no burn happens.~~ FIXED (branch `autotest-bdock-flight`): **Runner** -- before `make_nodes`, `control.remove_nodes()` (no stale-node stacking on a re-issue) then retarget the op to `TimeReference.XFromNow` + `LeadTime` ~15 s (KRPC.MechJeb `TimedOperation.TimeSelector` -> python `op.time_selector.time_reference = mech_jeb.TimeReference.x_from_now` / `.lead_time = 15.0`, discovered from the pinned `krpc_mechjeb-src` `Maneuver/TimeSelector.cs`; defensive try/except falls back to the default selector on any AttributeError). **Machine** -- new `matchTimeoutSeconds` param (default 600 GAME s) wired into `_bdock_phase_budget` for `BDOCK_MATCH_VELOCITY` -> a named FLAKE "match-velocity did not reach rel-speed floor (target_rel_speed=<last>)"; a per-frame rate-limited `match-velocity` diagnostic line (target_distance / target_rel_speed / node_count / node_dv) + `targetDistance` / `targetRelSpeed` added to the status-file snapshot block; and a one-shot dropped-target re-acquire (non-finite `target_rel_speed` for K debounced frames -> re-emit `ACTION_SET_TARGET` once via the `match_retarget_done` latch; NaN never completes the phase, fail-closed). ~~Still pending flight 6 to confirm the burn fires promptly and DOCK is reached.~~ Flights 6-8 confirmed MATCH-VELOCITY now completes fast and DOCK is reached with a perfect setup.

**Flight-8 follow-up (2026-07-24): pending node NRE'd the docking AP.** ~~Flight 8 entered DOCK with a PERFECT setup (tgtD=92.179 m, tgtV=0.017 m/s - the port-target telemetry fallback works) and then flaked at the 1800 s dock budget WITHOUT docking. Smoking gun: `MechJebModuleDockingAutopilot` threw NullReferenceException in Drive AND UpdateDistance every tick for ~28 minutes. Causal chain: MATCH-VELOCITY now completes in ~0.5 s (rel-speed already under the floor), so the kill-rel-vel NODE (planned XFromNow+15 s) was still PENDING in MechJeb's node executor with autowarp=True when DOCK enabled the docking AP; the executor rails-warped to the node at ~92 m from the Station, and PACKING CLEARS DOCKING-PORT TARGETS in stock KSP -> target null -> docking AP NREs forever (window dump: nodes=1 nodeDv=0.025 leftover at entry, later nodes=0 + tgtD=nan).~~ FIXED (branch `autotest-bdock-flight`): the prox-ops variant of the operator's mission-profile rule -- NEVER leave maneuver execution armed during terminal approach. New runner action `ACTION_MJ_ABORT_NODE_EXEC` (`node_executor.abort()` + `control.remove_nodes()`, best-effort Warn-style) emitted by the MACHINE as the FIRST DOCK-entry action, BEFORE `set_target_docking_port` + `mj_enable_docking`, so no pending node / executor / autowarp survives into terminal approach. Robustness: a DOCK-phase dropped-target recovery mirroring `matchRetarget` -- while `docking_ever_enabled`, a non-finite `target_distance` for K debounced frames re-emits `ACTION_SET_TARGET_DOCKING_PORT` EXACTLY ONCE (`dock_retarget_done` latch, `dock_nan_streak` counter); NaN never completes DOCK (the docked gate reads `docking_state`, fail-closed). ~~Still pending flight 9 to confirm the hard-dock completes.~~ Flight 9 confirmed the abort fired but revealed a further bug (below).

**Flight-9 follow-up (2026-07-24): same-batch enable saw the stale target.** ~~Flight 9's abort-first fix worked (abort fired at DOCK entry) but the docking AP STILL died instantly: `MechJebModuleDockingAutopilot` NRE'd in Drive + UpdateDistance WITHIN ONE FRAME of DOCK entry (ut 8687.6 vs entry 8688.2), exactly 2 NRE lines then silence (MechJeb benched the module), ship sat to the 1800 s budget. Cause: `ACTION_SET_TARGET_DOCKING_PORT` and `ACTION_MJ_ENABLE_DOCKING` were in the SAME action batch, but MechJeb's `core.target` only syncs from the KSP-level target on its NEXT Update, so the AP's first Drive tick saw the OLD VESSEL target (set back in SET-TARGET), cast it to a docking node, and NRE'd. Flight 8's tgtD=92 at entry was the vessel-target fallback reading, which masked that the port target never took.~~ FIXED (branch `autotest-bdock-flight`): the docking-AP enable is STAGGERED one poll after the port target is set. New `BDockState.dock_enable_pending`: the DOCK-entry batch is now `ACTION_MJ_ABORT_NODE_EXEC` + `ACTION_SET_TARGET_DOCKING_PORT` only (arming `dock_enable_pending=True`); the DOCK phase's first step emits `ACTION_MJ_ENABLE_DOCKING` alone on the next poll (~0.5 s, ample Unity frames for `core.target` to sync) and clears the flag. The dropped-target recovery uses the same stagger (set port + re-arm pending; enable next poll). Test-harness hardening: `FakeMissionControl` no longer repeats the terminal frame unboundedly (an under-fed script used to spin ~90M fly-loop iterations under the 0.001 s FakeClock before the wall tripped); after `max_last_repeats` (256, far above any real settle tail) it raises `EOFError`, a transport-drop name the fly loop re-raises, so an under-fed script fails fast. ~~Still pending flight 10 to confirm the hard-dock completes.~~ Flight 10 confirmed the stagger worked (abort -> set_target -> pending -> enable next poll) but exposed the deeper defect (below).

**Flight-10/11 follow-up (2026-07-24): prox-ops observability + attitude + LIVENESS.** ~~Flight 10: the operator watched the upper stage TUMBLE in orbit for ~28 min doing nothing useful; 1525 s into DOCK targetDistance was None again and the docking AP was dead, yet the machine sat the full 1800 s budget, and the harness would retry the identical 75-min mission into the same wall. Root defect: the machine had budgets (bound SLOW) but NO liveness detector (bound BROKEN), and telemetry could not even see a tumble / SAS / RCS / AP-status.~~ FIXED (branch `autotest-bdock-flight`), three layers:
- **Observability (A):** new docking-gated `TelemetrySnapshot` fields `angular_velocity` (rad/s, the tumble signal), `sas_enabled`, `rcs_enabled`, `docking_ap_status` (KRPC.MechJeb `DockingAutopilot.Status`); all fail-closed (NaN / False / "") and only read when `read_docking`, so B1-B7 snapshots are byte-identical. Surfaced in `snapshot_dict` (status file), `format_snapshot_compact` (ring dumps), and a per-frame rate-limited DOCK diagnostic line (`dock dist=.. relSpd=.. dockState=.. angVel=.. sas=.. rcs=.. apStatus=..`) mirroring the MATCH-VELOCITY line -- DOCK previously logged NOTHING between entry and the give-up dump.
- **Attitude (B):** new runner actions `ACTION_SET_SAS` (control.sas + stability_assist) / `ACTION_SET_RCS`; the machine emits SAS+RCS-on after EACH SEPARATE completes and at DOCK entry, so separation torque never tumbles the orbital stage and the AP is handed a stabilized ship.
- **LIVENESS (E, centerpiece):** principle -- *budgets bound SLOW; liveness watchdogs bound BROKEN; a phase may never idle to its budget while its actor is provably dead or inert.* DOCK: (E1a) enable-never-took -> re-emit once then fast flake "docking AP enable did not take"; (E1b) died-mid-approach -> re-enable once then fast flake "docking AP disabled without docking (benched/NRE?)"; (E2) progress watchdog -> none of dist/monoprop/angvel improving for `dockNoProgressSeconds` (120) -> fast flake "docking AP enabled but no observable progress". TRANSFER: `transfer_amount` flat for `BDOCK_TRANSFER_STALL_FRAMES` -> fast flake "transfer stalled". The dropped-target retarget is now bounded re-armable (`dock_retarget_count` <= 3, flight-9 one-shot was too stingy). New state fields `dock_retarget_count` / `dock_enable_wait_streak` / `dock_enable_reissued` / `dock_died_streak` / `dock_reenabled_after_death` / `dock_best_*` / `dock_last_progress_ut` / `transfer_best_amount` / `transfer_noprogress_streak`. ~~Still pending flight 11 (telemetry + watchdogs together) to confirm the hard-dock completes or a fast diagnosable flake.~~ Flights 11-13 confirmed the liveness layer works (flight 13 fast-flaked in 10 s of DOCK with the named E1a reason, wall 2133 s) AND the telemetry pinned the real root cause (below).

**Flight-13 root cause (2026-07-24): pre-reload PART handles are stale - behind EVERY dock failure since flight 7.** ~~DOCK entry actions all fired (abort, sas, rcs, set_target_docking_port, staggered enable, E1a re-issue) but `mj_docking_enabled` NEVER read True, `dockingApStatus` stayed "", and `targetDistance` went None AT the port set (it was finite through RENDEZVOUS/MATCH via the VESSEL target). Chain: `ACTION_SET_TARGET_DOCKING_PORT` assigned `self._station_port`, a handle captured at STATION-COMMIT BEFORE the `launch_vessel` FLIGHT reload. The reload destroys/recreates every `Part` object; the stale handle resolves server-side to a destroyed `ModuleDockingNode`; KSP's `SetVesselTarget` on a destroyed `ITargetable` silently CLEARS the target (hence tgtD None right after the set, in every flight); MechJeb then refuses to engage the docking AP with no port target (the flight-8/9 benched-NRE and flight-13 "enable never took" were the same null-target family). VESSEL targeting kept working all along (kRPC keys a Vessel by its stable id), which masked it. The captured station TANK handles are stale the same way and would have walled TRANSFER next.~~ FIXED (branch `autotest-bdock-flight`), P9 now ANSWERED (vessel handles survive the reload, PART handles do NOT): (1) `ACTION_SET_TARGET_DOCKING_PORT` resolves the port LIVE from `sc.target_vessel.parts.docking_ports` (first 'ready' via the pure `mlib.pick_ready_port_index`), keeps the captured handle only as a last resort when a live vessel target exists, and never clobbers a working target with a dead handle when there is no target vessel; (2) `_read_docking_state` re-resolves LIVE from the ACTIVE vessel's ports (post-merge it carries both mated ports; any 'docked'/'docking' is authoritative) - the stale handle read "" in every post-reload flight, blinding the DOCK-done + undock gates too; (3) the TRANSFER re-resolves both tanks LIVE by partitioning the merged part tree at the mated docked-port pair (transport = the active-control side), falling back to live-first-two then the captured handle, with a **loud TODO(flight-14)** that the station/transport SIDE assignment (which drives the recorded route-window delta signs the oracle checks) needs live validation - the bounded TRANSFER-stall watchdog covers a mis-resolved-tank stall meanwhile. `ACTION_CAPTURE_STATION` now documents the vessel-survives / parts-die contract. New pure helpers `mlib.pick_ready_port_index` / `normalize_docking_state` (unit-tested). CONFIRMED flights 14-18: five consecutive hard docks; transfers ran and asserted complete on 15-18 (the station/transport SIDE-SIGN validation for the oracle remains the loud TODO(flight-14) in mission_runner.py - the delta signs have not been oracle-checked yet).

**Flight-16 finding + fix (2026-07-24): the FIRST mission-machinery-caught Parsek recording defect - quickload-resume adopted the fresh rollout.** ~~Flight 16 ran MISSION-OK end to end (launch, separate, mid-mission CommitTree, launch_vessel, rendezvous, hard dock, LF 40 + mono 15 transfers, undock, TERMINAL) but the verifier chain went PARSEK-FAIL: analyzer RED, INV4-PARTEVENT-PID x13 on recording d5355cc63dde43a9b07c59595c8b106d (the Station launch, tree 46c56a23). All 13 flagged part events (Mainsail EngineIgnited ut=390.4, solar DeployableExtended ut=571.0, core Decoupled + engine ShroudJettisoned ut=691.8, 9x RCSActivated ut=696.2) are the INTERCEPTOR's early-life sequence carrying its craft-baked pids, recorded into the STATION's recording whose ghost snapshot holds the Station's live (regenerated) pids - zero pid overlap, so every event is playback-unresolvable. Causal chain (KSP.log 13:33:32-13:33:37): (1) the same-session `launch_vessel` FLIGHT->FLIGHT reload arrived with a STALE `vesselSwitchPending` flag (11914 frames old), so ParsekScenario classified it as a QUICKLOAD and stashed the just-committed Station tree as pending-Limbo; (2) `RestoreActiveTreeFromPending`'s wait loop missed on pid (recorded 3620499050 vs live 4183326108) but its SECONDARY NAME fallback matched ("Kerbal X" == "Kerbal X" - the Interceptor launches from the SAME .craft file), (3) the PID remap rebound d5355cc6 onto the fresh rollout (`FreshRollout: captured scene-entry vessel pid=4183326108 NEW_FROM_FILE` had fired seconds earlier, but only the COMMITTED-tree restore consulted it) and `TryBackupSnapshot` overwrote the vessel snapshot with the Interceptor, so the Station recording absorbed the Interceptor's entire mission (explicitEndUT 8952.7) and the save has ONE main tree where the mission flew TWO. The same fingerprint red-ed flights 8 and 11 triage-only.~~ FIXED (branch `autotest-bdock-flight`): new pure `QuickloadResumeMatchGuard` (`Source/Parsek/QuickloadResumeMatchGuard.cs`, xUnit-covered) gates every acceptance in the `RestoreActiveTreeFromPending` match loop: (a) the scene-entry FRESH-ROLLOUT vessel (`RecordingStore.SceneEntryFreshRolloutVesselPid`) is refused outright with an Info log and the tree is left in Limbo - a NEW_FROM_FILE launch is never the stashed recording's vessel; (b) a conclusive `Recording.RecordedVesselGuid` vs live `Vessel.id` mismatch (via `VesselLaunchIdentity.GuidsConclusivelyDiffer`; the craft-baked-persistentId contract) skips the pid/name match for that recording while the EVA-parent walk still matches a parent whose guid agrees; unknown guids stay inconclusive so legacy recordings keep the old fallback. Genuine quickloads are unaffected (same guid survives the save round-trip; fresh-rollout pid is only captured on fresh-launch startups). CONFIRMED flights 17-18 on the guard build: two separate trees, analyzer red=0 both flights; flight 18 was the full seven-verifier PASS.

**PR #1345 review follow-ups (Fable review 2026-07-24, verdict SHIP; pre-merge items applied):** ALL FIVE addressed on the EVA-lane branch `autotest-eva-flight`. (1) ~~unit test for the flight-11 rendezvous no-progress pause~~ DONE: `test_no_progress_paused_while_node_pending` (flat distance with a pending node never flakes across 20 polls >> the 5-frame window) + `test_no_progress_flakes_after_node_consumed` (node consumed then flat flakes at the threshold) in `test_mlib.py` (both vary altitude so only the no-progress watchdog, never the frozen-telemetry detector, can flake). (2) ~~dead config plumbing (launch_site + crew)~~ DONE: `mlib.Action` gained `launch_site` + `crew` fields; the FORGE machine threads `ForgeParams.launch_site` + `crew_names` onto the launch Action, and the runner's `ACTION_LAUNCH_VESSEL` handler threads both into `sc.launch_vessel(..., crew=names)`. CREW CONTRACT decided BY NAME, not count: kRPC 0.5.4 exposes no roster-enumeration API (only `get_kerbal(name)` + `launch_vessel(crew: List[str])`), so a count could not be resolved to names server-side. `ForgeParams.crew` (int) -> `crew_names` (tuple); the forge schema's `[params.crew]` -> `[params.crewNames]` (list). (3) ~~QuickloadResumeMatchGuard.EvaluateCandidate production-dead~~ DONE (documented): the composed verdict is deliberately not on the live path -- `RestoreActiveTreeFromPending` calls the two predicates directly because it interleaves the guid gate with the EVA-parent walk; a doc-comment on `EvaluateCandidate` now states it is kept for unit-test coverage / whole-verdict callers, not dead code. (4) ~~DOCK pending-enable vs docked-gate race~~ DONE: a `docked` short-circuit now sits AHEAD of the `dock_enable_pending` branch in `bdock_decide` -- a docked pair completes straight to TRANSFER (disable + T1) and never re-enables the AP; completing on `docked` alone (not `docked AND latched_off`) also fixes a latent bug where a docked-but-never-latched pair E1a-flaked a won mission. Tests: `test_docked_without_latch_still_advances`, `test_docked_on_pending_enable_poll_does_not_reenable_ap`. (5) ~~STATION-COMMIT flake_reason~~ DONE: a seam ERROR/TIMEOUT flake now carries `flake_reason="phase STATION-COMMIT: tree-commit seam returned <ERROR|TIMEOUT>"` (test `test_station_commit_error_flakes_with_named_reason`).

**EVA lane prep (branch `autotest-eva-flight`), items:**
- ~~**EVA-2 orbital fixture forge landed, live run pending (the LAST EVA fixture gap).**~~ DONE 2026-07-24: the forge RAN (MISSION-OK / PASS, 268 s wall, full PRELAUNCH -> LAUNCH -> ASCENT -> CIRCULARIZE -> SEPARATE -> PARK -> ORBIT profile), the harvest ran with `--expect-situation ORBITING`, the `eva2-lko-crewed` fixture is COMMITTED, and EVA-2-orbital-board flew it to a FULL PASS on its first flight (so it is re-tiered to daily with its count window pinned at 2). Original description: `harness/scenarios/FORGE-eva2-lko.toml` (operator tier) + the new mission `harness/missions/forge_lko.py` (pure machine `mlib.forge_lko_decide`) forge the crewed-LKO fixture EVA-2-orbital-board is waiting on. It is the FIRST orbital forge: the two existing forges drive `forge_station`, whose machine ends on the pad by design. NO new ascent was invented - it boots the SAME `bdock-forge-base`, reuses the FORGE crew-by-name `launch_vessel` entry, then flies the LIVE-PROVEN B-DOCK Interceptor-leg shape with the SAME mlib action builders (`_bdock_ascent_entry_actions`, `_bdock_attitude_hold_actions`) and the SAME two-step separation evidence counter (extracted from `_bdock_separate_step` into the shared pure `mlib.separation_evidence`, behaviour-identical, both machines now call it). What is genuinely new: (a) the PARK phase - throttle cut, nodes cleared, SAS + RCS held, and a HELD stable orbit (accepted situation, both apsides in tolerance, periapsis >= 75 km clear of the atmosphere, tumble <= 0.05 rad/s, debounced K frames for a 60 s dwell) before the SaveGame, so the fixture is a clean START state and not a moment mid-flight; (b) the crew gate - an opt-in `crew_count` telemetry channel (`read_crew=True`, -1 unread sentinel, one extra RPC per poll, every other mission's snapshot byte-identical) that must read >= `minCrew` ON THE PAD before the ascent starts, so a silent `launch_vessel` crew-seeding failure flakes in 300 s instead of stamping an UNCREWED fixture that reds EVA-2 with a confusing `no-crew` ten minutes later; (c) the circularization is handed to `ACTION_MJ_EXECUTE_NODES` rather than the bare circularization action, because that action sets node-executor autowarp EXPLICITLY (B-DOCK flight-12 lesson: the executor's autowarp is shared global state, so an unset one warped on one flight and coasted a whole leg at 1x on the next). `autoRecordOnLaunch` is pinned false in the spec: unlike the pad forges this one STAGES and FLIES, so an auto-record would leave Parsek metadata inside the persisted `.sfs` (the harvest prunes the `Parsek/` sidecar dir but copies the `.sfs` verbatim). HARVEST GENERALIZATION: `harvest_bdock_station.py` never assumed PRELAUNCH (its sanity check is activeVessel + vessel count), but it also could not TELL an orbital stamp from a broken one, so it gained `read_vessel_records` + an OPTIONAL `--expect-situation` gate (fails closed on an unresolvable index or unreadable situation) and now always logs the active vessel's name + situation; omitted, the two pad forges' behaviour is unchanged. The fixture bytes were forged by `python run.py --id FORGE-eva2-lko` + `python harness/tools/harvest_bdock_station.py --instance <instance> --run-save bdock-forge-base --target-name eva2-lko-crewed --expect-situation ORBITING`, and EVA-2 was re-tiered pending-fixture -> daily. FIXTURE DISCLOSURE (recorded in the EVA-2 spec's `[fixture]` block): the stamp is not a lone orbital stage - the SEPARATE phase's 21-part spent core co-orbits ~16.4 m away, inside physics range, so EVA-2 starts with two loaded vessels; harmless today because `ResolveBoardTarget` prefers `lastEvaExitFromPid`, but a future variant that boards without a preceding in-process EvaExit must pass an explicit targetPid. 4 landed booster remnants ~514 km downrange and the stock asteroid never load. The fixture also keeps an inert populated `SCENARIO{name=ParsekScenario}` node (`gameStateEventCount=18` + one MILESTONE_STATE row), so "zero Parsek state" is stated as "no recordings, no trees, no ledger state"; the populated node is what suppresses PreParsekBackup at load.
- ~~**EVA-3 pad fixture forge spec landed, live run pending.**~~ DONE 2026-07-24: the forge ran, the `eva3-pad-3crew` fixture is COMMITTED (the FULL 94-part Kerbal X stack, PRELAUNCH, 3 named crew), and EVA-3 flew it GREEN. The pad-EVA reachability caveat below did NOT materialize and no bare-pod craft is needed: both EVA-3 exits use `release=false`, so the kerbal never drops to the pad and boards straight off the hatch ladder at ~0 m (stack height only matters for a variant that RELEASES, which EVA-1 does from a ground-level single-part pod). The EVA-3 spec's `[fixture]` block was rewritten to describe the actual fixture. Original description: `harness/scenarios/FORGE-eva3-pad.toml` (operator tier) clones FORGE-bdock-station over the same `bdock-forge-base`, launching the Kerbal X with three named crew (`crewNames = ["Valentina Kerman", "Bob Kerman", "Bill Kerman"]`); `harvest_bdock_station.py --target-name eva3-pad-3crew` normalizes it into `fixtures/saves/eva3-pad-3crew` (the name EVA-3-multi-kerbal references). The fixture BYTES are still unforged (needs an operator forge run + harvest). CAVEAT for the live EVA-3 flight: the merged EVA-3 spec's own fixture comment prefers a BARE Mk1-3 pod at GROUND level and warns the Kerbal X pod "sits ~20 m up a full stack (ladder release lethal, pad boarding unreachable)". This forge uses the Kerbal X per the EVA-lane directive (only craft in the base). If the first live EVA-3 run confirms the pad-EVA reachability problem, add a bare-pod .craft to `bdock-forge-base` and point `craftName` at it (the forge machine + crew plumbing are already generic over the craft).
- **EVA scenario batch autorun: evaluated, DECISION = do not wire.** The EVA-1/2/3 seam scenarios keep `batchComplete` SKIPPED. The valuable AutoRecord EVA in-game tests are all `AllowBatchExecution=false` (Isolated/row-play only; a RunTests batch runs zero of them) and require a mid-flight crewed vessel (Skip on the PRELAUNCH EVA fixtures); the batch-safe EVA-adjacent categories (EvaSpawnPosition, CrewReservationLive) Skip when there are no committed spawned-endpoint recordings, which a fresh EVA fixture lacks; and a batch's FLIGHT-with-vessel in-memory+disk isolation revert would couple the seam scenario's recording-production verification to the batch teardown timing (fragile). Full rationale in the `FORGE`/EVA-1 spec comment. FUTURE (not this task): if a nightly home for EvaSpawnPosition / CrewReservationLive is wanted, add a DEDICATED batch-only scenario over the injected corpus (S1.4/H6 pattern) so committed spawned-endpoint recordings actually exercise those tests instead of Skipping. ~~**FUTURE**~~ DONE 2026-07-26 (branch `ingame-test-wiring`), and HALF of the recommendation was wrong. The dedicated batch-only scenario shipped for `EvaSpawnPosition` as `H20-eva-spawn-position` - not over the injected corpus (it does not need one; it needs a MANNED LANDED host, which `gloops-airshow` already is: crewed mk1-capsule, `landed = True`, `splashed = False`). `CrewReservationLive` CANNOT be wired that way at all, and the recommendation's premise is the thing that is wrong: both its cells short-circuit on `spawnedCount == 0` and the injected corpus holds zero recordings with a non-zero `SpawnedVesselPersistentId` - "committed spawned-endpoint recordings" do not exist and no scenario can conjure them. NOTE the mechanism, because the first version of this entry got it wrong and the wrong version is an active trap: it is NOT that `CleanSaveStart` -> `RemoveSpawnedPidLines` strips the key (that helper runs BEFORE the corpus writer injects, and does not run on the harness path at all - `run.py` omits `-CleanStart`, so `PARSEK_INJECT_CLEAN_START=0`). The real invariants are that `RecordingBuilder.WithSpawnedPid` has ZERO callers and that `AddRealCareerRecordings` injects `RECORDING_TREE` nodes only. Do NOT add `-CleanStart` to the harness inject to "make the mechanism real": `CleanSaveStart` also strips VESSEL nodes and sets `activeVessel = -1`, which routes the load to SPACECENTER and reds every FLIGHT-scoped spec in the H7-H20 group. Driving it would emit exactly the vacuous `total=2 passed=0 failed=0 skipped=2` the anti-vacuity gate exists to reject. Fix: teach the corpus writer to author spawned-endpoint recordings (a C# fixture change, NOT a spec change); the same change also makes `H16-corpus-spawn-health`'s third cell (`SpawnedPidConsistency`, wired but inert for the identical reason) meaningful. A FOURTH trap was found alongside the three recorded above and is now documented in `docs/dev/autotest-ingame-category-inventory.md`: a test that RUNS, PASSES and asserts over NOTHING (a store walk over an empty store, sometimes behind a silent `yield break` that reports PASSED rather than Skipped) is invisible to `batch_contract_vacuity_gap`, which only catches `passed == 0`; only the FIXTURE defends against it. Full 97-category enumeration, the A/B/C triage and the H7-H20 PENDING-OPERATOR fly order: `docs/dev/autotest-ingame-category-inventory.md`; status rows in `docs/dev/autotest-status.md`.

**EVA-1 first-flight finding + fix (2026-07-24): the PlantFlag gate read a stale button-active cache, not the live plant availability.** ~~First live EVA-1-pad-flag run (both attempts identical): the entire seam chain worked - LoadGame, StartRecording, EvaExit (Jeb out of the mk1-capsule on the pad, ladder release APPLIED `released=true`), EvaBoard merge-back, StopRecording, CommitTree - and the analyzer read the produced save CLEAN. The single red: PlantFlag returned ERROR after the full 180 s bounded gate wait (`plantflag failed reason=flag-gate-timeout lastGateOpen=false elapsed=180.0s`), with the kerbal standing on the launchpad in a sandbox save.~~ ROOT CAUSE (decompiled `KerbalEVA`, KSP 1.12.5): the gate `ReadPlantGate` read `Events["PlantFlag"].active`, which is NOT the live availability - it is an EDGE-TRIGGERED CACHE assigned `= CanPlantFlag()` only at `idle_OnEnter` / `idle_b_OnEnter` (entering `st_idle_gr` / `st_idle_b_gr`), an `OnVesselSituationChange` fired WHILE already in `st_idle_gr`, a construction-mode toggle, `OnVesselGoOffRails`, or `AddFlag`. A kerbal that lands and stands still on the pad after an `EvaExit release=true` enters `st_idle_gr` exactly ONCE (the log shows an attitude change of 31.75 deg at that moment - a settle/stumble), so the cache was computed while ground contact / ragdoll-settle was still transient and latched stale-false; then NOTHING re-fired (situation already `Landed`, no state re-enter, no rails toggle), so `.active` never flipped true even though `CanPlantFlag()` did a frame later. A player never hits this because moving the kerbal at all re-enters `st_idle_gr` and refreshes the cache; the seam never moves. The decompiled `CanPlantFlag()` truth is `vessel.state==ACTIVE && part.GroundContact && flagItems>0 && !isRagdoll && UnlockedEVAFlags(AC-level) && !InConstructionMode`, all recomputed live on each call; the log corroborates the kerbal was `Landed`/`SurfaceStationary` (so `part.GroundContact` was set - `vessel.Landed=true` is set in the same layer-15-collision block). FIXED (gate side, per M-C2): `ReadPlantGate` now reflects a direct `CanPlantFlag()` call each poll AND confirms the kerbal is in a plantable fsm state (`st_idle_gr` / `st_idle_b_gr` - the only two states where `PlantFlag()`'s `On_flagPlantStart` MANUAL_TRIGGER is registered; firing `PlantFlag()` in any other state decrements `flagItems` without planting). No verb-behavior or fixture change (the kerbal genuinely can plant; we just needed to detect it). Pure decisions `TestCommandPlantFlag.IsPlantGateOpen` + `DescribePlantGateBlock` are xUnit-covered; the gate-wait / timeout logs now carry `blocked=<unmet precondition(s)>` (e.g. `no-ground-contact`, `fsm=<state>`) so a future timeout self-explains. CONFIRMED on flight 4 (2026-07-24, full PASS): the gate opens and P6 flag-capture (`afterFlagPlanted` -> Parsek FlagEvent capture line) fires end to end.

**EVA-1 flight-2 finding + fix (2026-07-24): EvaExit reported a false `released=true` while the kerbal never left the hatch ladder - the real cause of the PlantFlag no-ground-contact timeout.** With the live-`CanPlantFlag()` gate + `blocked=` diagnostics from flight-1, the re-fly NAMED the blocker: `plantflag gate wait ... gateOpen=false blocked=fsm=Ladder_Idle,no-ground-contact` for the ENTIRE 180 s of both attempts. The kerbal was hanging on the hatch ladder the whole time - yet EvaExit had logged `evaexit release applied ... released=true` ~0.2 s after exit. ROOT CAUSE (decompiled `KerbalEVA` / `KerbalFSM`, KSP 1.12.5, same edge-triggered-FSM family as the PlantFlag cache): `On_ladderLetGo` (goto `st_idle_fl`) is registered on ONLY four states - `st_ladder_idle` / `st_ladder_climb` / `st_ladder_descend` / `st_ladder_end_reached` (`KerbalEVA.cs:8737`). A fresh EVA that auto-grabs the hatch ladder STARTS in `st_ladder_acquire` (`KerbalEVA.cs:3062`), a transitional state that does NOT register `On_ladderLetGo` and only advances to `st_ladder_idle` after a 1.0 s `KFSMTimedEvent` (`On_ladderGrabComplete`, `KerbalEVA.cs:8504-8506`). `correctLadderPosition()` sets `onLadder=true` during that acquire window (`KerbalEVA.cs:14762`), so `OnALadder` is already true at 0.2 s. `KerbalFSM.RunEvent` for an event NOT registered on the current state logs `"Event ... not assigned to state ..."` and RETURNS a SILENT no-op (`KerbalFSM.cs:298-311`); a registered event transitions SYNCHRONOUSLY in-call. The old `ApplyLadderRelease` fired `On_ladderLetGo` the instant `OnALadder` was true (still in `st_ladder_acquire`), hit the silent-return path, but set `evaExitReleaseApplied=true` and logged `released=true`. The kerbal never let go, the timed event advanced it to `st_ladder_idle`, and it hung on the ladder forever -> no ground contact -> the PlantFlag gate correctly refused for 180 s. Same lesson as BDOCK's two-step SEPARATE: an action being CALLED is not evidence the effect HAPPENED; completion must be verified by observable state. FIXED (release side): `ApplyLadderRelease` now polls the fsm and fires `On_ladderLetGo` ONLY from a receptive state (`IsLadderLetGoReceptiveState` - the four registered ladder states), then VERIFIES next poll that the fsm actually LEFT the ladder family (RunEvent is synchronous), bounded re-fire cap 3 (`LadderReleaseMaxFires`) if still on a ladder; `released=true` in the completion payload now means VERIFIED-left-the-ladder (`evaExitReleaseVerified`), never merely called-the-event. The not-on-a-ladder exit keeps the `release=noop` path. Pure decision `TestCommandEvaExit.DecideLadderRelease` is xUnit-covered; the release logs carry the observed fsm state names (`fsm=<state>->​<state>`, `release wait ... awaiting receptive let-go state`). The PlantFlag gate wait already tolerates the ~2-3 m post-release fall + any ragdoll-recover window (it polls `CanPlantFlag()` every frame and only rejects on the stably-closed AC lock or budget - no early abort), so no gate change was needed. CONFIRMED on flight 4 (2026-07-24, full PASS): the release lands the kerbal on the pad and the plant gate then opens end to end.

**EVA-1 flight-3 finding + fix (2026-07-24): the PlantFlag seam completed BEFORE the SiteRename dialog spawned, so `afterFlagPlanted` never fired and the recording-layer FlagEvent was never captured - the last EVA-1 red.** With the two prior FSM fixes landed, flight-3 drove the plant PHYSICALLY: driver PASS, analyzer clean, the flag planted (`[Progress Node Complete]: FlagPlant`, milestone `Kerbin/FlagPlant` credited via the ProgressTracking path, verdict OK). The single remaining mismatch: the logContract "Flag event captured" token was absent. Proof it never ran: `verboseLogging` was on, yet the UNCONDITIONAL top line of `ParsekFlight.OnAfterFlagPlanted` (`Flag planted: '...' date stamped`, a `ParsekLog.Verbose`) is absent from the whole log - so `GameEvents.afterFlagPlanted` never invoked Parsek's handler. ROOT CAUSE (decompiled `FlagSite` / `KerbalEVA`, KSP 1.12.5): the seam's Phase-B "edge case 10" fallback (`else if (flagVesselExists)` -> `plantflag dialog-answered-externally`) declared the dialog answered as soon as the FlagSite vessel existed but no "SiteRename" popup was found. But `FlagSite.CreateFlag` (which spawns the Flag vessel) runs in `KerbalEVA.flagPlant_OnEnter` - at `st_flagPlant` ENTRY, ~110 ms after `PlantFlag()` - while the SiteRename popup is spawned MUCH later, in `flagPlant_OnLeave` -> `FlagSite.OnPlacementComplete` -> `RenameSite`, gated behind the `On_flagPlantComplete` KFSMTimedEvent whose duration is the FULL plant-animation length (multiple seconds). `GameEvents.afterFlagPlanted.Fire(this)` runs ONLY inside that dialog's `afterDialog` button callback, which runs ONLY when a dialog BUTTON is clicked (`AcceptSiteRename` or `DismissSiteRename`); `SiteRenameDialog.OnDismiss = DismissSiteRename` and a bare `PopupDialog.Dismiss()` do NOT invoke `afterDialog`. So the flag vessel EXISTING is not evidence the dialog was answered - it appears before the animation even completes. The fallback false-OK'd the plant ~110 ms in; the subsequent `flushandquit` tore the flight down ~1 s later, before the animation finished, so the popup never spawned and `afterFlagPlanted` never fired (no `SiteRename`, no `Flag planted`, no `Flag event captured` anywhere in the log). The old comment calling that branch "unreachable unattended" was exactly inverted: it is the DOMINANT path. FIXED (seam side, per M-C2 - the verb answers through the SAME confirm path a player's click uses): the "answered-externally" inference is deleted. The seam now keeps polling (new `TestCommandPlantFlag.DecideSiteRenameDialogAction`: popup-present -> InvokeDismiss, popup-absent -> KeepWaiting REGARDLESS of flag-vessel presence) until the real SiteRename popup spawns, then invokes the dismiss button's own `OnOptionSelected` callback -> `DismissSiteRename()` then `afterDialog()` -> `afterFlagPlanted.Fire` synchronously this frame -> `ParsekFlight.OnAfterFlagPlanted` records the FlagEvent into `recorder.FlagEvents`. `dialogAnswered` is set ONLY by the actual button invoke; if the popup genuinely never spawns (e.g. `OnPlacementFail`), the command honestly times out (`flag-timeout` ERROR) at the 180 s budget (which already accounts for "animation + dialog answer") instead of false-OK'ing. Same lesson as flight-2 / BDOCK: an action being CALLED is not evidence the effect HAPPENED. A liveness wait line (`plantflag waiting for SiteRename dialog elapsed=...`) self-explains a timeout. New pure decision `DecideSiteRenameDialogAction` is xUnit-covered (4 cases, including the flag-vessel-exists-but-no-popup KeepWaiting guard). CONFIRMED on flight 4 (2026-07-24, full PASS): `Flag planted: ... date stamped` + `Flag event captured (foreground recorder ...)` both appear end to end, and the run's recordings count is pinned at 4 in the spec.

**EVA-3 first-flight finding + fix (2026-07-24): a fast EVA-and-re-board dropped the Board branch point entirely - Parsek's board merge required the EVA recording to have been promoted by the first-modification watcher.** First live EVA-3-multi-kerbal run on the freshly forged `eva3-pad-3crew` fixture: driverValidity PASS (every seam verb OK), analyzer red=0, logValidate PASS, anomalySweep PASS; the only red was two missing logContract tokens - `detected boarding from EVA` and `Tree board merge completed` - for BOTH board cycles. ROOT CAUSE (proved from the collected `[RecState]` walk, `logs/2026-07-24_2022_EVA-3-multi-kerbal/KSP.log`): an EVA branch does NOT leave the kerbal on the live recorder. `CreateSplitBranch` keeps the live recorder on the SHIP's continuation child and parks the kerbal's `bgChild` recording in `RecordingTree.BackgroundMap` (EVA-3 `[#13]`: `rec=d955e1fa|Kerbal X|pid=3620499050`, bgChild `9cc3a493` = pid 3726578224). KSP then switches focus to the kerbal, `OnVesselSwitchComplete` backgrounds the ship recorder (`[#19]`: `rec=-`), and the EVA recording is promoted to a live recorder ONLY by the post-switch first-modification watcher (`OnPostSwitchAutoRecordPhysicsFrame` -> `PromoteTrackedRecording`), which waits for an attitude / position / manifest delta. In the GREEN EVA-1 run the kerbal was released from the ladder, so an `AttitudeChange` trigger (angle=27.68 vs threshold 3.00) fired ~735 ms after the switch and promoted the recording (`[#20]/[#21]`) a full second before the board. In EVA-3 the exits use `release=false`: the kerbal hangs motionless on the hatch ladder and boards ~0.18 s after the switch, so the watcher was still armed (`trigger=None`) and `recorder` was null at the board (`[#20]`/`[#22]`: `rec=- rec.live=F/F`). Both downstream gates then failed CLOSED: `OnVesselSwitchComplete`'s boarding detection requires `recorder != null && recorder.IsRecording && recorder.RecordingStartedAsEva`, and `HandleTreeBoardMerge` requires the EVA recording to be `activeTree.ActiveRecordingId` behind a stopped recorder carrying `CaptureAtStop`. Neither ran; `pendingBoardingTargetPid` aged out through the 10-frame window (`Boarding confirmation expired`) and the seam's F2 quiescence conjunct was VACUOUSLY true (it holds the head while a merge is IN FLIGHT; a merge that never starts is quiescent), so EvaBoard completed OK. The saved tree proves the data loss: two `type = 1` (EVA) BRANCH_POINTs and ZERO Board branch points, and the kerbal recordings terminated `background_destroy` / Destroyed instead of Boarded. This is a genuine recording-fidelity defect, not a seam race: a player can hop out and straight back in, and the same "no modification" EVA drops the merge. The known-limitation note in `RuntimeTests.cs` (the EVA-branch canary skips on pad/landed/splashed parents because "no live-EVA-recorder window ever exists") describes the same gap from the other side. FIXED (Parsek side): `ParsekFlight.OnCrewBoardVessel` now calls `TryPromoteEvaRecordingForBoardMerge`, which rebinds a background-only EVA branch recording to the live recorder at the board so the EXISTING merge path runs unchanged (it is the same `backgroundRecorder.OnVesselRemovedFromBackground` + `PromoteRecordingFromBackground` pair the watcher would have run, just triggered by the board instead of by motion). `onCrewBoardVessel` fires BEFORE KSP's `SetActiveVessel` (log: our handler at 33.350, `[FLIGHT GLOBALS]: Switching To Vessel` at 33.351), so the kerbal is still the active vessel and `FlightRecorder.StartRecording`'s `FlightGlobals.ActiveVessel` bind is correct. The new pure decision `ParsekFlight.DecideEvaBoardPromotion` is xUnit-covered (11 cells) and fails closed on every ambiguity: `NotNeeded` when a live recorder already owns the kerbal (the EVA-1 green path is byte-identical), `SkipRecorderBusy` when a live recorder owns another vessel, plus no-tree / restoring / not-EVA / ghost-vessel / kerbal-no-longer-active / not-tracked skips, each logged. NOT fixed on the seam side deliberately: adding a wait-for-merge to EvaBoard would reclassify a dropped merge as a driver INVALID instead of the PARSEK-FAIL it is (mission-vs-Parsek orthogonality), so the logContract expectations stay the gate. Second-order effect that also disappears: with no live ship recorder at the second exit, EVA-3's second branch had to come from the `CreateSplitBranchFromBackgroundParent` fallback (`path=background-parent`); after the fix the board merge restarts the pod recorder, so the second exit takes the normal `CreateSplitBranch` path. (The "2 branches for 3 kerbals" reading of the first-flight log was a misread: the spec EVAs TWO kerbals, Valentina and Bob - Bill stays aboard - so 2 branches for 2 exits was always correct.) CONFIRMED on flight 2 (2026-07-24, full PASS): 2 `Tree branch created: type=EVA` + 2 `detected boarding from EVA` + 2 `Tree board merge completed`, 7 recordings. The spec's count window is now PINNED at 7..7 - the old 5..9 floor was exactly the buggy count, so a full regression of this fix satisfied it and a half regression (one merge dropped -> 6) satisfied it too, and `hlib.evaluate_expectations` applies logContracts with `re.search` (presence only, never an occurrence count), which leaves the count window as the sole numeric guard on "two merges".

**EVA lane clean-context review follow-ups (2026-07-24, verdict SHIP on the shipping C#, FIX-FIRST on docs):** the six MUST-FIX doc items are applied in place (EVA-2 spec header re-written to the committed-fixture / LIVE-PROVEN reality, `autotest-status.md` corrected in all three stale places + EVA-2's live pass recorded, EVA-3's `[fixture]` block rewritten to the actual 94-part Kerbal X stack, every stale "Re-flight pending" sentence replaced with its outcome, and both fixtures' contents disclosed honestly). Four latent holes were closed with them:
- **Count windows PINNED EXACTLY (was the substantive item).** EVA-1 4..4, EVA-2 2..2, EVA-3 7..7, each measured as `.prec` files in that scenario's green run save and matching its derivation. The old EVA-3 window `{min=5,max=9}` had a floor of exactly the buggy count, so a FULL regression of the board-merge fix passed it and a HALF regression (one merge dropped -> 6) passed it too; EVA-1's `{2,4}` had the same shape. `hlib.evaluate_expectations` applies `logContracts` with `re.search` - PRESENCE ONLY, no occurrence counting - so the count window is the ONLY numeric guard on "the merges happened", and it must stay pinned. Recorded in each spec's comment.
- **`DecideEvaBoardPromotion` gained `SkipTreeActiveRecordingBusy`.** `PromoteRecordingFromBackground` overwrites `activeTree.ActiveRecordingId` unconditionally and never parks the previous value back into `BackgroundMap`, so a recording the tree still names while no live recorder owns it (`CreateMergeBranch` step 10 nulls the recorder on a failed `StartRecording` but leaves its `ActiveRecordingId` set) would be silently orphaned by the board-time rebind. The decider now skips when the tree's active id is non-null and different from the recording being promoted; on the live path it is null (both `OnVesselSwitchComplete` backgrounding branches clear it), so the proven behaviour is unchanged. 4 new xUnit cells; the skip log now carries `treeActiveRec=` / `trackedRec=`.
- **EvaExit ladder release: attempts and fires are now separate counters.** `evaExitReleaseFireCount++` used to run BEFORE `fsm.RunEvent`, which throws when the fsm was never started - on that throw the count was already > 0, so a later ORGANIC ladder departure would report `released=true` ("verified-left") though our fire never took: the exact false positive the verified-left contract was built to kill. `evaExitReleaseAttemptCount` (incremented before the call) now drives the bounded cap, `evaExitReleaseFireCount` (incremented only after `RunEvent` returns) remains the evidence for `released=true`. `DecideLadderRelease`'s parameter is renamed `attemptCount`; a new cell proves three throwing attempts still exhaust bounded.
- **B-DOCK `DOCK` docked short-circuit now needs corroboration.** `_read_docking_state` returns `Docked` when ANY port on the active vessel reads docked, so a craft with an already-mated internal pair would complete `BDOCK_DOCK` on its first poll without docking to the Station (not reachable with the single-port Kerbal X, but the dropped `docking_ever_enabled` conjunct had been guarding it incidentally). The short-circuit stays, gated on either the docked read APPEARING during the phase (new `dock_entry_docked` state field, latched at phase entry) or the target port being within `BDOCK_DOCKED_TARGET_DIST_EPS` (10 m). `docking_ever_enabled` is deliberately NOT a disjunct: enabling the AP latches it after one poll regardless of effect, so an entry-docked craft would just false-complete two polls later. 3 new mlib cells.
- **Three nits:** the harvest tool's VESSEL scan is anchored to the `FLIGHTSTATE` span (new `flightstate_span`, brace-walked with fallbacks) because `activeVessel` indexes into THOSE nodes - a `SCENARIO`-embedded VESSEL ahead of FLIGHTSTATE would shift the index and make the situation gate judge the wrong craft (4 new cells); `mission_runner`'s `ACTION_LAUNCH_VESSEL` handler reads `action.launch_site` / `action.crew` directly now that both are declared `mlib.Action` fields (a `getattr` default would silently launch with the default crew after a rename); and the write-only `plantFlagLastGateOpen` field is deleted (the timeout line already logs the live local).

## Live observability of running test flights [BUILT, branches `live-observability` (Phase 1) + `live-observability-p2` (Phase 2); design `docs/dev/design-live-observability.md`]

Operator-requested after the B5/B6 diagnostic pattern (grep the newest mission stdout, sample rate-limited telemetry, INFER machine state) nearly hid two defect classes (the finding-12 single-frame attitude transient; the over-cap plan-removal loop that reads as a silent 1x hang). Phase 1 (supervisor-side, stdlib-only): `harness/status.py` renders one live panel from the existing artifacts - phase + time-in-phase vs budget, decoded telemetry, sparse events, phase history, and a heuristic "what is it doing / why might it look stuck" line (named the 2026-07-22_1210 over-cap block instantly in replay); `--watch/--raw/--run/--head`. Phase 2 (mission-side): a 5 s rate-limited `machine ...` decision-state line (incl. `planAttempts`/`bodyBlank`), a trailing `ut=` telemetry token, loud `gate <field> a->b | <snapshot values>` lines on every machine latch flip (single-frame transients visible by definition), a 20-frame ring-buffer `window dump` on transition/flake/vessel-lost/gate-flip, the classify=fly accepted-plan event, and an atomic best-effort `results/<runId>_status.json` every ~2 s that status.py prefers over log parsing. Pure helpers in mlib (`format_machine_state`/`diff_machine_state`/`format_snapshot_compact`/`snapshot_dict`/`machine_state_dict`), unit-tested; the fly loop only performs the I/O. Watch item: confirm live log-volume stays under the ~2x estimate on the next operator flight.

### Telemetry audit 2026-07-25: six things the runner MEASURED and never SHOWED [FIXED, branch `autotest-orbit-missions`]

An audit of the live surface after the ORBIT lane closed. Nothing here is a new
subsystem; every one of the six is a number the runner was already computing
and then discarding. The framing defect: **every phase budget in this system is
GAME time, and there was no WALL budget anywhere in the live surface.**

1. **No wall accounting at all.** MEASURED: a B12 run died on
   `mission-budget-expired` after burning **57% of its wall budget in ONE
   phase** while every displayed budget read ~7.5% consumed. The per-phase WALL
   was already measured exactly (`mission_runner._WarpUtilisation` closes a row
   on every phase exit; its rows summed to 467.87 s against a `wallSeconds` of
   468.009 on a real B5 run) and simply never shown, compared, or remembered.
   FIXED: `mlib.wall_budget_block` publishes
   `wallElapsedSeconds`/`wallRemainingSeconds`/`wallBudgetSeconds`/`phaseWallSeconds`
   into the status payload, and `status.py` prints
   `mission wall: 39m39s / 1h10m (57%) | phase wall 39m39s` instead of the old
   denominator-free `wall ~N (telemetry-line est.)`. The estimate stays as the
   fallback for an older/stale status file. SEPARATE BUG found while fixing it:
   `status.py load_mission_params` read only `[driver.missionParams]` and never
   `driver.steps[].budget`, which is where the wall budget (4200) actually
   lives - so the panel could not have known the denominator even in principle.

2. **`gameSecondsPerWallSecond` had ZERO programmatic consumers.** The number
   that named two shared-machine warp defects at a glance was end-of-run only
   (built by mlib, asserted by one unit test, quoted in three docs, read by
   nothing). FIXED: a ratio column on the phase-history rows, the OPEN phase's
   live `phaseWarp` block in the status payload, and a LOW marker.
   **The threshold is informational BY CONSTRUCTION and that is the finding:**
   the measured thrash reads ~40 while MechJeb's legitimate 600 s pre-ignition
   hold reads **7.96**, so the ratio alone CANNOT separate broken from
   deliberate. Marking on ratio alone would fire on every 1x phase and mean
   nothing. The marker therefore requires BOTH `>= 120 s wall` and
   `< 100 game-s per wall-s` - "this phase cost real wall and bought almost no
   game time" - which on a healthy B11 run names exactly four rows (MJ-ASCENT
   199 s, TRANSFER-BURN 129 s, CAPTURE-BURN 642 s, PARK 180 s = 91% of its
   non-warp wall). Three of those four are legitimate by design. No new give-up
   was added: `warp_liveness_starved` already owns the BROKEN case.

3. **`PHASE_BUDGET_KEYS` was blind to the entire ORBIT tail.** The table covered
   8 phases; `PLAN-CAPTURE`/`CAPTURE-BURN`/`PARK`/`ORBIT-COMMIT` were missing
   even though their keys exist in both specs, and `derive_heuristic` had no
   branch for them - so **B11's CAPTURE-BURN, the single most expensive phase in
   the whole suite at 642 wall seconds, printed `budget n/a`**. FIXED: all four
   keys plus the B1 / B4 / EVA-4 / FORGE / B-DOCK phases, each mirroring mlib's
   own `_*_phase_budget` dispatcher (a phase name that collides across machines
   maps to the same param key everywhere, which is what makes one flat table
   sound; B-DOCK's `TRANSFER` carries the 2x multiplier mlib applies). Four new
   heuristic branches read `captureExecDownStreak` / `parkStableStreak` /
   `nodeExec`, from the status file's machine block or the `machine ...` log
   line. A test cell asserts every table key is defined by a committed spec, so
   no invented keys. NO missing-but-undefined keys were found.

4. **The ring dump is an unbounded log amplifier.** MEASURED:
   `results/2026-07-25_0103_B12-minmus-orbit_mission.stdout.log` is **43 MB /
   181,786 lines**, of which **144,561 (79.5%)** are `window[NN/20]` payload
   from **7,218** gate-flip dumps - **7,207 of them triggered by the single
   field `gate warpToCmd`** while the coast thrashed its warp. Even a HEALTHY B5
   run spends **721 of 1,431 lines (50%)** on window payload carrying only 209
   unique frames (**71% duplication**), because consecutive dumps re-emit an
   overlapping slice of the same ring. The repo had already fixed this exact
   class once, for `park_stable_streak`. FIXED: `gate-flip` dumps only are
   rate-limited to one per **10 s** = `RING_BUFFER_FRAMES * POLL_INTERVAL_SECONDS`,
   the ring's own span, so admitted dumps carry contiguous non-overlapping
   history (a shorter gap duplicates, a longer one drops). `phase-transition`,
   `terminal-*`, `vessel-lost` and the give-up dumps stay unconditional; the
   `gate warpToCmd` LINE is untouched (it was informative in replay); one
   batch-summary line per flight names how many dumps were suppressed, emitted
   from a `finally` so it covers all five exit paths plus the exception unwind.

5. **No cross-run duration record.** `flake.json` tracked `{total, numerator,
   rate, quarantined}` and nothing tracked duration, and every artifact that
   carried one is gitignored (`results/*.json`, `results/summary.txt`, `*.log`,
   `coverage/*`). CONSEQUENCE MEASURED: the B12 spec header claimed "Fly B11
   FIRST ... it prices the capture tail in ~20 wall-minutes instead of ~30",
   while every archived run says the opposite - **B11 PASS wall 1315 / 1319 /
   1317 / 1317 (p50 1,317 s), B12 PASS wall 627 / 627 / 627 / 626 (p50 627 s)**.
   Backwards across four measured runs each, unnoticed, because nothing durable
   ever compared two runs of the same scenario. The intuition behind the claim
   (Minmus is the longer coast in GAME time) is true and irrelevant: a game-time
   span costs almost nothing in wall once it warps, while a 1x hold costs wall
   at 1:1, so the Mun's 642 s pre-ignition hold outweighs the Minmus coast.
   FIXED: `hlib.compute_durations` / `duration_regressions` (pure) +
   `harness/coverage/duration.json`, **COMMITTED** (a few hundred bytes, and
   regenerating it requires re-flying the suite), PASS results only - an INVALID
   that died on a budget measures the BOUND, not the scenario. Warns at
   `last > 1.5 * p50` with a 3-sample floor (the measured run-to-run spread is
   0.2-0.3%, so 1.5x is far outside noise). The B12 spec claim is corrected;
   `autotest-status.md` did NOT repeat it (its B12 row already said, correctly,
   that B12 is the cheaper of the two).

6. **Retry cost was never summed.** `_run_scenario_with_retry` wrote each
   attempt's result and returned the last; no scenario total was logged.
   MEASURED: B7-duna burned **794 + 776 = 1,570 s across two INVALID attempts**
   and produced nothing, traceable only as two unrelated summary lines. FIXED:
   one `scenario cost attempts=N wallTotal=Xs terminal=Y` Info line plus
   `attemptsWallSeconds` on the terminal result (re-written with
   `append_summary=False` so the rolling summary does not double). The result
   also carries `missionWallSeconds`, read from the mission JSON the mission
   step already parses through the same schema gate as the verdict read, so
   **the harness-vs-mission residue is a subtraction**. That residue MEASURED at
   a stable **40-67 s across 16 runs** (KSP boot ~35 s + verifier chain ~10 s),
   which is why the seven individual call sites are deliberately NOT
   instrumented - the subtraction bounds them, and a residue past ~120 s is the
   signal to go look, not a reason to add seven permanent timers now.

### Telemetry audit follow-up 2026-07-26: the mutation review [FIXED, branch `autotest-orbit-missions`]

A reviewer audited the six changes above by MUTATING them (12 mutations, 10
caught, 2 survived) and returned FIX-FIRST. Everything below lands on the same
branch, so #1350 is safe standalone regardless of merge order.

1. ~~**The duration ledger was DESTRUCTIVE (G5).**~~ `refresh_coverage_and_flake`
   recomputed `harness/coverage/duration.json` from `results/*.json` and
   truncate-wrote it. `results/` is GITIGNORED and per-checkout, so a fresh
   worktree that flew ONE scenario overwrote the committed 24-entry record with
   1 entry. OBSERVED LIVE 2026-07-25. FIXED here (the fix previously existed
   only on the descendant `autotest-landing-missions`, which would have let main
   inherit the bug if #1350 merged first).
2. ~~**Merging only the MISSING scenarios is HALF a fix.**~~ A scenario the run
   DID measure still had its committed entry REPLACED: `{"n": 5, "p50": 1317}`
   became a per-checkout `{"n": 1, "p50": 1400}`, and `n=1 <
   DURATION_MIN_SAMPLES` DISARMS the regression warn. Under the
   one-worktree-per-branch workflow that is most scenarios most of the time, and
   only 3 of the 24 committed entries carry `n >= 3` today. FIXED by storing the
   SAMPLES, not the summary: `hlib.duration_samples` extracts
   `{scenarioId: {endedUtc: wallSeconds}}` from PASS results,
   `hlib.merge_durations` unions them with the committed tail (bounded at
   `DURATION_SAMPLE_TAIL = 10`, one JSON line per sample) and recomputes
   `n/p50/p95/last` over the union. `n` counts every sample ever contributed
   (including ones aged out of the tail) so the arming gate reflects real
   history. THE LEDGER ONLY EVER ADVANCES: a sample counts as new only when its
   `endedUtc` is strictly newer than every key the committed tail holds -
   without that watermark a long-lived worktree double-counts, because the
   samples that aged OUT of the tail are still sitting in its `results/` dir
   (25 real samples became n=41 on the next run in the first cut of this fix,
   caught by self-review before commit). A summary-only committed entry has no
   watermark, so it bootstraps with `n = max(prior_n, len(samples))`: a fresh
   worktree KEEPS the committed `n` (the warn stays armed) and a long-lived one
   does not double it. Both error directions are UNDER-counts, which can only
   make the warn more conservative. Keyed by `endedUtc`, so merging the same
   result set repeatedly is idempotent; results sharing a `runId` collapse to
   the newest first.
3. ~~**G4's one safety claim had ZERO test coverage.**~~ Two mutations survived
   1,117 green tests: removing the gate-flip rate limit entirely, and
   rate-limiting EVERY reason (`phase-transition` / `terminal-*` /
   `vessel-lost` too). Cause: the only end-to-end cell flew a fake B1 producing
   exactly ONE gate flip, so the suppression path never ran, and under the
   second mutation three phase-transition windows and the gate-flip window
   vanished while the summary line printed a claim it had just violated. FIXED:
   `GateFlipSuppressionFlightTests` drives the real fly loop with a scripted
   machine flipping a gate on many consecutive frames under a clock stepping
   inside the limit, and asserts `suppressed > 0`, `count(reason=gate-flip
   dumps) == emitted`, `count(phase-transition dumps) == count(transitions)`,
   the terminal window, and an unsuppressed `vessel-lost` window. Both
   mutations now die (verified by re-applying them).
4. ~~**`duration.json` was written non-atomically and an unreadable ledger was
   silently replaced.**~~ The recovery path reopened the exact bug it was
   written to close: a partial file from a Ctrl-C / run-budget process-tree
   kill / power loss failed the next run's parse, fell back to "no prior", and
   truncate-wrote this checkout's handful over the 24-scenario record - visible
   to `git status` as an ordinary modification a "stage everything" pass would
   commit. FIXED: `run.write_text_atomic` (tmp + `os.replace`, mirroring
   `write_result`) for all four generated artifacts, and
   `run.read_duration_ledger` FAILS LOUD - a file that exists but does not
   parse, fails `hlib.check_schema`, or has no scenarios map logs an Error and
   SKIPS the duration write entirely for that run.
5. ~~**The LOW throughput marker was noise (G2).**~~ MEASURED across the
   archive: on healthy B11 it marked 4 rows, ALL FOUR legitimate (MJ-ASCENT
   1.33, TRANSFER-BURN 12.3, CAPTURE-BURN 8.0, PARK 1.0); across 5 healthy B12
   runs it marked the identical 2 rows every time. **ZERO true positives.** The
   decisive number: healthy CORRECTION-BURN reads ratio 43.6 while the defect
   the marker exists for reads ~40, so the ratio cannot separate them; the 120 s
   wall floor separates by DURATION, not health. FIXED: the marker now requires
   `armedWarpCommands > 0` as well. `mlib.is_warp_arming_command` splits arming
   from cancelling (`ACTION_CANCEL_WARP` and `SET_RAILS_WARP(0)`, a cancel to
   1x, do NOT arm), `warp_utilisation_row` carries `armedWarpCommands`, and
   `status.armed_warp_commands_in_span` counts them per phase off the log's
   `action ...` lines for the closed history rows. Every false positive issued
   ZERO arming commands (PARK's single command is `set_rails_warp value=0.000`);
   the thrash issued 3,603. On healthy B11 and all 5 healthy B12 runs the
   tightened rule fires on ZERO rows. HONESTLY: one archived B12 flake
   (`2026-07-25_0229`, CAPTURE-BURN 138 s / ratio 1.102) ALSO had zero warp
   commands, so the tighter rule would not have caught it either - but it was
   already caught by its own `capture under-burn` assertion, so the marker added
   nothing there.
6. ~~**The panel labelled the throughput block with the WRONG phase.**~~ The
   header phase comes from the LOG's last transition; the throughput numbers
   come from the STATUS FILE's phase. Neither was labelled, so a real render
   printed `PHASE: EVA-WINDOW` above DESCENT's numbers. FIXED:
   `status.format_phase_throughput_line` prints the payload's own
   `phaseWarp["phase"]` whenever it disagrees with the header.
7. ~~**A strictly better G4 rule, same code size.**~~ The 43 MB pathological log
   holds only **16 distinct `(phase, gate-field)` pairs** against 7,218
   gate-flip dumps (7,207 from `warpToCmd` alone). Admitting the FIRST
   occurrence of each pair unconditionally emits 16 windows instead of 7,218 - a
   bigger reduction than the time rule - and no novel flip ever loses its
   20-frame context, which the time rule could not promise (a suppressed flip
   followed by >10 s of quiet lost its window permanently). FIXED:
   `mlib.gate_flip_novelty_keys` + `should_dump_gate_flip_window(...,
   first_seen=)`; the limiter keeps a bounded seen-set (cap 512, far above the
   measured 16) and the time limit now applies to REPEATS only.
8. ~~**A malformed ledger entry crashed the end of a run.**~~ `run.py` indexed
   `entry["last"]` while `duration_regressions` read defensively, so a
   hand-edited `{"n": 5, "lastVsP50": 2.0}` raised `KeyError` out of
   `refresh_coverage_and_flake` AFTER the whole suite had flown, and
   `logger.close()` never ran. Reachable now that the file is committed. FIXED
   at both ends: `merge_durations` DROPS an entry that carries neither samples
   nor the full numeric shape, `duration_regressions` never flags one, and the
   warn line uses `.get`.
9. ~~**Two NITs.**~~ (a) `(no GAME budget for this phase)` was an affirmative
   claim that was false when the phase IS in the table but the spec omits the
   key and `mlib` applies a machine default; `status.phase_budget_note` now
   distinguishes "untimed phase" from "key absent, machine default applies".
   (b) The panel showed only the mission budget though `_drive_mission_step`
   holds both kills, and the percentage under-reads by KSP boot + seam steps
   (always in the "looks safer than it is" direction); the wall line now carries
   a `run budget` term and an `excl. boot` token.
10. ~~**The "no invented keys" test proved almost nothing.**~~ It was a
    SUBSTRING search over concatenated scenario TOMLs, so a key appearing only
    in a COMMENT passed; it said nothing about `mlib` (the real authority) and
    nothing about the reverse direction - a phase mlib budgets that the table
    omits, which was the original G3 bug. REPLACED with an oracle: build
    sentinel params (a distinct value per table key), construct each machine's
    params via its `*_params_from_dict` builder, and assert `_*_phase_budget(P,
    phase) == status.phase_budget_seconds(phase, sentinels)` for EVERY phase
    constant of all seven machines that have a dispatcher, plus a
    no-unread-keys direction. Verified to catch a dropped `CAPTURE-BURN`, a
    mis-mapped `PARK`, an invented key, and a dropped B-DOCK `TRANSFER` 2x
    multiplier.

## First end-to-end harness run fixes: H5 walk contract + S0.5 discard sidecar residue [BUILT, branch `autotest-s05-autorecord-pin`]

The first daily-cadence composition run against merged main (2026-07-19; S1.4 PASS, S0.6 PASS) surfaced two reds:

- ~~**H5-invariants-corpus contract arithmetic (spec bug).**~~ The spec's required log contract demanded `recordings=272` but the in-game walk counts RECORDINGS, not injected trees: the 272-tree corpus hydrates to 306 recordings across 276 walked trees (chain trees contain multiple members). Contract re-pinned to the exact `recordings=306 trees=276` pair. Bonus finding: the live walk reported `fails=0 warns=0` over all 306, so the synthetic corpus is invariant-clean, resolving the spec's former PENDING-OPERATOR check.
- ~~**S0.5-live-record-discard residue (genuine seam-adjacent defect).**~~ StartRecording's quickload-resume OnSave writes ACTIVE-tree sidecars (`.prec`/`.pann`/`_ghost.craft`) to disk; the shared discard core (`AutoDiscardActiveTreeCore`) is in-memory-only by design, so DiscardTree left an orphaned `9007aaaa.prec` behind, and with the store at zero `CleanOrphanFiles`' zero-known safety guard preserves it FOREVER. Fix: `DiscardTreeImpl` captures the tree's recordings pre-teardown and reaps sidecars for ids absent from the post-discard `BuildKnownRecordingIds()` set (pure `SelectDiscardReapRecordings` decider; the known-id guard keeps a committed-restore clone, which shares its committed original's id, from ever deleting the original's files). Per the Fable review, the reap additionally skips entirely (pure `DiscardReapSkipReason`, logged) while a Re-Fly session marker or merge journal is active or an active-tree restore is in progress: in those load shapes the restored active tree holds the ONLY copy of the original mission's recordings (RewindInvoker removes the committed tree before the restore pops it to active), so their ids are legitimately unknown while their files are still the durable committed data. Initial forensics theory (auto-record firing before the SetSetting pin) was DISPROVED by the timeline: SetSetting landed before OnFlightReady, no auto-record fired.
- Watch item (not reproduced): H5's first invocation red `recordings.count 0 < min 272` - the injected corpus did not hydrate on the first staged launch but did on the solo re-run. Flake ledger will catch recurrence.

**OPEN (gameplay-path sibling of the S0.5 leak):** normal-gameplay discard paths (merge-dialog Discard, scene-exit no-op auto-discard, pre-switch Discard) share the same in-memory-only core, so a player discard with an otherwise-empty store strands the quickload-resume sidecars permanently (zero-known guard refuses cleanup; with any committed recording present the next-load orphan sweep QUARANTINES them - `CleanOrphanFiles` moves sidecars aside, it never hard-deletes - so impact is the empty-store case only). Fix belongs at `AutoDiscardActiveTreeWithMessage` with the same known-ids guard AND, mandatorily, the same Re-Fly/merge-journal/restore-in-progress skip gate the seam reap carries (`DiscardReapSkipReason`): in the restored-committed-tree load shapes the active tree holds the only copy of committed recordings whose ids are absent from the known set, and a gameplay-path reap without that gate would permanently destroy committed mission data (Fable review of PR #1328, finding 1). Needs its own reviewed PR since it adds file deletion to a shared gameplay path.

## Re-Fly leaked post-rewind pending science into the merge [FIXED, branch `fix-refly-pending-science`]

Found by the 2026-07-19 preservation-branch forensic audit. `GameStateRecorder.PendingScienceSubjects` is in-memory only and had no reconciliation across a Re-Fly (Rewind-to-Separation): it was not captured in `ReconciliationBundle.Capture`, never UT-classified at `Restore(cutoff)`, and the re-fly load deliberately skips the quickload discard (`ParsekScenario.ShouldRunQuickloadDiscard` returns false while the session/invoke is active). Since Re-Fly is invokable mid-recording, a subject captured after the rewind point survived the revert still tagged with the origin recording id; at the merge the origin split hands the kept pre-rewind HEAD that id (`RecordingTreeSplitter`), and the tag-based routing in `LedgerOrchestrator` ignored `captureUT`, so a ScienceEarning row landed on a kept, non-superseded recording. Science was credited for an experiment that never happened on the surviving timeline, and tombstones cannot reach a row created after the merge.

Fix (two layers):
1. `ReconciliationBundle` now captures the pending-science list, and `Restore(cutoff)` (the Re-Fly SUCCESS path) restores it minus entries with `captureUT` strictly after the cutoff, mirroring the Rec-1 strict greater-than contract in `RouteLedgerRetire` (an entry stamped exactly at the cutoff fired at-or-before the state the loaded quicksave embeds, so it is kept). The parameterless +inf rollback overload restores the list wholesale (blind), matching the established rollback contract.
2. Belt-and-suspenders in the tree science routing (`ResolveTreePendingScienceOwnerRecordingId`): a tagged subject whose `captureUT` lies outside the tagged recording's `[StartUT, EndUT]` window logs a Warn and falls back to capture-UT window attribution (the downstream cross-recording check in `ConvertScienceSubjects` then drops the mis-tagged capture). Two carve-outs keep the tag instead of re-routing, both logged at Verbose (they fire on legitimate flights every commit, so no Warn): (a) NO window covers the capture - post-window tagged captures are legitimate for on-rails flights, the BUG-A anchor in `ConvertScienceSubjects` owns that case; (b) the covering window belongs to a CHAIN SIBLING of the tagged recording (same `ChainId`) - the windows are built with chain-gap closure (`AdjustStartUtForChainGap`), so the BUG-A capture on a chained flight lands inside the successor's ADJUSTED window, and re-routing there would drop the science entirely (cross-recording skip credits NEITHER segment). Implausible capture UTs (NaN/Inf/<=0) keep tag routing unchanged.

Accepted residual: within one chain the tag stays authoritative, so a stale re-fly leak whose tag and covering window are chain siblings would still credit via the BUG-A anchor IF it ever bypassed layer 1 (e.g. a crash between the capture and the rewind's bundle Restore). Layer 1 removes such subjects on every rewind success path, so the routing check intentionally trades this narrow residual for not dropping legitimate chained on-rails science.

Tests: bundle capture/restore round-trip with and without cutoff (before/at/strictly-after boundary entries, NaN entries, replace-not-merge), rollback-blind behavior, and the routing paths (re-route warn, keep-tag verbose with BUG-A credit preserved, chain-sibling gap-window keep-tag with credit on the tagged segment, implausible-UT no-warn) in `ReconciliationBundleTests` / `PendingScienceSubjectsClearTests`.

## Auto-merge recordings: silent full-fidelity commit [BUILT, branch `automerge-full-fidelity`]

Implements `docs/dev/plans/silent-full-fidelity-autocommit.md`. The `autoMerge` ON path used to commit ghost-only (`AutoCommitTreeGhostOnly` nulled every `VesselSnapshot`), and the finalized stable-terminal snapshot was nulled at OnLoad before its `_vessel.craft` sidecar was written, so a surviving vessel could not re-materialize at recording end (spawn-at-end lost). Fix routes the two outside-Flight auto-commit sites (warm scene-change fallback + cold-load pending-outside-flight, both in `ParsekScenario.OnLoad`) through a shared `AutoCommitPendingTreeOutsideFlight`, which calls the dialog's own `MergeDialog.MergeCommit` + `BuildDefaultVesselDecisions` when the new pure predicate `ParsekScenario.ShouldSilentFullFidelityCommit` qualifies (`isAutoMerge && Finalized && !reFlyActive && scene != MAINMENU`), else the lightweight ghost-only commit (re-fly / Limbo / MAINMENU / non-autoMerge). `ParsekFlight.CommitTreeSceneExit` now force-writes dirty sidecars (mirrors the autoMerge=OFF #289 loop) so the finalized snapshot is durable for the cold-load hydrate. The OnSave `SafetyNetAutoCommitPending` stays ghost-only (avoids a quicksave-inside-OnSave via `MergeCommit.RefreshQuicksaveAfterMerge`). #88 folded: landed/splashed exits no longer force an approval dialog under autoMerge; both gates removed (`SceneExitInterceptor.ShouldShowDialogBeforeSceneChange` landed/splashed row + the OnLoad `ShouldShowCommitApproval` gate), and `GhostPlaybackLogic.ShouldShowCommitApproval` + the now-dead `activeVesselLandedOrSplashed` params/wrappers deleted. Re-fly kept dialog/journal-gated (a silent `MergeCommit` would run `TryCommitReFlySupersede`); MAINMENU kept dialog. Default stays OFF. Impl-review fixes: the silent `MergeCommit` passes `refreshQuicksaveAfterCommit: false` (new param) so no `GamePersistence.SaveGame` runs inside OnLoad (re-entrant-OnSave / never-SaveGame-in-OnLoad rule); the now-dead `RecordingStore.PendingDestinationScene` field + writer removed; `ApplyVesselDecisions` widened to `internal static` with a retention test. Suites: xUnit 18176 (new `SilentFullFidelityCommitDecisionTests` + `MergeDialogVesselTests.ApplyVesselDecisions_KeepsSpawnableSnapshot_NullsGhostOnly`; `SceneExitInterceptorTests` landed/splashed cases flipped to None; `Bug156Tests` ShouldShowCommitApproval block removed).

**PENDING-OPERATOR** (for the default-flip follow-up, per the design runbook): with autoMerge ON, verify (1) an LKO survivor exiting to Space Center persists as a real vessel, not a ghost; (2) a landed vessel exits to KSC silently (no dialog) and persists; (3) quit mid-mission then Resume commits full-fidelity on the cold-load path; (4) a crash then Revert rolls back cleanly (#434 intact); (5) a Re-Fly exit still shows its confirmation dialog. Then flip `ParsekSettings.autoMerge` + `UI/SettingsWindowPresentation` defaults in a follow-up PR.

## M-C1 - Seam verbs batch 1: InvokeRewind / AnswerMergeDialog / TimeJump / KscAction [BUILT, branch `autotest-c1-impl`]

Implements `docs/dev/design-autotest-seam-verbs-c1.md` (merged #1315) exactly: the four verbs moved Reserved->Implemented (14 implemented / 11 reserved); five new DispatchState bits + per-verb precondition/deferral rows; InvokeRewind routes through the real RewindInvoker.CanInvoke with decline reasons surfaced verbatim (plus the merge-journal/load/recording dispatch guards, conservative-INTERRUPTED); AnswerMergeDialog is the folded conclude-and-answer verb (kind-scoped ReFlyMergeDialogPresent, drives the FLIGHT exit when marker-live-but-no-dialog, invokes the live DialogGUIButton's own callback, answer-applied AND scene-settled completion with the post-settle re-scan); TimeJump is forward-only through TimeJumpManager.ExecuteJump with UT+settle completion; KscAction's four sub-actions invoke the REAL stock APIs (research through a properly-hosted RDTech MonoBehaviour - GameObject.AddComponent<RDTech> + host=R&D + Warmup() + ResearchTech(), NEVER a host-less `new RDTech` shell that would spend no science and fire a phantom event; reflection-invoked SpaceCenterBuilding.UpgradeFacility; the stock-CurrencyModifierQuery-gated hire via HireApplicant [the "mirrored CrewRecruited debit" originally applied here was removed after the first live L-track run - stock `Funding.onCrewHired` already charges on the OnCrewmemberHired HireApplicant fires, so the mirror double-debited; see the L-track batch fix entry above]; KerbalRoster.Remove) and confirm the EFFECT before OK (guard-blocked = REJECTED blocked-committed; research maps the RDTech.OperationResult too). hlib companions: verb-table move + InvokeRewind/TimeJump into DEFERRED_SEAM_VERBS + new subkinds. Tests: 341 TestCommand xUnit (pure-decider cells + the AnswerMergeDialog scene-in-core cell, the CLAIMED->Interrupted journal cells) + a career-gated `KscActionResearchNodeInGameTest` (asserts science spent + node Available + the GameStateRecorder observer line - the pure suite is structurally blind to the applier) + hlib cells; full suite 18273 + lib 282 + provision 149 + missions/lib 89.

**Review-round fixes (three-reviewer panel):** the research applier was rebuilt onto a hosted RDTech MonoBehaviour (B1 blocker - a `new RDTech` shell silently spent no science, false-REJECTED valid research, and poisoned the ledger with a phantom OnTechnologyResearched); `DecideAnswerCompletion` takes the TestCommandScene enum so scene classification lives in the pure core; hire affordability routes through the stock CurrencyModifierQuery (strategy modifiers honored) with the pure raw check kept as the headless precondition.

**PENDING-OPERATOR:** the Unity applier paths (reflection facility upgrade, roster hire/dismiss + effect confirmation, TimeJumpTo, the popup-answer drive) need one live career session per the design's runbook section; grep the GameStateRecorder observer lines (not the guard-patch lines) to confirm stock effects reached the recorder. The new `KscActionResearchNodeInGameTest` covers the research-node path in-game (career + FLIGHT batch). The B9 rewind-cycle fixture (Crashed sibling + RP with the three usability prerequisites) is authored in a follow-up scenario lane.

**Post-merge Fable audit fixes [branch `autotest-integration-fixes`]:** a post-merge Fable audit of the merged C1 code surfaced two completion-decider defects plus three smaller ones, all in the pure Lane-B cores:
- ~~**TimeJump CompleteOk was unreachable live (BLOCKER).**~~ `DecideJumpCompletion` used a two-sided `Abs(currentUT - targetUT) <= tol` reached-test, but `ExecuteJump` does not pause the game, so the clock keeps advancing at 1x and by the time the 3-frame settle window drained the UT was already tens of ms PAST the target and receding - `reached` went false and stayed false, so every live jump fell through to the budget and ERRORed `jump-timeout` despite landing exactly. Fixed to a one-sided latch (`currentUT >= targetUT - tol`): reaching or passing the target latches reached. Root cause of the escape: every shipped `DecideJumpCompletion` test fed a FROZEN clock; added moving-clock decider cells (UT advances past target across polls -> CompleteOk; UT never reaches target -> JumpTimeout).
- ~~**DecideRewindCompletion made RewindTimeout unreachable in its own documented case (SHOULD-FIX).**~~ `contextPending` short-circuited to StillWaiting BEFORE the budget check, so a reload that aborts without `ConsumePostLoad` (context Pending forever) held the FIFO head indefinitely. Reordered to check the budget unconditionally after the marker-success check (mirror `DecideLoadCompletion`); added the pending-forever-past-budget -> RewindTimeout cell.
- ~~`ResolveTargetUt` gained a finiteness + sane-magnitude guard~~ (rejects NaN / Infinity / an overflow from `now + delta` / a magnitude beyond `MaxAbsTargetUt = 1e12` with a distinct `target-out-of-range` reason; `TimeJumpImpl` now surfaces the resolve error verbatim instead of a hardcoded `missing-jump-target`), so a 1e308 target can never reach `ExecuteJump`.
- ~~`DecideAnswerCompletion` applied-but-scene-stalled now carries the applied fact~~ (new `AnswerAppliedSceneStall` terminal -> `ERROR msg=answer-applied-scene-stall` with `applied=true` in the payload) so the orchestrator never reads a committed merge as a clean `answer-timeout` failure.
- ~~`TryCompleteTwoPhase` wraps the completion probes in the same exception containment `ExecuteHead` has~~ (a throwing reflection invoke against a mid-teardown dialog converts to a terminal ERROR instead of escaping `Update`).
- Doc corrections in `design-autotest-seam-verbs-c1.md`: the at-most-once sections + edges 5/11/16 wrongly claimed a KILLED run retries from a pristine re-staged template - `hlib.should_retry` never retries KILLED; corrected to terminal KILLED / no retry / at-most-once on the WAL alone. The `kerbal-hire` funds null-to-fill recommendation was a guaranteed false-red (no hire capture pattern exists in `STOCK_AWARD_PATTERNS`); corrected to a fixture-pinned author constant now, fill-from-capture only after the capture patterns are rewritten against real stock logs (M-B3).

## M-C1.1 - Seam-verb follow-ups: Science-mode research gate + SaveGame verb [BUILT, branch `autotest-c1-followups`]

The two ratified M-B3 blocking dependencies (`docs/dev/design-autotest-ledger-scripts-b3.md`), implemented against the M-C1 seam.

- ~~**Science-mode research-node gate (M-B3 OQ1).**~~ DONE. `research-node` deferred `career-not-ready` in SCIENCE_SANDBOX because the shared readiness gate hard-required CAREER, even though R&D and node research are live in Science. Fixed with a sub-action-scoped widen (the upgrade-facility SPACECENTER sub-gate is the template): a new `DispatchState.RnDPresent` bit (`(Mode==CAREER || Mode==SCIENCE_SANDBOX) && ResearchAndDevelopment.Instance != null`, sampled by the addon's `IsResearchReady`), and the KscAction dispatch admits `research-node` on `CareerPresent || RnDPresent` while hire-kerbal / dismiss-kerbal / upgrade-facility STAY CAREER-only (`CareerPresent`) because their `Funding.Instance` legs are null in Science. The shared top-level CAREER Mode bit was NOT relaxed (the ratified warning). Pure decider cells cover every mode x sub-action combination (CAREER all four admit; SCIENCE_SANDBOX research admits + other three defer career-not-ready; SANDBOX all four defer) in `TestCommandC1DispatchTests`.
- ~~**SaveGame batch-2 seam verb (M-B3 L2/R6 dependency).**~~ DONE. An in-process `GamePersistence.SaveGame(name, HighLogic.SaveFolder, SaveMode.OVERWRITE)` of the CURRENT live game to the run save, reusing FlushAndQuitImpl's save call shape minus the quit, so the R6 facility-refund window is drivable in ONE launch (upgrade -> SaveGame -> LoadGame -> assert). SaveGame was NEVER in the M-A2 reserved envelope (no save verb was reserved), so it is a NEW implemented verb name per the M-B3 `SaveGame` naming: 15 implemented / 11 reserved. Design surface: sync verb (standard CLAIMED->EXECUTED->DONE; a replayed DONE id is skipped upstream, and re-saving identical live state is harmless anyway), AnyScene precondition with an in-executor no-game refusal (`ERROR no-game` at MAINMENU / null CurrentGame), args `{name?}` defaulting to `persistent`, payload `OK saved=<name>`, save-write failure -> `ERROR save-failed`, full logging. Pure decider `TestCommandSaveGame` (name-default / can-save gate / payload) is xUnit-covered; hlib companion moved SaveGame into `IMPLEMENTED_SEAM_VERBS` (+ acceptance test).

Doc: a new "M-C1.1 follow-ups" section in `design-autotest-seam-verbs-c1.md` documents both surfaces; forward-pointer notes added to the M-A2 known-verb table (SaveGame) and the M-C1 doc's DispatchState / KscAction dispatch spec (the research widen). Tests: TestCommand xUnit (verb-table 15/11, the mode x sub-action matrix, SaveGame dispatch + pure decider) + hlib cells.

## M-C2 - EVA seam verbs + EVA missions: EvaExit / EvaBoard / PlantFlag [BUILT, branch `autotest-mc2-impl`, PENDING IN-GAME PROOF]

Implements `docs/dev/design-autotest-eva-missions.md` (merged #1339) exactly: three NEW implemented verbs (never reserved, additive like SaveGame) moving the verb table 15 -> 18 implemented / 11 reserved; three additive `DispatchState` bits (`ActiveVesselIsEva`, `StructuralSplitPending`, `FlightEvaPresent`) sampled by `BuildDispatchState`; per-verb precondition/deferral rows (EvaExit defers `split-pending`/`flighteva-not-ready` and refuses `load-in-flight`; PlantFlag/EvaBoard defer `not-eva`); per-verb `DeferralBudget` constants 120/180/120. All three are two-phase PENDING verbs with bounded, observable completions routed through pure xUnit-covered deciders (`TestCommandEvaExit`/`TestCommandPlantFlag`/`TestCommandEvaBoard`), mirroring the M-C1 Lane-A-applier / Lane-B-decider split. The only source touches outside `TestCommands/` are two internal read-only accessors on `ParsekFlight` (`StructuralSplitPending` over `pendingSplitInProgress`, `BoardMergeQuiescent` over `!ChainToVesselPending && pendingBoardingTargetPid==0`); no new Harmony patches, no GameEvents subscriptions, no schema change. The verbs call the SAME public stock entry points a player's hatch-click / plant-flag click / board uses (`FlightEVA.fetch.spawnEVA` via reflection - not compile-visible, same as the in-game EVA tests; the public `KerbalEVA.fsm.RunEvent(On_ladderLetGo)`, `PlantFlag()`, `BoardPart(part)`; the "SiteRename" popup answered via its own dismiss-button callback, the `AnswerMergeDialog` pattern), so a seam-driven EVA is byte-identical to a hand-driven one for every Parsek observer.

- **EvaExit** (two-phase, irreversible): `ResolveKerbalArg` (default-first-crew / exact-ordinal-match / `no-crew` / `kerbal-not-aboard`), part+airlock resolution (`no-airlock`), `spawnEVA(tryAllHatches:true)` with null -> `REJECTED eva-refused` (no side effect), remembers `lastEvaExitFromPid` (in-memory, non-durable) for EvaBoard's default target; optional `release=true` runs the ladder let-go once active (not gated on ground contact - the plant gate owns that); optional `settleSeconds` dwell (F7) held AFTER the base conjuncts so Parsek's deferred EVA auto-record arms before the next FIFO command. Completion: exists AND active AND settled AND (release satisfied) AND settle-dwell elapsed; budget -> `eva-exit-timeout`.
- **PlantFlag** (two-phase, F1 bounded-wait gate, dialog-answering): instant refusals for stably-closed causes only (`not-eva`, `flag-lock-stable`); then PENDING and polls the LIVE plant gate via `DecidePlantGateWait` (a transiently-closed gate mid-fall is `KeepWaiting`, NEVER a terminal reject - the EVA-1 near-deterministic failure). The gate is `ReadPlantGate` = a direct `CanPlantFlag()` call each poll AND a plantable-fsm-state check (`st_idle_gr` / `st_idle_b_gr`), NOT `Events["PlantFlag"].active` (that edge-triggered cache latched stale-false on the pad and timed out the first live EVA-1 - see the EVA-1 first-flight finding above). Fires `PlantFlag()` exactly once on the open transition, answers the "SiteRename" popup via its dismiss button's own callback (so `afterFlagPlanted` fires and Parsek captures the FlagEvent), CompleteOk on flag-vessel + dialogAnswered + settled; `flag-gate-timeout` (now carries `blocked=<unmet precondition>`) / `flag-timeout`.
- **EvaBoard** (two-phase, F2 quiescence, irreversible): resolves the target (explicit `targetPid` / `lastEvaExitFromPid` if loaded / nearest loaded non-EVA), refuses `unknown-target` / `no-boardable-part` / `target-full` / `not-near-target` (10 m `IsWithinBoardRange` honesty bound over stock's missing distance check), calls void `BoardPart`, then the FIVE-conjunct completion (EVA vessel gone AND kerbal in target crew AND target is active vessel AND Parsek board-merge quiescent AND settled) - the quiescence conjunct holds the FIFO head out of the board-merge window so a next command can never corrupt the merge; crew-unchanged-past-budget -> `board-timeout`, never a false OK.

hlib companions (F5/F6): the three names into `IMPLEMENTED_SEAM_VERBS` (18 total); `DISPATCH_DEFERRAL_BUDGET_SECONDS` entries EvaExit 120 / PlantFlag 180 / EvaBoard 120 (none a `DEFERRED_SEAM_VERB`, all under the 540 cap); `spec_expects_live_recording` learns the `autoRecordOnEva=true` pin so EVA-2's genuinely-recording run keeps REC-001/REC-003 UNSUPPRESSED. Three seam-driven specs: `EVA-1-pad-flag` (nightly, existing gloops-airshow fixture), `EVA-2-orbital-board` + `EVA-3-multi-kerbal` (`pending-fixture` - the two NEW fixtures `eva2-lko-crewed` / `eva3-pad-3crew` are operator step P2). All three validate through the real `CommittedSpecValidationTests` path.

Tests: TestCommand xUnit +~69 cells (verb-table 18/11, C2 dispatch matrix incl. the safe-point/batch/interrupted gates, the three pure deciders' every-conjunct + regression cells - mid-fall KeepWaiting, false-OK-over-unanswered-dialog guard, the five board conjuncts incl. the quiescence window, settleSeconds dwell, the 10 m inclusive bound); full C# suite 18538 green. hlib cells: verb-table move + step acceptance + the F5 budgets + the F6 autoRecordOnEva live-recording clause; harness suites lib 402 / provision 203 / missions/lib 281 green.

**PENDING-OPERATOR (design live-prove list, one KSP session):** P1 load-and-focus sanity of `gloops-airshow`; P2 commit `eva2-lko-crewed` + `eva3-pad-3crew` then re-tier EVA-2/EVA-3 pending-fixture -> daily/nightly; P3 first EVA-1/2/3 runs pin the recordings-count windows (currently provisional per R-C) and the exact structural-snapshot message (the `[Pipeline-Smoothing]` token is regex-escaped in-spec pending confirmation); P4 pin the orbital auto-record log wording (EVA-2's required token keeps the stable `Auto-record started` prefix); P5 confirm ladder release lands with ground contact + zero kerbal damage; P6 confirm the SiteRename dismiss-callback fires `afterFlagPlanted` and Parsek's capture line appears. Also promotes EVA-1 nightly -> daily once the windows are pinned.

## EVA-4 - atmospheric mid-flight EVA + kerbal personal chute: `EvaChuteDeploy` + mission `eva4_atmo_chute` [~~LIVE-PROVEN 2026-07-24~~; ~~INTERMITTENT FAILURE FOUND 2026-07-25~~; ~~fixed headlessly 2026-07-26, branch `eva4-failopen`~~ **RE-PROVEN 2026-07-26 (flight 4, PASS on attempt 1)**]

~~**OPEN 2026-07-25 - the EVA chute CUTS ITSELF mid-descent and the kerbal dies; and the MISSION still returns MISSION-OK.**~~ CLOSED. Both defects were diagnosed and fixed HEADLESSLY on 2026-07-26 from the archived evidence (no new flight), and the operator RE-FLEW it the same day: **PASS on attempt 1, all seven verifiers, wall 409 s**, provisioned from the branch with the deployed DLL SHA-identical to the branch build.

```
apoapsisWindow 19696.874 | evaWindowReached 1592.752 | evaWindowDescentRate -18.560
craftCanopyObserved 11964.692 | MISSION-OK
missionOutcome PASS gating=2 | driverValidity PASS | analyzer PASS red=0
logValidate PASS | anomalySweep PASS hits=[] | expectations PASS mismatches=0
```

**The green flight is the weaker half of that result, and the operator checked the stronger half deliberately: a LIVE kerbal proves nothing about a DEAD one.** The fail-open closure was verified structurally rather than by outcome - (1) the mission DECLARES what it does not verify (`unverifiedByMission ["kerbalSurvival"]`); (2) `missionOutcome` GATES the two owning verbs, so an unmet one reds `PARSEK-FAIL(mission-outcome)`; (3) it FAILS CLOSED IF DISARMED - zero gating verbs reads SKIPPED, never PASS, which is the part most likely to have been got wrong; (4) the dead path is covered in xUnit over the pure decision, with both aliveness bits load-bearing (the debounced bit gates `KerbalLost` so a transient read cannot fake a death; the RAW bit is a required `CompleteOk` conjunct so the debounce cannot mask a real one).

RESIDUAL, recorded rather than closed: no test kills a kerbal in a LIVE flight, so end to end that path rests on xUnit over the pure decision plus a code read. A deliberately fatal flight is NOT warranted - the pure decision is the right home for it - but the gap is real and is stated here rather than left implicit. See the two ROOT CAUSE blocks and the RUNBOOK below (kept: it is now the proven recipe, not a proposal). Found by the first full daily+nightly sweep (24 of 25 scenarios green; this was the only red). Verdict `PARSEK-FAIL(expectations)`, wall 187 s, `results/2026-07-25_1007_EVA-4-atmo-chute.json`, collected log `logs/2026-07-25_1310_EVA-4-atmo-chute/`.

Measured descent profile from the verb's own polling (this is the whole defect in five lines):

```
t=0.0s   state=SemiDeployed  alt=1650.4  vspeed=-11.0    canopy verified
t=5.0s   state=Cut           alt=1480.0  vspeed=-55.5
t=10.0s  state=Cut           alt=1115.8  vspeed=-86.7
t=15.0s  state=Cut           alt=634.3   vspeed=-103.0
t=20.1s  state=Cut           alt=101.1   vspeed=-109.2
t=20.5s  [ERROR] evachutedeploy eva-chute-kerbal-lost ... chuteState=Stowed alive=false
```

The chute DID open - `canopy verified ... state=SemiDeployed` at t=0, which is the OBSERVED-state gate the 2026-07-24 hardening added, working correctly. It then went to **Cut** within 5 s and the kerbal accelerated from -11 to -109 m/s and died. This is NOT the flight-1 failure mode (that one was an INERT chute that never left Stowed, `automateSafeDeploy=0`); the canopy opened and was then cut. Note the terminal read prints `chuteState=Stowed` because the kerbal vessel is already gone by then - the live states are the `wait` lines above.

**ROOT CAUSE (b), THE CANOPY CUT - ESTABLISHED 2026-07-26 from the archived log + decompiled KSP 1.12.5. The canopy was not cut by any parachute logic; it was cut as a SIDE EFFECT of a COLLISION knocking the kerbal out of the semi-deployed-parachute FSM state.**

The evidence is four lines of `logs/2026-07-25_1310_EVA-4-atmo-chute/KSP.log`, in order:

```
[LOG 13:09:53.614] [Parsek][INFO][TestCommands] evaexit release fired ... fsm=Ladder_Idle->Idle_Floating stillOnLadder=false
[LOG 13:09:53.668] [Parsek][VERBOSE][Recorder] Part event: ParachuteSemiDeployed 'kerbalEVA' pid=1940654732
[LOG 13:09:53.868] [Parsek][VERBOSE][Recorder] Part event: ParachuteCut 'kerbalEVA' pid=1940654732
[LOG 13:09:53.884] Event Stumble not assigned to state Ragdoll
```

That last line is the whole diagnosis and it is the ONLY occurrence of `Stumble` or `Ragdoll` in the entire run (`grep -c` over the collected KSP.log: 1). Decompiled ground truth:

- `On_stumble` is a `MANUAL_TRIGGER` event registered on every state EXCEPT ragdoll / grappled / clamber P2 / P3 (`fsm.AddEventExcluding(...)`, KerbalEVA.cs:8153), so it IS registered on `st_semi_deployed_parachute`, and its `GoToStateOnEvent` is `st_ragdoll` (KerbalEVA.cs:8151).
- The ONLY site that fires it is the collision callback, gated on `c.relativeVelocity.sqrMagnitude > stumbleThreshold * stumbleThreshold` with `stumbleThreshold = 3.5` m/s (KerbalEVA.cs:12700 / :847).
- `OnSemiDeployedParachuteModeLeft` calls `evaChute.CutParachute()` on EVERY exit from that state except a transition to `st_fully_deployed_parachute` (KerbalEVA.cs:11152-11169), and `ragdoll_OnEnter` then calls `evaChute.AllowRepack(false)` (KerbalEVA.cs:4486-4497), so the cut is terminal.
- The only other event that leaves `st_semi_deployed_parachute` for `st_ragdoll` is `On_land_start`, whose `OnCheckCondition` reads `lastCollisionNormal` / `lastCollisionTime` (KerbalEVA.cs:8061-8117) - also collision-driven. And `On_parachute_cut` goes to `st_idle_fl`, NOT ragdoll (KerbalEVA.cs:9605), so the OBSERVED Ragdoll state rules out "the module cut for its own reasons" as the cause.

So the exit was collision-driven and the cut is a CONSEQUENCE of the FSM transition, not its cause. The logged `Event Stumble not assigned to state Ragdoll` is the SECOND frame of that same contact, arriving after the FSM had already moved (the first frame fired from a state where the event IS registered, so it produced no warning line).

WHAT IT HIT is MEASURED (corrected 2026-07-26; an earlier revision of this entry called it inferred and unprovable). KSP logs no collider identity, but Parsek's own recording supplies the geometry: the kerbal's `.prec` carries a `ReferenceFrame.Relative` section ANCHORED TO THE POD (`ref = 1`, `anchorRecordingId = 4cc22361...`), and per the RELATIVE contract its `lat/lon/alt` fields are anchor-local METRES. Parsed, they give the pod-relative separation directly:

| UT | event | separation from the pod |
| --- | --- | --- |
| 123.38 - 124.04 | on the hatch ladder | 0.78 -> 0.71 m |
| 124.20 | ladder let-go | ~0.8 m |
| 124.26 | `ParachuteSemiDeployed` | **0.82 m** |
| 124.48 | `ParachuteCut` / Stumble | **1.50 m** |
| 124.70 / 124.92 | | 2.88 / 4.91 m |

The kerbal was 0.8 m from the pod when its canopy inflated, and nothing else was within kilometres. (`[Parsek][VERBOSE][BgRecorder] Background point sampled: pid=2905720181 ... dist=90m` at 13:09:57.707 is the same story 3.8 s later.) A semi-deployed canopy brakes the kerbal hard within a couple of physics frames while the pod continues at its own rate, which is exactly how a >3.5 m/s closing velocity appears between two objects that started together.

WHY THIS RUN AND NOT FLIGHT 1 OR 2 - and a CORRECTION (2026-07-26). An earlier revision of this entry said the exposure is the LENGTH of the semi-deployed window, because `ModuleEvaChute`'s stock `deployAltitude` is 1000 m so a 1,650 m EVA must sit SEMI-deployed for ~650 m while flight 1's 356 m exit went semi -> full in 0.66 s (`ParachuteSemiDeployed` 22:09:49.166, `ParachuteDeployed` 22:09:49.826). **That reasoning is WRONG and it is dangerous wrong.** `OnFullyDeployedParachuteModeLeft` (KerbalEVA.cs:11219, wired at :7244) calls `evaChute.CutParachute()` UNCONDITIONALLY - no state exclusion at all - and `On_stumble` is registered on `st_fully_deployed_parachute` too. A stumble out of a FULL canopy cuts it exactly like a stumble out of a semi one.

So a full canopy is NOT safe from this, and raising the EVA chute's `deployAltitude` - the technique already proven on the CRAFT chute at `craftChuteFullDeployAltMeters = 2500` - would NOT have fixed it. The operative variable is PROXIMITY AT CANOPY TIME, which is what the separation table above measures and what the fix targets. The intermittency is simply whether a stumble-grade contact happens while the pod is still within reach; flight 1 exited a co-moving pod at terminal velocity and flight 2 got lucky.

This correction is recorded rather than quietly patched because the wrong version is actively hazardous: a maintainer reading it would apply the `deployAltitude` knob, conclude the hazard was gone, and drop the standoff.

FIX (b): a bounded, OBSERVED pre-chute STANDOFF on `EvaExit` (`minStandoffMeters`, EVA-4 sets 30). The exit does not complete until the kerbal has read at least that far from every other LOADED vessel for `StandoffDebouncePolls` (2) consecutive polls, so the canopy inflates with nothing in reach. TWO bounds, both NON-FATAL on expiry (it completes anyway carrying `standoff=timeout` on the wire plus a Warn line naming which bound fired, because an unchuted kerbal is a certain death while a contact risk is not - the mitigation must never be able to make the outcome worse than the bug it mitigates):

- `StandoffMaxWaitSeconds` = 8 s, ~3.5x the ~2.3 s the 30 m clear actually took.
- `standoffFloorAltMeters` (EVA-4: 500 m), and THIS is the load-bearing one. The first draft had only the wall clock, at 15 s, justified in a code comment as "~165 m of fall" - which is 15 s x the LADDER-ATTACHED -11 m/s. The kerbal is UNCHUTED for the whole stage (the chute is armed by the NEXT step), so the bound is paid in FREE FALL: the flight-3 log itself measures 1650.4 m at elapsed=0.0s down to 634.3 m at elapsed=15.0s, i.e. ~1,016 m - a 6.2x error. Since the mission may hand off anywhere in [700, 2100] m, a wall-clock-only bound would have flown a low handoff into the ground with the canopy never armed, at t ~ 11.5 s, 3.5 s before the give-up ever fired. That is strictly worse than the collision the stage exists to avoid, i.e. the fix as first written violated its own stated rule. Found in panel review, not in flight. Off (`standoff=off`, zero extra Unity reads) for every scenario that does not declare it. Parsek's own ghost map vessels are excluded from the distance read (proto-only, no colliders, so they cannot stumble a kerbal - and a co-located one would otherwise pin the standoff at 0 m until the bound expired, silently reverting the mitigation on any save with a playing ghost). The spec makes the fix self-proving: `evaexit standoff cleared` is a REQUIRED `logContracts` token, so a stale DLL, a dropped arg, or a standoff that never cleared reds the run rather than flying the un-mitigated profile unnoticed.

**ROOT CAUSE (a), THE FAIL-OPEN - a mission that reports OK while its subject dies. ESTABLISHED, and the fix proposed in the original entry is NOT implementable.**

`eva4_atmo_chute` returned `MISSION-OK reason=all telemetry assertions met` on this exact flight. Its four assertions are `apoapsisWindow`, `evaWindowReached`, `evaWindowDescentRate`, `craftCanopyObserved` - every one about the CRAFT and the EVA WINDOW, none about the kerbal surviving. The original fix line here read "add a kerbal-survival assertion to the mission machine". **That cannot be done.** `eva4_decide`'s success terminal is `EVA4_EVA_WINDOW`, reached while the craft is still airborne and crewed, and the mission SUBPROCESS EXITS there; the kerbal EVA vessel is created afterwards, by the seam step `EvaExit`. The timestamps say it plainly: the mission's last line is `[Mission][Info][Verdict] mission verdict=MISSION-OK ... wall=112.980s` (`results/2026-07-25_1007_EVA-4-atmo-chute_mission.stdout.log`), and `evaexit start` is at KSP.log 13:09:52.404, AFTER it. There is no frame on which a kerbal-survival assertion could be evaluated, because on every frame the mission ever read the kerbal was crew inside a pod, not a vessel.

**THE FAIL-OPEN IS ONE LAYER UP, AND IT IS NOT A FIFTH COMMANDED-vs-OBSERVED.** The four documented instances of that class (the CAPTURE-BURN NodeExecutor, B1's chute, EVA-4's ladder release, the B-DOCK docking AP) all share "no observed channel existed, so we asserted on our own latch". Here the observed channel existed and worked perfectly: `EvaChuteDeploy` polls the LIVE kerbal (vessel present, parts present, crew aboard, roster not Dead), debounces it over 3 consecutive gone-reads, and fast-fails on a distinctly named give-up - `[Parsek][WARN] evachutedeploy kerbal read gone ... gonePolls=3/3` then `[Parsek][ERROR][TestCommands] evachutedeploy eva-chute-kerbal-lost`, at 13:10:14.159. The prescription had ALREADY been applied. The step's verdict was recorded (`results/2026-07-25_1007_EVA-4-atmo-chute.json`, `driver.steps[6] = {"cmd": "EvaChuteDeploy", "verdict": "ERROR", "met": false}`, and `driver.allExpectedMet: false`) - and then consulted by no gate at all: the same file records `verifiers.driverValidity.status: "PASS"` beside `allExpectedMet: false`. This is the failure mode that remains AFTER you fix commanded-vs-observed: **OBSERVED-BUT-UNGATED**. The observation is made, it is correct, it is durably recorded, and the classifier throws it away.

The reason is `run.py`'s autopilot carve-out, which makes EVERY post-mission seam step non-gating on a MISSION-OK run. Its rationale is sound and is kept - "a good flight Parsek then failed to record is a PARSEK-FAIL(expectation), NOT a driver-INVALID a retry would paper over" - but it silently assumed the expectations verifier would always notice. For `CommitTree` / `StopRecording` that mostly holds. For an OUTCOME verb it holds only if the spec author happened to write the right regex. On this run four expectation rows caught it (three missing required tokens plus the forbidden `\[Parsek\]\[ERROR\]`); delete any of them from the spec and a run that killed its own subject reports PASS.

FIX (a), in two halves:

1. **The structural gate (hlib + run.py).** New per-verb table `SEAM_VERB_POST_MISSION_ROLE`, TOTAL over `IMPLEMENTED_SEAM_VERBS` and gated by a unit cell so a new verb cannot inherit a default. `outcome` verbs (the four M-C2 EVA verbs - each one's verdict is a claim about a KERBAL's in-world state that no verifier re-derives) GATE; `recording` verbs (everything else: Parsek behaviour or plumbing) stay non-gating exactly as before. run.py emits a `missionOutcome` verifier row naming the gating verbs and the first unmet one with its `msg=`, and `classify_verdict` maps it to `PARSEK-FAIL(mission-outcome)`. Deliberately NOT a driver-INVALID: routing it through the driver stage would preempt and SKIP every verifier below it (throwing away the evidence that made this run diagnosable) and would let an intermittent subject death retry into a PASS-with-a-flake-note. Deliberately ahead of log-contract / expectation in the verifier precedence, because those rows are the downstream symptoms and this one is the cause.
2. **The mission's own honesty (mlib).** `MISSION_HANDOFF_CONTRACTS` records, per mission, what a HANDOFF mission does NOT verify and which step owns it. EVA-4 declares `unverifiedByMission: ["kerbalSurvival"], verifiedBy: ["EvaExit", "EvaChuteDeploy"]`; the block rides the result JSON and the MISSION-OK reason line gains the same statement, so `MISSION-OK` can never again be read as end-to-end success by a human or a script. A mission absent from the table is byte-identical to before. This half is DISCLOSURE, not a gate - the gate is deliberately structural (half 1) so it cannot be defeated by a future mission author forgetting to declare.

**RUNBOOK - EXECUTED 2026-07-26, PASS on attempt 1.** Kept as the proven re-fly recipe (and as the recipe for any future EVA-lane standoff change); the paragraph below describes the state it was written in.

Everything above was diagnosed and fixed from the committed artifacts - no KSP was launched. What is proven (counts MEASURED off the suites, not restated): 15 hlib cells, 5 fake-KSP smoke cells (one drives a deliberately BLINDED expectations block, so the only thing that can red it is the outcome step's verdict; another drives a post-mission REFUSAL, which must classify as a retryable driver fault and NOT as a Parsek defect), 8 mission-lib cells (6 mlib + 2 shell cells pinning the `[Verdict]` LOG line, which is the channel `status.py` and a watching human read), 18 xUnit `Standoff_*` / payload cells; all four harness suites (`lib` 746, `provision` 203, `missions/lib` 814) and the 18,686-case xUnit suite green; mutation-checked (see below).

What is NOT proven, and the honest list is longer than the first draft's: (1) that a 30 m standoff actually prevents the collision in the live game; (2) that the standoff WIRING is correct, because `EvaluateStandoff` / `TryCompleteEvaExit` are Unity-side and every xUnit cell exercises the pure statics beneath them - a refactor that dropped the `&& standoffSatisfied` conjunct would keep every headless suite green, and only the live `evaexit standoff cleared` token would catch it. That token is a REQUIRED `logContracts` entry precisely so it does, but it has never run.

FOUR commands, in order, and the first three are NOT optional. `EvaExit`'s standoff stage is C#; the harness flies `automation/stock-minimal`, NOT the dev instance; and provision's DEPLOY copies `Source/Parsek/bin/Debug/Parsek.dll` verbatim while its identity grep checks only `ParsekFlight` / `GhostPlaybackEngine` (`provision.py:65`) - strings present in EVERY Parsek build - so a STALE `bin/Debug` passes provision silently.

1. Build, so `bin/Debug` is this branch:

```bash
cd Source/Parsek && dotnet build
```

2. Provision, so the automation instance gets that DLL:

```bash
cd harness && python provision/provision.py --profile stock-minimal
```

3. VERIFY THE DEPLOYED DLL BEFORE FLYING. This is the gate a 2026-07-25 B12 run cost a whole flight for. From `harness/`, `automation/` is TWO levels up: it sits at the UMBRELLA root beside the worktree, not inside it. Expect 3; a 0 means the deploy did not take and the flight would prove nothing:

```bash
python -c "d=open(r'../../automation/stock-minimal/GameData/Parsek/Plugins/Parsek.dll','rb').read(); print(d.count('evaexit standoff'.encode('utf-16-le')))"
```

4. Only then fly (one nightly-tier scenario, ~7 min wall):

```bash
cd harness && python run.py --id EVA-4-atmo-chute
```

CONFIRMS THE (b) FIX - and `evaexit standoff cleared` is now a REQUIRED `logContracts` token in the spec, so the operator does not have to eyeball it: a stale DLL, a dropped spec arg, or a standoff that never cleared all red the run on a missing required token instead of quietly flying the un-mitigated profile. These lines must appear in the collected `KSP.log`, in this order, between `evaexit start` and `evachutedeploy armed`. Anchor the window on `evaexit start`, NOT on `evaexit release verified`: the spec documents the `release=noop` exit (a kerbal that never grabbed the hatch ladder) as legitimate and equally chute-ready, and on that path the seam logs `evaexit release=noop` instead, so the old anchor would vanish on a run the spec itself calls valid:

```
[Parsek][VERBOSE][TestCommands] evaexit standoff kerbal=Jebediah Kerman nearest=<m> min=30.0 clearPolls=<n>/2 waited=<s>s state=Waiting
[Parsek][INFO][TestCommands] evaexit standoff cleared kerbal=Jebediah Kerman nearest=<m> min=30.0 waited=<s>s
[Parsek][INFO][TestCommands] evaexit complete kerbal=Jebediah Kerman evaPid=<pid> released=true standoff=cleared
```

Then, from the chute step, the run is good iff BOTH of these appear and `Part event: ParachuteCut 'kerbalEVA` does NOT appear before the touchdown:

```
[Parsek][VERBOSE][Recorder] Part event: ParachuteDeployed 'kerbalEVA' pid=<pid>
[Parsek][INFO][TestCommands] evachutedeploy complete ... canopy=true chuteState=Cut down=true situation=LANDED alive=true
```

DO NOT use `Event Stumble not assigned to state Ragdoll` as the red criterion. An earlier draft of this runbook said a single occurrence between the EVA and the touchdown meant the bug was reproducing; that is FALSE and would fail a GOOD run. Flight 1 (`logs/2026-07-24_2210_EVA-4-atmo-chute/KSP.log`) logged 76 of them - 68 at touchdown and 8 at 22:09:48.475-.489, i.e. 67-81 ms after `evaexit complete` and BEFORE `evachutedeploy armed` - on the flight whose kerbal LANDED ALIVE. A stumble there is harmless: `On_semi_deploy_parachute` is registered on `st_ragdoll` as well as `st_idle_fl` (KerbalEVA.cs:9599), so a ragdolled kerbal can still open its chute. The fix makes that window LONGER, not shorter - the standoff deliberately holds the kerbal unchuted beside the pod for ~2-3 s - so expect these lines and ignore them.

The criterion that actually discriminates is the CHUTE, not the FSM: `Part event: ParachuteCut 'kerbalEVA` must NOT appear while the kerbal is still FLYING. A cut at the touchdown is the stock auto-cut and is expected.

FAILURE READINGS: `standoff=timeout` on the `evaexit complete` line means the kerbal never got 30 m clear in 15 s - the mitigation did not engage, and a cut canopy after that is the original bug, not a regression. `standoff=off` means the DLL is stale (re-provision) or the spec arg was dropped.

CONFIRMS THE (a) FIX (only observable on a run that FAILS, so it cannot be checked on a green re-fly): the result JSON must carry `verifiers.missionOutcome` with `gatingVerbs: ["EvaExit", "EvaChuteDeploy"]`, and the harness log must carry `[Harness][Info][Verify] verify missionOutcome status=PASS gating=2 firstUnmet=-`. On a green run that row reads PASS; if the kerbal dies again the verdict must read `PARSEK-FAIL` subkind `mission-outcome` rather than `expectation`.

MUTATION RESULTS (each mutation applied to HEAD, suites re-run, then reverted):

| Mutation | Cells that red |
| --- | --- |
| M1 `EvaChuteDeploy` role `outcome` -> `recording` | `test_outcome_set_is_exactly_the_four_eva_verbs`, `test_the_eva4_flight3_step_stream_names_the_chute_step`, `test_a_blind_spec_still_reds`, `test_the_real_spec_reds_naming_the_cause_not_the_symptom`, `test_the_result_record_names_the_step_and_its_terminal` |
| M2 `classify_verdict` drops the `mission_outcome_unmet` branch | `test_a_dead_subject_reds_the_run_as_mission_outcome`, `test_it_names_the_cause_ahead_of_the_downstream_symptoms`, `test_a_blind_spec_still_reds`, `test_the_real_spec_reds_naming_the_cause_not_the_symptom` |
| M3 `first_unmet_post_mission_outcome` always returns None | `test_the_eva4_flight3_step_stream_names_the_chute_step`, `test_a_blind_spec_still_reds`, `test_the_real_spec_reds_naming_the_cause_not_the_symptom`, `test_the_result_record_names_the_step_and_its_terminal` |
| M4 mlib drops EVA-4's handoff declaration | all 5 non-negative `Eva4HandoffContractTests` cells (2 FAIL, 3 ERROR) |
| M5 `StandoffDebouncePolls` 2 -> 1 | `Standoff_RequiresTheDebounceRun_NotOneGoodFrame`, `Standoff_ConstantsAreSizedForTheMeasuredProfile` |
| M6 `StandoffClearThisPoll` `>=` -> `>` | `Standoff_ComparesTheObservedDistanceInclusively` |
| M7 `ClassifyStandoff` always returns `Cleared` | `Standoff_RequiresTheDebounceRun_NotOneGoodFrame`, `Standoff_BoundedWait_GivesUpAndSaysSo` |
| M8 `StandoffClearThisPoll` drops the explicit NaN guard | NOTHING - **equivalent mutant**, reported rather than papered over. C# IEEE comparison already yields false for `NaN >= x`, so the guard is belt-and-braces. `Standoff_UnreadableDistance_FailsClosed` pins the BEHAVIOUR (which is correct and stable); the line is kept as intent documentation so a future rewrite of the comparison cannot silently lose fail-closed. |
| M9 `StandoffToken` reports a give-up as `cleared` | `Standoff_BoundedWait_GivesUpAndSaysSo`, `Standoff_TokenDistinguishesNeverAskedFromGaveUp` |
| M10 `StandoffMaxWaitSeconds` 15 -> 0 | `Standoff_ConstantsAreSizedForTheMeasuredProfile` |
| M12 `ClassifyStandoff` drops the altitude-floor branch | `Standoff_GivesUpAtTheAltitudeFloorWhateverTheClockSays` (the blocker cell: without the floor a low handoff flies the kerbal into the ground before the wall clock fires) |
| M13 `AdvanceStandoffClearPolls` rewritten as a TALLY (`prev + (clear ? 1 : 0)`) | `Standoff_DebounceIsARunAndResetsOnAnyNonClearPoll` (this is why the accumulator was extracted from the applier: as an inline applier line the same mutation kept all 13 pre-existing cells green) |
| M14 `expected_fail_signature_matched` drops the `NEVER_BUGID_ONLY_SUBKINDS` guard | `test_a_quarantine_key_cannot_turn_a_dead_subject_green` |
| M15 `classify_post_mission_outcome_miss` always returns `(True, "")` | `test_a_refusal_is_a_driver_fault_not_a_flight_outcome`, `test_every_driver_subkind_it_can_emit_is_retryable`, `test_a_refusal_is_a_retryable_driver_fault_not_a_parsek_defect` |
| M11 `StandoffToken` reports a still-`Waiting` stage as `cleared` | `Standoff_AnUnconcludedStageIsNotReportedAsCleared` (found in self-review before the PR: the first draft's token collapsed `Waiting` into `cleared`, so an `eva-exit-timeout` would have claimed an observation that was never made - the exact shape of the defect this whole change exists to close) |

**NOT DONE, deliberately.** The mid-mission command-seam bridge on `main` is hardcoded to `CommitTree` (`mission_runner.py` `_perform_seam_commit`). Generalising it would let EVA-4 do the whole EVA inside the mission and hold real kerbal-survival assertions - but PR #1357 (`rewind-loop-lane`) is already generalising exactly that bridge, so building a second one here would collide head-on. Once #1357 lands, the mission-side assertion the original entry asked for becomes implementable and the `handoff` declaration above is what should be retired in its favour.

Adds the one EVA surface the M-C2 trio does not reach. EVA-1/EVA-3 exit on the pad and
EVA-2 exits in orbit; EVA-4 exits MID-FLIGHT IN ATMOSPHERE, so it is the only case where
the kerbal is a FALLING VESSEL with a real trajectory of its own. Four Parsek surfaces
ride on that: (1) an EVA tree branch created mid-flight in atmosphere; (2) ATMOSPHERIC
TrackSections on the KERBAL's own recording; (3) the EVA chute captured as a two-phase
part event ON the kerbal (`ParachuteSemiDeployed` -> `ParachuteDeployed`, the D7
`chute-two-phase` cell, previously claimed by NO scenario); (4) the DOWN terminal applied
to a KERBAL recording, with the kerbal ALIVE.

FEASIBILITY was established from decompiled KSP 1.12.5 + the committed fixture bytes
BEFORE anything was built, because all three legs were genuinely in doubt:

- **Can a kerbal EVA from a moving craft in atmosphere at all? YES, with no stock
  envelope gate whatsoever.** `FlightEVA.spawnEVA(pcm, fromPart, fromAirlock,
  tryAllHatches)` (decompiled, FlightEVA.cs:334-546) refuses on exactly four things: the
  kerbal not being in `fromPart.protoModuleCrew`, a null airlock transform, an obstructed
  hatch / hatch inside a fairing (`HatchIsObstructedMore` + `hatchInsideFairing`), and a
  mod veto through `GameEvents.onAttemptEva` + `overrideEVA`. There is NO dynamic-pressure,
  g-force, speed, or altitude check anywhere on the path, and grepping the whole decompiled
  assembly finds NO stock subscriber to `onAttemptEva`. `onGoForEVA` even has an explicit
  in-flight branch (`if (!fromPart.vessel.LandedOrSplashed) StartCoroutine(kerbalEVA
  .StartNonCollidePeriod(...))`). So the SAFE ENVELOPE is ours to choose, not KSP's to
  enforce - which is why the scenario defines one explicitly rather than relying on a gate
  that does not exist.
- **Does the kerbal have a chute, and how is it deployed? YES, and by one public method.**
  The chute is `ModuleEvaChute : ModuleParachute` declared on the kerbalEVA PART itself
  (`Squad/Parts/Prebuilt/kerbalEVA.cfg`: deployAltitude 1000, minAirPressureToOpen 0.04,
  autoCutSpeed 0.5, chuteMaxTemp 650, fullyDeployedDrag 500). The `Squad/Parts/Cargo/
  Parachute` "evaChute" part is pure CARGO (`ModuleCargoPart`, packedVolume 10) - it only
  has to sit in the kerbal's `ModuleInventoryPart` for the module to switch on
  (`KerbalEVA.UpdatePackModels` -> `evaChute.SetEVAChuteActive(hasChute &&
  CanCrewMemberUseParachute())`, KerbalEVA.cs:1650-1662; the inventory scan matches
  `storedPart.partName.Equals("evaChute")` at :1477). Stock kerbals get it for free:
  `kerbalEVA.cfg`'s `ModuleInventoryPart` declares `DEFAULTPARTS { name = evaChute; name =
  evaJetpack }`, and the COMMITTED `b1-pad-craft` fixture's roster Jebediah already carries
  `INVENTORY { inventory = evaChute,evaJetpack }` with a `STOREDPART evaChute` in slot 0 -
  verified by reading the committed `persistent.sfs`, not assumed. The skill gate is
  vacuous in practice: `CanCrewMemberUseParachute` compares `experienceLevel >=
  vessel.VesselValues.EVAChuteSkill.value` and `PartValues` initialises EVAChuteSkill to 0.
  DEPLOY PATH: both stock player paths call the SAME public method. The keybind path is
  `On_semi_deploy_parachute.OnCheckCondition` (KerbalEVA.cs:9552-9590), which checks
  module-enabled + STOWED + the `EVA_ChuteDeploy` key + `VesselUnderControl` + NOT
  `JetpackDeployed`, then calls `evaChute.Deploy()`; the PAW path is the `[KSPEvent]
  Deploy()` on ModuleParachute. So `Deploy()` IS the player's click (M-C2 contract), and
  no kRPC reach is needed or wanted.
- **Survivability: YES, and the chute is genuinely load-bearing.** kerbalEVA's
  `crashTolerance` is 50 m/s and `maxTemp` 800 K. Under the module's `fullyDeployedDrag =
  500` (engaged below its own 1000 m `deployAltitude`) a kerbal sinks at roughly 5-6 m/s -
  an order of magnitude inside tolerance. Without it a kerbal falls near or past that 50
  m/s tolerance, so the sequence is a real save, not a formality. The seam verb does NOT
  take survival on trust: its DOWN completion requires the EVA vessel to still exist with
  the kerbal aboard and a non-Dead/Missing roster status, so a landed-but-dead kerbal is a
  terminal ERROR, never an OK.

**New seam verb `EvaChuteDeploy`** (`TestCommandEvaChuteDeploy.cs` pure decisions +
`ParsekTestCommandAddon.EvaChute.cs` applier), built in the M-C2 shape - pure decision core,
bounded gates, named timeouts, verbose logging:
- Arm refusals mirror the stock keybind path's OWN guards in its order, so the seam refuses
  for exactly the reasons a player's key press would be ignored: `not-eva`,
  `no-eva-chute-module`, `eva-chute-unavailable` (module switched off = no evaChute in the
  inventory: the fixture-contract failure), `eva-chute-not-stowed`, `eva-jetpack-deployed`.
- `Deploy()` only ARMS (STOWED -> ACTIVE, synchronously) and returns SILENTLY when the part
  is shielded from airstream, so the arm is VERIFIED by re-reading the state (`ArmTook`);
  a still-STOWED read is `eva-chute-arm-refused` (REJECTED, no side effect). Called is not
  evidence - the standing EVA-lane rule.
- Stage A CANOPY is a BOUNDED WAIT on the observed deployment state, not a post-call
  assertion: the module's own FixedUpdate gate needs static pressure over 0.04 atm (or
  altitude under 1000 m) AND speed over 1 m/s AND `DeploySafe` reading SAFE, and
  `DeploySafe` returns UNSAFE for the first 1.0 s after the part unpacks
  (ModuleParachute.cs:2108-2160), so the canopy is NEVER open on the arming frame. The
  observation is LATCHED, because stock `UpdateCut` auto-cuts below `autoCutSpeed` 0.5 m/s -
  a kerbal standing on the ground reads CUT, and a re-read at completion would fail the very
  landing the verb exists to prove.
- Stage B DOWN is opt-in (`awaitDown=true`), the same shape as EvaExit's optional
  `release` / `settleSeconds` stages: hold until LANDED/SPLASHED with the kerbal alive.
  Named terminals: `eva-chute-canopy-timeout` (armed but never opened) vs
  `eva-chute-down-timeout` (opened but never landed) vs `eva-chute-kerbal-lost`.
- Budget 420 s (`DeferralBudget.EvaChuteDeploySeconds`), under the harness 540 s cap; it is
  the FIRST EVA verb added to `DEFERRED_SEAM_VERBS` because its awaitDown stage genuinely
  holds the FIFO head for minutes.

**`release=true` on the preceding EvaExit is LOAD-BEARING, not cosmetic** (same
edge-triggered-FSM family as the EVA-1 flight-2 defect): `ModuleEvaChute` reaching
SEMIDEPLOYED calls `kerbalEVA.OnParachuteSemiDeployed()` -> `fsm.RunEvent(
On_semi_deploy_parachute)`, and that event is registered on ONLY `st_ragdoll` and
`st_idle_fl` (KerbalEVA.cs:9600). `KerbalFSM.RunEvent` for an unregistered event is a
SILENT no-op (KerbalFSM.cs:298-311). `On_ladderLetGo.GoToStateOnEvent = st_idle_fl`
(KerbalEVA.cs:8678), so the VERIFIED ladder release is exactly what puts the kerbal in a
receptive state; a kerbal still on the pod's hatch ladder would arm its chute and never
enter the chute state. (`RunEvent` does NOT evaluate `OnCheckCondition`, so the keybind
path's `GetKey()` / `VesselUnderControl` conditions do not apply to the module-driven
transition - verified in the decompiled `KerbalFSM`.)

**New mission `eva4_atmo_chute`** (`mlib.Eva4Params` / `Eva4State` / `eva4_decide` /
`eva4_window_open` / `evaluate_eva4_assertions`). It reuses the B1 hop shape
(PRELAUNCH -> ASCENT -> COAST -> DESCENT) but its terminal is EVA-WINDOW: the craft is
still AIRBORNE and CREWED when the mission ends. That split is forced, not stylistic -
kRPC exposes no EVA API and hlib allows exactly ONE mission-kind step per spec, so the
mission can only fly the craft into the envelope and hand off to the seam. Deliberately a
SIBLING machine rather than a parameterisation of B1: B1's terminal is the craft on the
ground and must stay exactly that (it is live-proven and other scenarios depend on its
shape), so folding an airborne terminal into it would make a proven contract a special
case of an unproven one.
- The window is SELF-REGULATING rather than a golden altitude: it needs the craft's own
  chute commanded AND altitude inside `[evaWindowMinAltMeters, evaWindowMaxAltMeters]`
  AND `|vertical speed| <= evaMaxDescentRateMps` AND an airborne situation. The craft
  crosses the ceiling still near terminal velocity, so the gate stays shut and opens a few
  hundred metres lower once the canopy bites - the handoff altitude is decided by the
  physics.
- Sinking past the floor without the window opening is a NAMED ASSERT-FAIL
  (`eva-window-missed`, carrying the altitude / vspeed / situation / craft-chute state),
  never a silent burn-down of the descent budget. Unlike B1 there is NO chute-deployed-impact
  DOWN success terminal: the craft reaching the ground at all means the EVA never happened.
- `settle_frames=0` + `skip_settle_tail`: the terminal hands a STILL-DESCENDING craft to a
  time-critical seam step, so every settle sample would spend altitude the kerbal needs.

**Why the EVA is specified LOW (window `[700, 2100]` m, `|vs| <= 25` m/s) rather than at
apoapsis.** An apoapsis exit (B1's window is 6-30 km) would give the kerbal 6-30 km of
descent; at ~5-6 m/s under full canopy that is 20+ minutes, far outside any per-step
budget the harness allows (the 540 s deferred-step cap). A low exit is also the SAFEST
available envelope: the craft is already decelerating under its own canopy, so dynamic
pressure at the hatch is trivial (25 m/s in sea-level air is well under a kPa) and far
inside the kerbalEVA part's 50 m/s crash tolerance and 800 K maxTemp. Flight 2 confirmed
both halves: the handoff landed at 1,606 m / -23.2 m/s and the kerbal's own descent
(~1.6 km at a steady -4.5 m/s) took ~215 s of its 420 s step budget.
(The band shipped as 800-2400 m / 60 m/s BEFORE flight 1; the re-tune moved it, see the
FLIGHT-1 block below.)

**No new fixture, no fixture edit.** `b1-pad-craft` is reused as-is; its roster Jebediah
already carries the stock default `evaChute,evaJetpack` inventory.

**The parent craft is a deliberate second surface.** After the EVA the pod descends under
its own canopy as a background vessel and touches down BEFORE the kerbal. Per the B1
fixture note the Jumping Flea breaks apart at touchdown (a COMPUTED ~8-9 m/s vs the booster's 7
m/s tolerance); that is EXPECTED here. Nothing in the spec asserts the pod survives - B4
owns the craft-survives-intact contract; EVA-4's survival contract is the KERBAL's. The
recordings count shipped as a provisional 2-10 window sized to absorb the breakup
children; flight 2 MEASURED 3 (pod root + kerbal EVA split + the pod's post-EVA
continuation - the breakup produced no extra recording on this profile) and the spec is
now PINNED at exactly 3. It is a re-pin contract, not a widen-on-drift one: the count is
the only numeric guard that the EVA split and the kerbal's own recording exist, and a
window wide enough for breakup variance is also wide enough for a dropped EVA branch.

Tests (counts are the CURRENT totals at branch HEAD, i.e. including the post-live
hardenings at the end of this section): `TestCommandEvaChuteDeployTests` holds 27 `[Fact]`
+ a 9-case `[Theory]` - the arm-refusal ladder, the synchronous arm verification,
canopy-open classification, the DOWN situation set, all five completion outcomes incl.
`KerbalLost` short-circuiting a satisfied completion, the two-aliveness-bit split (the
debounced bit drives only `KerbalLost`, the raw per-poll read is a required `CompleteOk`
conjunct), the kerbal-loss debounce - which delays a loss verdict past a transient
unreadable sample but can never mask a real one - and payload latching; plus the
verb-table / precondition-table / executor-interface / deferral-budget coverage tests
extended to 19 implemented verbs. The EVA-4 mlib block
(`Eva4WindowGateTests` / `Eva4ParachuteStateNormalizationTests` / `Eva4MachineTests` /
`Eva4ParamTests` / `Eva4AssertionTests`) holds 38 cells, including every fail-closed leg
of the window gate and the K-consecutive debounce guards; hlib gains an EvaChuteDeploy
deferred-and-capped cell. All headless suites green (xUnit, `harness/lib`,
`harness/provision`, `harness/missions/lib`), and `run.py --id EVA-4-atmo-chute --dry-run`
validates.

**FLIGHT-1 FINDING + FIX (2026-07-24): arming the craft's chute at 2500 m is INERT, not
late - stock refuses to open a parachute at ~300 m/s, so the EVA window could never
open.** The first live EVA-4 run ASSERT-FAILed exactly as the design intended - fast,
self-explaining, no budget burn (107 s wall): `eva-window-missed: altitude 702m fell
below the window floor 800m (vspeed -295.2m/s, situation FLYING, craftChute armed)`,
phasesReached PRELAUNCH/ASCENT/COAST/DESCENT, `apoapsisWindow` met at 19,879 m. The
named-failure design and the per-frame diagnostics did their whole job; the mission
params were wrong.

MEASURED (mission stdout per-frame telemetry, `harness/results/2026-07-24_1907_*`):
peak altitude 11,965 m at ut 60.6 (orbital apoapsis 19,879 m); ASCENT 9.7 s; COAST
40.8 s; the unchuted descent settles at TERMINAL **-301 m/s** by ~2,700 m and holds it;
the chute was armed at **2,382 m / -301 m/s** and 5.1 s later, at 855 m, the rate had
moved **4.7 m/s**. The canopy had not opened at all.

ROOT CAUSE, proved two independent ways rather than inferred. (1) The Parsek recording:
the pod's `.prec` carries ZERO `ParachuteSemiDeployed` / `ParachuteDeployed` part events -
only a `Decoupled` at ut 119.70 (the breakup). (2) Decompiled `ModuleParachute.cs`
(1255-1290): the ACTIVE -> SEMIDEPLOYED transition needs
`automateSafeDeploy >= (int)deploymentSafeState`, and the b1-pad-craft fixture PERSISTS
`automateSafeDeploy = 0` (= open only while SAFE), which `DeploySafe` never reads at
~300 m/s in dense air. An armed chute therefore just WAITS - and a craft already at
terminal velocity never slows on its own, so it waits forever. The arm was inert.

THREE FIXES, each addressing a distinct part of the failure:
- **ARM WHILE SLOW, not at an altitude.** `eva4_decide` now arms on the first DESCENT
  frame whose `|vertical speed|` is within `craftChuteArmMaxRateMps` (30) - i.e. at the
  apoapsis crossing, where `DeploySafe` is trivially SAFE and Kerbin is already ~0.2 atm
  (far over the module's 0.04 atm semi-deploy gate). The COAST -> DESCENT transition now
  FALLS THROUGH into the descent body on the SAME frame instead of returning: the arm
  gate is rate-based and the rate only worsens (~10 m/s per ~1 s poll on Kerbin; measured
  entry sequence -7.4, -16.9, -26.1, -35.5 m/s), so a one-poll delay needlessly eats the
  bound and a few polls would push the craft permanently outside it - the same failure in
  slow motion.
- **RAISE the full-deploy altitude.** The machine sets the craft chutes' stock
  `deployAltitude` (kRPC `Parachute.DeployAltitude`, a PAW tweakable, so player-normal)
  to `craftChuteFullDeployAltMeters` (2500) in the same frame it arms. The fixture
  persists 1000 m and the Mk16's full-deploy animation is SLOW
  (`parachuteMk1.cfg deploymentSpeed = 0.12`, ~8 s), so leaving it at 1000 m would force
  the EVA band under 1000 m with an unmeasured settle distance eating into it. Raising it
  also lengthens the KERBAL's atmospheric descent - the recording surface this scenario
  exists to exercise.
- **GATE ON OBSERVED STATE, NOT ON THE COMMAND.** The window's first conjunct now reads
  the craft's parachute state from kRPC (`ParachuteState`, decompiled
  `KRPC.SpaceCenter.Services.Parts`) and requires `Deployed`. The old conjunct was the
  machine's own "we emitted the deploy action" latch, which was TRUE for the entire failed
  flight - the exact "an action being CALLED is not evidence it HAPPENED" lesson the EVA
  lane already learned twice (BDOCK SEPARATE, EVA-1 ladder release), reached here through
  a mission-machine latch instead of a seam verb. New opt-in telemetry channel
  `TelemetrySnapshot.craft_chute_state` (`KrpcMissionControl(read_chute=True)`, `""`
  unread sentinel = fail-closed, most-deployed-chute-wins across the craft; every other
  mission's snapshot stays byte-identical), and the `eva-window-missed` reason now carries
  the OBSERVED state, so a repeat reads `craftChute=Armed` and names itself.

RE-DERIVED PARAMS: window `[800, 2400] m / |vs| <= 60` -> `[700, 2100] m / |vs| <= 25`
(ceiling 400 m below the full-deploy trigger so the observed-Deployed conjunct can only
become true inside/above the band; floor gives 1400 m of settle room for the ~8 s
animation and still ~700 m of sky for the kerbal, whose own chute fully deploys under
1000 m at ~5-6 m/s so its descent time is bounded by that leg rather than by the exit
altitude; 25 m/s passes the COMPUTED ~8-9 m/s full-canopy touchdown rate (never B1-measured; B1's chute had never opened) with ~2.8x
margin while a merely semi-deployed craft can never satisfy it). BUDGETS re-checked
against the measured fall times: ASCENT 90 and COAST 180 keep B1's values (measured 9.7 s
and 40.8 s); `descentTimeoutSeconds` was raised 240 -> 480, the mission step 600 -> 900
and the runtime 1560 -> 1920, because the craft now flies nearly the whole descent UNDER
CANOPY and its semi-deployed rate was the one number flight 1 could not supply - the
budget was sized to OUT-WAIT a slow one rather than to predict it. (Flight 2 measured that
rate and `descentTimeoutSeconds` went back to 240; see the FLIGHT-2 block below. The
mission-step and runtime budgets stay at 900 / 1920 deliberately - they are wall-clock
envelopes that also absorb KSP boot, scene load and seam latency.) A new
`craftCanopyObserved` assertion row
reports the observed bit alongside the commanded altitude/rate, so the result JSON carries
both halves of the distinction. Tests: the re-tune added the flight-1 regression guards
to the EVA-4 mlib block (`test_shut_when_chute_only_armed`,
`test_window_missed_below_floor_names_the_observed_chute_state`,
`test_coast_to_descent_arms_on_the_transition_frame`,
`test_arm_waits_until_inside_the_rate_bound`) and a kRPC-enum normalization cell.

**SAME-EVIDENCE FINDING, SPUN OFF (not EVA-4's to fix): B1-pad-hop's chute never opens
either.** The live-proven B1 log (`logs/2026-07-20_1829_B1-pad-hop`) shows the identical
shape - its `parachuteSingle` has ZERO `Parachute*` part events, only a `Decoupled` at
ut 119.70, and its recording ends at 65 m. B1 arms at the same 2500 m with the same
`automateSafeDeploy = 0` fixture, so it too impacts at terminal velocity. B1 is GREEN
anyway because `_b1_down_eligible` awards its "chute-deployed impact" DOWN terminal on the
COMMANDED latch (`state.chute_deployed`) plus vessel-lost plus last-altitude, never on an
observed canopy. So B1's documented Parsek surface ("chute-deployed ground-arrival
recording") does not match what it flies. ~~Left alone here because B1 is live-proven and
other work depends on its shape; tracked as its own task.~~ DONE 2026-07-25: that task is
the B1 parachute entry at the top of this file. B1 now arms at the apoapsis crossing and
gates on the OBSERVED canopy, and it was de-listed from live-proven pending its
re-prove.

**FLIGHT-1 TAIL BEHAVIOUR - what actually happened after the ASSERT-FAIL, and why it
matters.** An earlier version of this section said "flight 1 never reached the EVA". That
is FALSE, and it mattered: it is what let the failed attempt's collected artifacts be read
as clean. `run.py` deliberately "drives the REMAINING seam steps regardless of the mission
outcome" (`run.py:1009-1019`), so once the mission ASSERT-FAILed on `eva-window-missed`
the whole seam tail still ran against a pod falling at terminal velocity. Flight 1's own
collected log (`logs/2026-07-24_2210_EVA-4-atmo-chute/KSP.log`, git-state commit
`28a04db4f` = the pre-re-tune build) records it step by step: `evaexit complete
kerbal=Jebediah Kerman released=true`, then `evachutedeploy start alt=356.4 vspeed=-277.3
situation=FLYING`, `evachutedeploy armed`, `canopy verified state=SemiDeployed alt=221.4
vspeed=-258.2`, and 19 s later `evachutedeploy complete canopy=true chuteState=Cut
down=true situation=LANDED alive=true`. Then `stoprecording stopped=true`, `committree
committed=true`, `flushandquit`. Three consequences:

- **A window-missed run performs the very terminal-velocity hatch EVA the design says
  cannot happen.** The whole point of the mission's bounded envelope is that the seam's
  irreversible `EvaExit` fires into a verified-safe state; on a window-missed run it fires
  into whatever state the pod is in when the mission gives up. Jebediah happened to
  survive (the stock kerbalEVA chute semi-deployed at 221 m and its 50 m/s crash tolerance
  did the rest), but that was luck, not contract.
- **Worst case the tail burns ~120 + 420 s of named deferral budget per failed attempt.**
  If the kerbal dies, `EvaExit` runs out its 120 s budget to `eva-exit-timeout` and
  `EvaChuteDeploy` then defers its full 420 s on `not-eva` - so a fast, self-explaining
  107 s mission failure can still cost ~9 more minutes of wall clock before the run ends.
- **The failed attempt's collected save and log carry a spurious EVA branch + landing.**
  That is what contaminated the record here: a run whose mission never reached the handoff
  still produced an EVA tree branch, a kerbal recording, a chuted landing and a committed
  tree, all of which read like success in the artifacts.

There is NO false PASS. The mission's ASSERT-FAIL classifies the whole scenario INVALID
(`hlib.classify_mission_step`) BEFORE the tail runs - flight 1's own result JSON is
`verdict=INVALID subkind=mission` - and the save is re-staged per attempt (`stage_fixture`
does an `rmtree` + `copytree` of the template, `run.py:1995`), so no junk state survives
into the next run. The problem is cost and artifact honesty, not verdict correctness.
FIXING IT WAS NOT EVA-4's JOB: "drive the remaining steps regardless" is a cross-cutting
harness contract shared by every autopilot scenario, so it was filed separately and fixed
there - see the next entry. The artifacts of the flight-1 attempt still contain a real but
unintended EVA; no future window-missed attempt will.

~~**HARNESS: an UNMET mission step no longer drives the world-mutating seam tail.**~~
FIXED 2026-07-25 (the cross-cutting fix filed by the entry above). After a mission step
comes back UNMET, `run.py` now drives the CLEANUP tail steps ONLY and skips the rest,
writing no channel line for a skipped step. Mechanism (general, not an EVA-4 special
case): every implemented seam verb carries a TAIL ROLE in `hlib.SEAM_VERB_TAIL_ROLE`
alongside the existing per-verb tables - `cleanup` (`StopRecording`, `FlushAndQuit`),
`inert` (`RecordingState`, `MissionMark`), `world-mutating` (everything else). The unmet
tail runs `cleanup` only; an UNKNOWN verb resolves to `world-mutating` (fail-safe), and a
unit cell asserts the table is TOTAL over `IMPLEMENTED_SEAM_VERBS`, so a verb promoted
from RESERVED forces an explicit role decision - that gate fired on `EvaChuteDeploy` while
merging PR #1348, exactly as intended. Pure decision:
`hlib.plan_unmet_mission_tail(steps, mission_index, skip_tail)`. Spec opt-out
`[driver].skipTailOnUnmetMission` defaults to the safe `true`, must be a bool, and warns
as inert on a seam-kind driver.
- **Why `FlushAndQuit` and `StopRecording` are cleanup:** skipping the quit would let the
  watchdog kill the tree, and KILLED outranks driver-INVALID in `classify_verdict`, so the
  mission's own subkind would be MASKED and the attempt would stop being retryable;
  `StopRecording` closes the recording so the recorder flushes instead of being torn down
  mid-sample by the quit, and so the collected log's recording markers pair. Two scope
  limits kept honest after review: the run's own logValidate is already SKIPPED on
  driver-invalid (artifact honesty, not verdict), and EVA-4 is today the ONLY scenario
  that can reach this role - six specs carry a `StopRecording` step (EVA-1/2/3, S0.5,
  S0.6, EVA-4) but the first five are seam-kind with no mission step, and every other
  autopilot scenario ends without one, so on their unmet runs the recorder is live at
  quit either way.
- **Why `CommitTree` is NOT cleanup** (the one genuinely two-sided call): committing writes
  the failed attempt's junk tree into the durable committed set and applies its resource
  deltas, and it cannot buy the run a verdict back - an unmet mission is already
  driver-INVALID at `classify_verdict`'s "driver stage failed" branch, which precedes
  EVERY save-reading verifier in that chain, so on
  that path the analyzer is triage-only and logValidate / testResults / anomalySweep /
  expectations / ledgerOracle are all SKIPPED whether or not the tree was committed. The
  "an uncommitted tree the analyzer then reports on" worry does not arise: the analyzer
  never reports verdict-driving findings there. `verifiers.unmetMissionTail` records the
  skip next to those SKIPPED rows so the thin save is self-explaining.
- **Scope:** only the UNMET path changed. A MISSION-OK run drives the full tail exactly as
  before (all eleven autopilot scenarios: B1/B2/B4/B5/B6/B7, BDOCK-1, EVA-4 itself, and
  the three FORGE specs), and seam-only drivers (S0.5/S0.6/
  S1.4/S1.5/S4.1, H5/H6, B10, the L1 six-pack, EVA-1/2/3) have no mission step at all.
  Verified empirically in review by running both trees side by side: a MET autopilot run
  and a seam-only run produce BYTE-IDENTICAL result records on `origin/main` and on the
  branch, and `validate_spec` over all 28 committed scenarios yields an identical
  error+warning list on both.
- **NON-CLAIM (review correction):** skipping `SaveGame` does NOT protect the FORGE
  fixture path, and the first version of this entry wrongly said it did. `FlushAndQuit`
  ITSELF forces `GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, OVERWRITE)`
  (`ParsekTestCommandAddon.FlushAndQuitImpl`), the SAME slot all three FORGE specs'
  `SaveGame` step targets, so a half-forged state reaches disk either way. See the
  separate FORGE-harvest entry below. The `CommitTree` half of the argument is unaffected:
  `FlushAndQuit` deliberately never auto-commits an in-flight recorder
  (`TestCommandFlushAndQuit`), so skipping `CommitTree` genuinely leaves the junk tree
  uncommitted.
- Tests (34 new cells): 26 pure hlib cells (role-table totality + no stale rows, the
  cleanup set, fail-safe unknown, id stability, the opt-out, the spec surface incl. the
  misplaced-key guard, and a DATA-DRIVEN sweep over every committed autopilot spec
  loaded from disk - all 11 - asserting each keeps its QUIT owner and drives nothing
  outside the cleanup set, so the coverage cannot go stale when a scenario is added or
  its tail edited) + 6 fake-KSP smoke cells driving the REAL EVA-4 tail
  shape (`EvaExit` + `EvaChuteDeploy` + `StopRecording` + `CommitTree` + `FlushAndQuit`)
  and asserting on the COMMAND CHANNEL FILE - the only artifact that proves a command was
  never sent - that neither in-world verb nor the commit was written on an unmet run while
  `StopRecording` / `FlushAndQuit` were, that a MISSION-OK run still drives everything,
  that the opt-out restores the legacy tail and is visible in the record, and that the
  never-spawned UNMET paths (load-failed, no-result) skip too + 2 mission-step cells:
  the third never-spawned UNMET path (the in-flight venv backstop, unmet with zero
  spawns) and the proof that a RUN-budget kill drives NO tail at all, cleanup included
  (it is not an unmet-tail case: the KSP tree is already dead).
  Mutation-tested during review: 17 single-point mutations
  of the production code (every-step-runs, CommitTree->cleanup, FlushAndQuit->mutating,
  unknown-verb-defaults-cleanup, delete the skip branch, and 12 more), zero survivors.

**HARNESS (latent, surfaced by the review above): a FORGE run whose mission fails still
stamps its save, and nothing gates the harvest.** Not introduced by the tail-skip work and
not made worse by it, but the tail skip does NOT cover it, so it is filed rather than
assumed handled. `FlushAndQuit` force-saves the `persistent` slot
(`ParsekTestCommandAddon.FlushAndQuitImpl` -> `GamePersistence.SaveGame("persistent",
HighLogic.SaveFolder, OVERWRITE)`) on every path, which is exactly the slot
`FORGE-bdock-station` / `FORGE-eva3-pad` / `FORGE-eva2-lko` target with their `SaveGame`
step and exactly the file `harness/tools/harvest_bdock_station.py` reads. So a forge whose
mission ASSERT-FAILs part-way through assembly still leaves a half-forged
`saves/<runSave>/persistent.sfs` on disk. The harvest's only always-on gates are
"activeVessel present" + ">= 1 VESSEL node"; its `--expect-situation` gate is OPTIONAL and
only the ORBITAL harvest documents using it (`--expect-situation ORBITING`), so a pad
forge would harvest a half-forged state without complaint. Fix (not attempted here): make
the harvest refuse unless the run it is harvesting from ended MISSION-OK - e.g. read the
forge run's result JSON, or require `--expect-situation` for every harvest. Cheap and
worth doing before the next fixture mint; until then, only harvest a forge run you have
confirmed PASSED.

**FLIGHT 2 (2026-07-24): FULL PASS on attempt 1.** All seven verifiers PASS/SKIPPED
(`batchComplete` SKIPPED as designed, everything else PASS), and the re-tune
worked end to end. The machine walked PRELAUNCH -> ASCENT -> COAST -> DESCENT ->
EVA-WINDOW with all four telemetry assertions met (`apoapsisWindow` 19,747 m,
`evaWindowReached` 1,606 m, `evaWindowDescentRate` -23.2 m/s, `craftCanopyObserved` armed
at 11,965 m and OBSERVED Deployed), then the seam chain ran: `EvaExit`, `EvaChuteDeploy`
armed with the canopy VERIFIED, a steady -4.5 m/s chuted descent, `ParachuteCut` at
touchdown, `down=true situation=LANDED alive=true`, `StopRecording`, `CommitTree`.
Analyzer red=0, logValidate PASS, anomalySweep PASS, expectations PASS. Parsek recorded
the mid-flight EVA branch and Atmospheric TrackSections on the kerbal's own recording -
the four surfaces this scenario exists for. Operator list resolved:
- **P1 recordings count: PINNED 3** (was the provisional 2-10). Measured 3 `.prec` in
  `saves/b1-pad-craft/Parsek/Recordings`: pod root + kerbal EVA split + the pod's post-EVA
  continuation. Exact rather than a window, because `logContracts` are presence-only
  (`hlib.evaluate_expectations` uses `re.search`) so the count is the only numeric guard
  that the EVA split and the kerbal recording exist. Re-pin (never widen) on a legitimate
  future difference; B1's breakup-child count is documented as varying.
- **P2 `'kerbalEVA` part-name prefix: CONFIRMED** in both Part-event tokens.
- **P3 semi-deployed descent rate: MEASURED, and `descentTimeoutSeconds` TRIMMED
  480 -> 240.** The semi-deployed craft does not crawl: it sinks at up to **-236 m/s**
  (peak magnitude at ~5.7 km), the rate decaying with air density to -223 m/s at the
  2500 m full-deploy trigger, after which the full canopy brakes it to -23 m/s in ~5.6 s.
  The whole DESCENT phase (ut 60.9 -> 122.5) took **61.6 s**, so 240 s is ~3.9x the
  measured phase and restores the pre-re-tune value. The budget is a BACKSTOP anyway -
  every realistic canopy failure is caught first, and by name, by `eva-window-missed` at
  the 700 m floor (flight 1 proved that: 107 s wall, no budget burn) - so the shorter
  bound only makes the pathological "airborne but never satisfying the window" stall
  report sooner. The mission-step (900) and runtime (1920) budgets are deliberately NOT
  trimmed with it: they are wall-clock envelopes that also cover KSP boot, scene load and
  seam latency, and the measured flight-2 numbers (mission wall 112.5 s, scenario wall
  402 s) already sit far inside them.
- **P4 kerbal canopy + survival: CONFIRMED** (`canopy=true`, `down=true situation=LANDED
  alive=true`).
Also resolved by flight 1 and unchanged: the hop profile (peak 11,965 m, unchuted terminal
-301 m/s) and the ascent / coast durations (9.7 s / 40.8 s).

**POST-LIVE HARDENINGS (review follow-up, same branch).** Two in-family fixes that the
live run could not have surfaced because neither failure mode fired:
- **The EVA-window gate is now K=2 debounced** (`mlib.EVA4_WINDOW_DEBOUNCE_K`, the house
  `DEFAULT_DEBOUNCE_K` idiom). It previously opened on ONE poll, and two of its five
  conjuncts are single-sample kRPC reads: stock flips `ParachuteState` to `DEPLOYED` at
  the START of the full-deploy ANIMATION (decompiled `ModuleParachute.cs:1372-1380`), so
  through the ~8 s the Mk16 canopy takes to bite, only the `|vertical speed|` conjunct -
  read from the SAME frame - separates "observed full canopy" from "still fast". A single
  glitched frame would therefore have certified a terminal-velocity EVA green, and the
  `evaWindowDescentRate` assertion cannot catch it because it re-reports that same frame.
  Two independent frames must now agree; the streak resets to 0 on any disagreeing frame,
  the `eva-window-missed` floor check is unchanged (a craft sinking mid-streak still reds
  by name), and the cost is ~10-20 m of the 1400 m band at the measured handoff rate. The
  run is also surfaced as `evaWindowStreak` in the machine-state gate diff.
- **`EvaChuteDeploy` completion now requires the RAW per-poll aliveness read for
  `CompleteOk`**, keeping the 3-poll debounced bit for `KerbalLost` only. With `awaitDown
  =false` the canopy latch plus a settled scene satisfy every other conjunct, so a kerbal
  dying INSIDE the debounce window would have completed OK on the very poll the loss
  began - "KerbalLost beats everything" only held AFTER the debounce expired. Not
  reachable in the shipped spec (EVA-4 pins `awaitDown=true`), fixed properly anyway.
Tests: +4 mlib cells (the K-consecutive requirement, the streak reset, the mid-streak
window-missed guard, and the happy path re-shaped to need two agreeing frames) and +3
xUnit cells (no-OK on the first gone-read for both `awaitDown` shapes, loss wins once the
debounce expires, honest timeouts mid-debounce).

**SUITE-WIDE LOG-FORMAT NOTE (from the flight-1 re-tune, recorded here because it is easy
to miss):** the mission telemetry stdout line gained a `chute=` field for EVERY mission
(`mission_runner.py`), not just EVA-4. The `TelemetrySnapshot` itself is unchanged for
every other mission (`read_chute` defaults off, and the unread sentinel is `""` which
renders as `-`), so no other mission's DECISIONS moved - but any tooling that parses that
line positionally sees a new column.

## Scenario coverage expansion - Tier 0/1 seam cells over the gloops fixture [BUILT, branch `autotest-tier01-scenarios`]

Three new `harness/scenarios/*.toml` specs, all seam-driven (no autopilot, no new fixtures) over the existing `fixtures/saves/gloops-airshow` SANDBOX pod, so they run on `stock-minimal` today with only the implemented seam verbs. Each validates through the real `CommittedSpecValidationTests` path and `--dry-run`s clean:

- `S0.5-live-record-discard` (inject none): LoadGame -> StartRecording -> RecordingState -> StopRecording -> DiscardTree. The first RUNNABLE live-recording cell (B1/B2 are PENDING-OPERATOR), so it exercises REC-001/REC-003 UNSUPPRESSED (`spec_expects_live_recording` True via StartRecording), plus D1 `discard-rollback`. Guards: paired start/stop markers + `discardtree discarded=true` + the store returns to zero. Robust to the stationary-pod sub-2-point-drop because discard zeroes the store either way.
- `S0.6-live-record-commit` (inject all-synthetic, 272): same live start/stop then CommitTree on top of the corpus. Guards: paired markers + `committree committed=true` + a corpus floor (`count` `{272,273}`; the +1 tolerates the sub-2-point-drop, the 272 floor proves the commit did not lose the existing corpus). Covers D5 `single-node`, D16 `sidecar-prec`.
- `H5-invariants-corpus` (inject all-synthetic, 272): RunTests over the in-game `RecordingInvariants` (H5) category, walking the LIVE 272-recording store through the M-A1 invariant core (distinct from B10, which runs H5 over an EMPTY store). Guards: `RecordingInvariants walk: recordings=272` (corpus hydrated intact, R21/R22 index-integrity family) + `BATCH_COMPLETE ... failed=0`. Non-live, so REC rules are correctly suppressed. Covers D16 `sidecar-prec`/`schema-gate`. PENDING-OPERATOR: the first live run must confirm the 272-corpus passes the invariant core with zero Fail findings; a deliberately degenerate synthetic recording would need quarantine via `expectedFail`, not a weakened `failed=0`.

Candidates evaluated and DROPPED (each names the missing capability, to feed verb/fixture prioritization):
- Cold-load passive-safety variant of B10 with a ledger oracle on the gloops fixture - DROPPED. The gloops save is SANDBOX; `_capture_seed_baseline` (run.py) returns `invalid-fixture` (all `hasFunds`/`hasScience`/`hasRep` false) for any `[expectations.ledger]` with `seedFrom="template"`, so the run would be terminal-INVALID. Blocker: no committed career fixture save (only the SANDBOX gloops save ships), and B10's own `fixtures/saves/fresh-career` does not exist on disk.
- Standalone commit-produces-exactly-one-recording cell - DROPPED as a hard `count={1,1}` assertion. Blocker: the gloops pod is a motionless PRELAUNCH craft and there is no Warp/Wait seam verb, so a near-instant Start/Stop can finalize to <2 points and be dropped (default max sample interval 3.0s). Absorbed into S0.6 as a tolerant `{272,273}` range instead. A `Warp`/`WaitSeconds` verb or a moving-vessel fixture would unblock the exact-count form.
- Dedicated RecordingState query-contract spec - DROPPED as too thin to warrant a boot: the seam only asserts the OK verdict in v1 (the recording/tree/points payload is not harness-asserted), so it was folded into S0.5 as an in-flight probe step instead.

## B9 rewindable-tree fixture + first rewind scenarios (S4.1 / S1.5) [BUILT, branch `autotest-b9-scenarios`]

The `rewind-b9` injection preset + the M-C1 InvokeRewind/AnswerMergeDialog/TimeJump verbs' first scenarios, satisfying the design's "B9 fixture RP-usability prerequisites" (`design-autotest-seam-verbs-c1.md`).

**Fixture capability.** `Source/Parsek.Tests/Generators/RewindB9Fixture.cs` assembles a committed tree (root ascent + surviving upper stage slot 0 + CRASHED booster slot 1, `TerminalState.Destroyed`) plus a Rewind-to-Separation `RewindPoint` `rp_b9_root`, satisfying all three CanInvoke prerequisites the fixture owns: (1) a fixed known RP id whose quicksave sidecar is written on disk at `Parsek/RewindPoints/rp_b9_root.sfs`; (2) `CreatingSessionId` null so `LoadTimeSweep` keeps it (not a session-scoped provisional); (3) `ChildSlots`/`PidSlotMap`/`RootPartPidMap` keyed to the synthetic vessel + root-part pids the injected recordings carry (via `ScenarioWriter.DeriveVesselPersistentId` / `DeriveRootPartPersistentId`, so `slot=1` resolves). `ScenarioWriter` gained `AddRewindPoint` (emits the `REWIND_POINTS`/`POINT` block matching `ParsekScenario.SaveStagingList`), `WriteRewindPointSaveFiles`, and the two Derive*Id seams. **RP quicksave sidecar (B1 fix):** the sidecar is NOT a bare copy of the host save - the re-fly's pre-load selected-slot scrub (`RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly`) and post-load `PostLoadStripper` both KEEP only vessels whose `persistentId` the RP's PidSlotMap/RootPartPidMap references and STRIP the rest, so a vessel-less or unmatched-pid quicksave makes the scrub keep nothing and the Activate fail ("selected vessel not present on reload"). `WriteRewindPointSaveFiles` therefore rewrites the sidecar's FLIGHTSTATE to hold exactly one controllable VESSEL per child slot, each stamped with the slot's mapped vessel- + root-part pids and cloned from the host save's own command vessel when present (real parts, so the CanInvoke deep-parse gate resolves them), with `activeVessel` at the focus slot. The tree/RP/sidecar pid triangle is thus consistent (PidSlotMap pids == sidecar VESSEL pids == recording pids), and strip/restore/marker/merge are genuinely exercisable. **Honest limit is now OPERATOR-VERIFIABLE, not structural:** whether KSP can LOAD the cloned vessels live (duplicate part pids / crew across the per-slot clones may be regenerated on load) is confirmed only when an operator boots the B9 split in FLIGHT. Wired as a named injection preset (`injectedRecordings = "rewind-b9"` -> `hlib.INJECTED_RECORDINGS`; `run.py` stage branch -> `inject-recordings.ps1 -Preset rewind-b9` -> `dotnet test --filter InjectRewindB9`). `InjectRewindB9` defaults to a distinct `rewind-b9-fixture` save name so a bare `dotnet test` no-ops instead of clobbering the shared `test career` corpus; `inject-recordings.ps1` carries the matching per-preset default save so a manual `-Preset rewind-b9` cannot purge the corpus either. Tests: `RewindB9FixtureTests` (7 cells: RP node shape, null-session, two controllable child slots, PidSlotMap-matches-injected-pids, `REWIND_POINTS` emit + `RewindPoint.LoadFrom` round-trip, crashed-sibling `terminalState=4` + serialized pid, RP sidecar carries a VESSEL per slot with the mapped pid + activeVessel, deep-parse passes when parts resolve / declines when they do not).

**Scenarios (both validate via `CommittedSpecValidationTests` + `--dry-run` clean). NOTE 2026-07-26 (`rewind-loop-lane`): both were `tier = operator` on a FALSE premise and are now `tier = nightly` - see the rewind-in-flight entry at the top of this file. The operator-tier and PENDING-OPERATOR wording in the rest of THIS entry is superseded; the corrections are called out inline below.**
- `S4.1-rewind-merge` (inject rewind-b9): the B9 worked example - LoadGame -> SetSetting(autoRecordOnLaunch off) -> InvokeRewind{rp=rp_b9_root, slot=1} -> AnswerMergeDialog{merge} (the folded verb drives the exit) -> RecordingState -> FlushAndQuit. Headless-checkable guards: the `Re-Fly (Rewind-to-Separation) StartInvoke` + `Invocation complete` log contracts (proving the re-fly ran end-to-end), no `[ERROR]`, and a recording-count floor. D9 = rewind-to-separation/refly-gate/reconciliation-bundle/read-back-guard/head-tip-split/supersede-relation/terminal-kind-classify/merge-journal (NOT tombstones - uncrewed booster; NOT revert-during-refly-dialog - we merge), D8 = recalc-engine. An `[expectations.rewind]` block carries the save-parse asserts. SUPERSEDED 2026-07-31 on two counts: the block is no longer RESERVED/SKIPPED (the M-C2 verifier landed and S4.1 ARMS it with `gating = true`, the first committed spec to do so), and the assert is `max = 0` supersede rows, not `>= 1` - S4.1 flies no re-flight, so the correct assertion is that the merge writes ZERO rows and still completes.
- `S1.5-rewind-loop` (inject rewind-b9): the DRIVABLE SUBSET of the catalog S1.5 - TimeJump{deltaSeconds=600} past the injected recording EndUTs to spawn the end-of-recording ghost, then rewind-strip-respawn via the seam using rp_b9_root as the quicksave-equivalent. D6 time-jump, D18 time-jump-observables/rewind-strip-respawn-cycle, D8 epoch-isolation/recalc-engine, D9 rewind-to-separation/refly-gate. ~~**DEPENDENCY: do NOT schedule before the `autotest-integration-fixes` PR merges** - the TimeJump leg relies on that branch's TimeJump completion-decider fix; until it lands TimeJump can settle incorrectly and the leg is untrustworthy.~~ CLOSED 2026-07-26: that is PR #1322, merged as `eb94607dd`. **Budget (S8):** `budgetSeconds = 2400` is sized for the WORST DEFER path (`sum of run.py per-step waits = LoadGame 660 + SetSetting 60 + TimeJump 660 + RecordingState 60 + InvokeRewind 660 + AnswerMergeDialog 120 + RecordingState 60 + FlushAndQuit 60 = 2340; +60 margin`) so an unattended defer surfaces a clean per-step TIMEOUT instead of a run-budget KILL. S4.1's `budgetSeconds = 1680` is sized the same way (no TimeJump step).

**S1.5 capability gap (why only the subset is drivable).** The FULL catalog S1.5 ("fly B1, commit, warp past EndUT, quicksave, rewind, assert stripped/respawned, crew re-reservation + resource reset vs ledger oracle") needs four capabilities M-C1 does not provide: (a) a live B1 flight + commit -> the mission-autopilot library, not a seam verb (the `rewind-loop-lane` `r1_rewind_loop` mission is the first cut of this); (b) an in-scene quicksave / RP-create seam verb to make the RP LIVE rather than inject it (the injected `rp_b9_root` stands in) - RE-DIAGNOSED 2026-07-26: creating the RP is only half of it, NAMING it is the other half and is the harder half; see blockers (a)/(b) in the rewind-in-flight entry at the top; ~~(c) a FLIGHT-scene entry -- TimeJump/InvokeRewind are `RequiresFlight` and the seam has no launch verb, so an operator must be in FLIGHT (the gloops host save loads to SPACECENTER)~~ **(c) WAS NEVER TRUE and is WITHDRAWN 2026-07-26** - `LoadGame` is the flight-entry verb and `gloops-airshow` carries `activeVessel = 1`, so it loads to FLIGHT, not SPACECENTER; (d) a career fixture + ledger oracle for the crew-re-reservation / resource-reset asserts (rewind-b9's host is sandbox). The highest-value remaining unblocker is a seam channel exposing live RewindPoint ids, NOT a FLIGHT-scene B9 template.

**PENDING-OPERATOR (coordinator runbook).** ~~Both scenarios are `tier = operator` ... otherwise every RequiresFlight verb only defers to a TIMEOUT and no PASS is reachable.~~ SUPERSEDED 2026-07-26: both are `tier = nightly` and run unattended. With the B1 sidecar fix the re-fly is genuinely exercisable end to end (the selected slot's vessel is present under its mapped pid, survives the strip, and is activatable); what the FIRST nightly run establishes is the LIVE-LOAD fidelity of the cloned vessels (whether KSP can instantiate them - duplicate part pids / crew across the per-slot clones may be regenerated on load). Still genuinely PENDING beyond that: the supersede-relation / tombstone save-parse (S4.1) is PENDING-VERIFIER, not pending-operator - the `[expectations.rewind]` block is RESERVED until the M-C2 rewind verifier lands (UPDATE 2026-07-31: LANDED report-only, branch `claude/r9-save-parse-verifier-tshhzv`; the asserts now produce recorded `saveParse` readings each run, and what remained was the gating promotion after one local S4.1 run. UPDATE 2026-07-31, same day: that promotion SHIPPED on branch `r9-arm-s41` - reading run `2026-07-31_1628` confirmed the readings matched the declared windows, S4.1's block now carries `gating = true`, and the armed gate was proven live both ways (`_1635` PASS armed, `_1637` PARSEK-FAIL(save-structure) under a `min = 1` negative control). S4.1's supersede-row / tombstone assert is therefore no longer PENDING-VERIFIER at all; S1.5's career-fixture gap below is untouched - see the roadmap R9 entry) - and S1.5's crew-re-reservation / resource-reset asserts need a career fixture the sandbox host does not provide.

## M-B3 - L1 ledger action scripts: first nonzero seam-declared manifests + career fixtures [BUILT, branch `autotest-mb3-impl`]

Implements `docs/dev/design-autotest-ledger-scripts-b3.md` (merged #1321). The FIRST-EVER nonzero `[[expectations.ledger.manifest]]` scenarios: six L1 scripts driving exactly one career-effecting M-C1 `KscAction` sub-action each and asserting the touched HARD pool moved by exactly the declared author constant (the seam-declared-vs-save diff is the SOLE trusted leg). No new code, no oracle math, no manifest schema, no `STOCK_AWARD_PATTERNS` change - declarative specs + fixtures + tests against the frozen M-B2/M-C1/M-A5 surfaces. Suites: lib 349 (+15 in `test_ledger_scripts_b3.py`); all six specs pass `CommittedSpecValidationTests` + `--dry-run` exit 0; `--cadence daily --dry-run` schedules none of them (all `tier = "pending-fixture"`).

**~~Boot-contract fix (2026-07-23, branch `autotest-career-fixtures`): the no-vessel LoadGame route must write persistent.sfs before Start().~~ DONE.** The first live ledger batch found B10 + L1-passive-sandbox PASS but the 5 ACTING L1 cases (research-node-career/-science, dismiss-kerbal-career, upgrade-facility-career) RED on the missing `Game state: TechResearched` / `CrewRemoved` / `FacilityUpgraded` recorded-action log line - though the ledger oracle PASSED (Parsek's career math is correct; only the RECORDING layer failed to log). Root cause: `ParsekScenario.OnLoad` never ran, so the `GameStateRecorder` never subscribed to `OnTechnologyResearched` before the seam issued its `KscAction`. The no-vessel SPACECENTER route in `ParsekTestCommandAddon.LoadGameImpl` boots via `Game.Start()` -> `HighLogic.LoadScene(SPACECENTER)`, and the KSC scene bootstrap `SpaceCenterMain.Start()` RE-READS `persistent.sfs` FROM DISK (`GamePersistence.LoadGame("persistent", HighLogic.SaveFolder, ...)`) then `game.Load()` -> `ScenarioRunner.SetProtoModules` on THAT disk game - it never touches the in-memory `HighLogic.CurrentGame`. So the prior `UpdateScenarioModules(game)` mutated a game the KSC scene discards; the stripped fresh-* fixtures (no `ParsekScenario` node) booted to KSC with no ParsekScenario, `SetProtoModules` never instantiated it, `OnLoad` never ran. Fix: insert `GamePersistence.SaveGame(game, "persistent", save, SaveMode.OVERWRITE)` between `UpdateScenarioModules` and `Start()`, EXACTLY matching stock `MainMenu.OnLoadDialogPipelineFinished` (KSP 1.12.5), so the disk re-read carries the augmented game with ParsekScenario. The `onGameStatePostLoad.Fire(node)` step in the stock sequence is NOT replicated (nothing in Parsek subscribes to it; it is not load-bearing for scenario instantiation, and the route loads from file so no ConfigNode is in hand). Writing the run save's persistent.sfs is safe: it is staged/disposable and the seed baseline was captured pre-launch from the TEMPLATE. The focusable-FLIGHT route is unchanged (Game.Load runs on the in-memory game via FlightDriver.Start). Runtime-only change; no new pure decider. xUnit 18470 green.

**The six L1 specs (`harness/scenarios/`, all `tier = "pending-fixture"` until the fixtures land):**
- `L1-research-node-career` - research `basicRocketry`; `tech-unlock science = -5.0` author constant (VERIFY-PENDING-OPERATOR), science pool drops by the node cost.
- `L1-upgrade-facility-career` - upgrade a NON-R&D facility (Tracking Station); `facility-upgrade funds = -150000.0` (VERIFY-PENDING-OPERATOR), funds drop by the per-level cost.
- `L1-hire-kerbal-career` - hire one applicant; `kerbal-hire funds = -24000.0` fixture-pinned at hired-count 4 (VERIFY-PENDING-OPERATOR).
- `L1-dismiss-kerbal-career` - dismiss one roster kerbal; `kerbal-dismiss` all-zero pools -> pool-neutrality guard (empty-capture leg TRUSTED here like B10).
- `L1-research-node-science` - Science-mode research; science-only assertion. **DEPENDENCY (a SEPARATE sibling lane, NOT this one):** the M-C1 `research-node` sub-action readiness widening to `(Mode == CAREER || Mode == SCIENCE_SANDBOX) && ResearchAndDevelopment.Instance != null` (OQ1, plan of record); until it lands the verb DEFERS `career-not-ready` in Science and passive-only Science is the temporary fallback.
- `L1-passive-sandbox` - pure B10 variant, NO KscAction step (CAREER-gated verbs would DEFER in SANDBOX); empty manifest, all pools absent, any-capture-reds trusted.

**Deferred (design, NOT M-B3 script work):** the L2 facility-refund-window (R6) is deferred behind a named `SaveGame` batch-2 seam verb (a sibling lane implements that + the Science readiness widen; L2/R6 stays deferred entirely). The other L1 items with no seam verb - complete-contract / strategy (batch-2 `accept-contract` / `complete-contract` / `activate-strategy` sub-actions), milestone (flown B-track + `ProgressTracking` pattern), EVA science (B3 + the roster world sub-facet) - are named-deferred. The nonzero capture leg stays a no-op for these scenarios - originally read as "shipped patterns dead against real EN text", corrected 2026-07-29 to the permanent reason: KSP writes no funds and no science award line at all, so a funds-only or science-only scenario captures nothing whatever the table says (see entry 1b). Trusting it on those facets is not deferred, it is impossible; only a reputation-producing scenario can ever exercise the capture leg.

**~~PENDING-OPERATOR - create the three career fixtures~~ DONE (2026-07-23, FILE-CONSTRUCTED headlessly, branch `autotest-career-fixtures`).** The operator-principle resolution: the fixtures are built by FILE CONSTRUCTION (trim/reset the cleanest existing CAREER dev save `test career` -> pure-stock clean-slate KSC via a brace-aware ConfigNode transform), NOT by piloting KSP. Committed: `harness/fixtures/saves/{fresh-career,fresh-science,fresh-sandbox}` (each `persistent.sfs` + `.loadmeta` + `AddOns/DistantObject/Settings.cfg`; no Parsek footprint - no `Parsek/` dir, no `ParsekScenario` node, no `ParsekSettings` custom-param; default career difficulty multipliers all 1.0; third-party `Trajectories`/`Tracking_Persistence` scenarios stripped; `start` tech node parts reduced to pure base-stock). Validated headlessly: brace-balanced + parseable; the REAL pre-launch seed-baseline path (`analyze-recordings.ps1` over each staged template) produces `careerSave parsed=true` with the pinned pools (fresh-career funds=500000 sci=100 rep=0, fresh-science sci=100 funds/rep absent, fresh-sandbox all absent) and RED=0; `test_ledger_scripts_b3` + lib sweep (397) green. Pinned values + author-constant status recorded in `harness/fixtures/saves/README.md`. **basicRocketry=5 is VERIFIED** (the source save's Tech node carried `cost = 5`); hire -24000 + upgrade -150000 stay VERIFY-PENDING-OPERATOR (read `observedAfter=` on the first live run). L1-hire step arg reconciled `FIXTURE_APPLICANT -> "Verhat Kerman"`; L1-dismiss `Bill Kerman` already in roster. **Re-tier held:** all 7 stay `pending-fixture`; the first green live run (named headless-boot follow-up) re-tiers to daily (couples re-tier with confirm-green, avoids the never-run-daily self-quarantine of item 4). **NEW BLOCKER for L1-passive-sandbox:** a SANDBOX template has no pools, and `run.py::_capture_seed_baseline` terminal-INVALIDs any `[expectations.ledger]` scenario whose template parses with all pools absent (`invalid-fixture`) - the fixture is correct but the seed gate blocks it before boot. Resolve by teaching the seed gate to accept a no-pools template when the manifest is EMPTY (expected==seed==all-absent, the facet-skip path the spec already assumes; `oracle` math agrees - `test_passive_sandbox_empty_manifest_expected_equals_absent_seed` passes) OR by removing the `[expectations.ledger]` block from `L1-passive-sandbox` (runs then as a pure recording-invariants passivity proof). Original operator instructions retained below for the live-run follow-up:

On a provisioned stock-minimal EN KSP 1.12.5 instance, per the design Data Model fixture rules. Each fixture is a fresh stock save trimmed of everything nondeterministic (no craft in flight, no active vessels, no in-progress contracts, no unlocked tech beyond the mode's default start node, all facilities at level 0), then stamped with the recording schema generation (M-A4) and registered synthetic-provenance:

1. **`fixtures/saves/fresh-career`** (GAME node `Mode = CAREER`; also closes B10's dangling reference - B10 and the L1-career scripts SHARE this one save). Seed the `Funding` / `ResearchAndDevelopment` / `Reputation` SCENARIO nodes to KNOWN values: funds `500000` (well above the 150000 facility upgrade + the hire), science `100.0` (above the 5-science node), reputation `0.0`. Pin the roster to EXACTLY the 4 stock hired kerbals (Jeb/Bill/Bob/Val), 0 assigned, ZERO Parsek reservations / stand-ins, PLUS 1 known applicant available to hire. Load-scene = SPACECENTER (facility upgrade needs it). Record in a fixture README: (a) the pinned hired-crew count (4) + the resulting hire cost read off the `kscaction action=hire-kerbal applied=true observedAfter=` line (the `L1-hire-kerbal-career` author constant; if the hired roster drifts the recruit-cost curve input changes and the constant goes stale); (b) the chosen non-R&D facility (Tracking Station) + its level 0->1 cost read off `kscaction action=upgrade-facility applied=true observedAfter=` (the `L1-upgrade-facility-career` author constant; if it turns out GameVariables-scaled, treat as fixture-pinned); (c) the `basicRocketry` node science cost read during creation (the research author constant); (d) the applicant name + a dismissable kerbal name to reconcile the `kerbal = "FIXTURE_APPLICANT"` / `kerbal = "Bill Kerman"` step args to the real fixture.
2. **`fixtures/saves/fresh-science`** (GAME node `Mode = SCIENCE_SANDBOX`; no funds/rep pool - `CareerSaveParser` sets `hasFunds`/`hasRep` false). Seed science `100.0`. Stock starting kerbals. Used by `L1-research-node-science` only (the science-facet single-module script), and only once the M-C1 `research-node` readiness widening lands.
3. **`fixtures/saves/fresh-sandbox`** (GAME node `Mode = SANDBOX`; no economy pool at all - every `hasX` false). Stock starting kerbals. Used by `L1-passive-sandbox` (a pure B10 passive variant; NO KscAction driven - CAREER-gated verbs would DEFER).

After the fixture's FIRST GREEN LIVE RUN (not merely on commit - see the DONE note's re-tier-held decision), re-tier its spec(s) from `pending-fixture` to `daily`; `L1-research-node-science`'s M-C1 readiness-widen dependency has landed. Then run each L1 script and confirm the `[Harness][...][SEED|CAPTURE|ORACLE|DIFF|VERIFY]` lines, the `kscaction ... applied=true manifestKind=<kind>` line, the matching `Game state: TechResearched|FacilityUpgraded|CrewHired|CrewRemoved` observer line (NOT a guard-patch line), and `ledgerOracle status=PASS`. Negative control for research: hand-edit the produced save's R&D science to an unspent value, re-run just the verifier, confirm `PARSEK-FAIL(ledger)` with `sciencePool` named. Do NOT add a capture pattern (the pattern rewrite is deferred).

## M-B2 - Ledger oracle: independent two-leg career-state cross-check [BUILT, branch `autotest-mb2-impl`]

Implements `docs/dev/design-autotest-ledger-oracle.md` (merged #1311). Leg B: the analyzer's `.analysis.json` gains an additive `careerSave` block (the independent `CareerSaveParser` output; AnalyzerVersion 2->3; `SaveDirectoryLoader` returns any Parsed snapshot with `Inv8Ledger` re-gated on `HasFunds` so INV8 verdicts are unchanged). Leg A: run.py captures a per-run action manifest (spec-declared author constants + stock-log-captured awards via EN-pinned `STOCK_AWARD_PATTERNS`) into `results/<runId>.manifest.json`, with a pre-launch analyzer pass over the staged template as the seed baseline. The oracle (`harness/lib/oracle.py`, pure) computes expected totals (author-constant REQUIRED for state-dependent science/reputation facets, captured amounts must be deltas, reputation Hermite curve ported double-precision from `ReputationModule.cs`) and diffs against the parsed block with the hard-facet vs report-only policy; hard drift -> `PARSEK-FAIL(ledger)` with the UT window named. Captured-vs-declared cross-check: an award the capture SAW but the spec never DECLARED reds (`unmatched_captured_awards`), so B10's expected stays == seed and a capture-enumeration gap cannot false-PASS. B10 carries the first real (empty) `[expectations.ledger]` block. Suites: xUnit 18174 + lib 258 + provision 149 + missions/lib 89.

**PENDING-OPERATOR:** (1) ~~REWRITE `STOCK_AWARD_PATTERNS` against real EN stock text~~ **DONE 2026-07-29** for REPUTATION, from the CL-1 flights' measured lines - no operator capture session was needed in the end, because a career flight had already produced them. The funds half of that rewrite was itself composed rather than measured and was retired the same day: KSP logs NO funds and NO science award line (entry 1b), so the capture is reputation-only and science is closed negatively rather than left unmeasured. The now-live cross-check is REPORT-ONLY behind `captureCrossCheck`; full write-up in the `harness-fail-open-gates` entry above. The original text, for the record: REWRITE `STOCK_AWARD_PATTERNS` against real EN stock text, not merely verify it. The committed patterns are DEAD against real stock logs: they key on `delta=` (science) / `funds=` / `reputation=` tokens that Assembly-CSharp never emits - real stock logs read `[Research & Development]: +N data on <subject>` (and the funds/rep award lines have their own, different shapes). So the leg-A capture matches ZERO lines today and the captured-vs-declared cross-check is a STRUCTURAL NO-OP - safe only under save-diff-primary (B10's empty-manifest zero-delta cross-check + the produced-save diff catch a drift regardless of capture). The operator must CAPTURE the real award lines from a live EN KSP.log FIRST, then the patterns (regex + named `amount`/`subject`/`guid` groups) are rewritten to match them before any non-empty (L1) manifest can trust the capture. (2) the live B10-with-oracle row per the design's PENDING-OPERATOR section.

**Deferred (design "Deferred Items"):** non-empty seam-declared manifests (first L1 scenarios); milestone/contract facet promotion beyond report-only; the rec3 route-residual carve-out activation. Review-panel additions: (a) a RUNTIME GUARD that refuses to trust the stock-log capture for a NON-EMPTY manifest until an operator has verified `STOCK_AWARD_PATTERNS` against a live EN KSP.log (today the conservative patterns are safe only because B10's empty-manifest zero-delta cross-check makes an incomplete/imperfect set harmless); (b) the ONE-AWARD-PER-LINE capture assumption (`parse_stock_award_lines` captures at most the first matching pattern per line), pending operator confirmation of the real stock award line shapes (a single line emitting multiple awards would need a multi-match pass); (c) the shared-curve residual blind spot on `apply_rep_curve` (a transcription fault shared with `ReputationModule.cs` produces expected==save==recalc and stays green) closes only with a check against known in-game rep transitions before any L1 rep script trusts a non-empty rep manifest.

## M-B1 - Mission library: kRPC/MechJeb flown scenarios [BUILT, branch `autotest-mb1-impl`]

Implements `docs/dev/design-autotest-mission-library.md` (merged #1310). All pure logic + shells + orchestrator wiring headless-tested: hlib autopilot spec validation / classify_mission_step / venv_admission, mlib phase machines + assertion evaluators (PEAK-apoapsis gate, not frames-in-window) + debounce + connect-retry + warp guard + post-connect-exception split + result round-trip, mission shells + fake-telemetry integration + bootstrap pure parts, run.py handoff smokes over the real loop with a fake mission subprocess. The COMMITTED specs `B1-pad-hop.toml` / `B2-lko-ascent.toml` are validated through the REAL path by a round-trip test (`test_hlib.CommittedSpecValidationTests`: parse each `scenarios/*.toml`, resolve mission schemas via `run.resolve_mission_schemas`, run `hlib.validate_spec`), so a committed-spec regression (e.g. a `steps` array mis-scoped under `[driver.missionParams]`) can never escape; both also `--dry-run` clean.

**PENDING-OPERATOR (live items, per the design's runbook section):**
1. Bootstrap the mission venv (`python harness/missions/bootstrap_venv.py`): confirm krpc==0.5.4 resolves via pip, the import + generated-code smoke passes, the resolved protobuf version freezes into the stamp, then promote the protobuf pin into requirements.txt.
2. Create the two fixture saves using KSP's OWN stock craft (no building): `fixtures/saves/b1-pad-craft` = VAB-load the stock "Jumping Flea" (mk1pod.v2 + chute + RT-5 Flea), launch to the pad, save-and-exit; `fixtures/saves/b2-lko-craft` = same with the stock "Kerbal X" (the canonical two-stage orbiter). Both ship in `Ships/VAB/` and the provisioned instance junctions/copies them, so they appear in the instance's VAB stock-craft list.
3. Live B1 pad-hop run, live B2 LKO-ascent run, and the deliberate flake/assert-fail captures proving INVALID(mission)/INVALID(autopilot-flake) never poison the PARSEK-FAIL bucket.

**Deferred (design "Deferred Items"):** the budget-arithmetic spec-validation cross-check; kOS secondary stack; VAB craft-file flow (v1 uses pre-placed fixture vessels).

## M-A3 - Autorun in-game test hooks + RecordingInvariants in-game category [LANDED, branch `autotest-hooks`]

- ~~Env-gated autorun hooks (`PARSEK_AUTORUN_TESTS` selector, `PARSEK_AUTORUN_EXIT` quit-after) so an external orchestrator can run in-game test batches unattended and read a grep-stable `BATCH_COMPLETE v1` line per batch.~~ DONE. Inert by default (both env vars unset = zero per-frame work, no behavior change, nothing written to any save). Design: `docs/dev/design-autotest-autorun-hooks.md`.
- ~~`RecordingInvariants` in-game test category (H5): walks the live `RecordingStore` and runs the shared analyzer invariants against it in a real KSP session.~~ DONE. The analyzer core now lives in `Parsek.dll` (`Parsek.Analyzer`) so the same rules run offline and in-game.

**Known limitation (NIT-4, accepted / deferred):** an exit-armed MULTI-category autorun run does not suppress the mid-run Space Center bounce if a token hits an NRE storm. The per-token batches are deliberately non-exit-armed (only the driver's final aggregate exit quits KSP), so a storming token still runs the normal Space Center bounce recovery before the run continues; the aggregate quit happens after all tokens. Campaign-safe (the disk save is already reverted by the token's teardown), so this is accepted for v1 rather than fixed. Single-category and `all` exit-armed runs are unaffected (H2 supersedes the bounce there).

## M-A findings-baseline - per-save known-findings baseline for the offline analyzer [LANDED, branch `autotest-baseline`]

- ~~A per-save baseline (`<save>/analysis/baseline.cfg`, ConfigNode) that accepts already-known findings so a gated run reds only on genuinely NEW findings, without ever hiding a finding from the report or applying to a fresh mission save.~~ DONE. Design: `docs/dev/design-autotest-findings-baseline.md`.

**Shape:** `BaselineMode {Ignore, Apply, Forbid}` (Forbid = baseline-presence-is-FAIL, the structural fresh-save guard). Pure `BaselineFilter.Apply` (in `Parsek.Analyzer`) marks `Finding.Baselined` on key `(RuleId, Target, SectionIndex, digit-masked MessageDigest)` matches, one-match-per-entry (surplus gates via MULTI-MATCH), refuses placeholder Targets and stamped fixtures, and recomputes the `Counts.FailNonBaselined` / `StaleNonBaselined` splits that drive `IsRed`. The `.analysis.txt` header carries a terminal `RED=<0|1>` token as the single gate source (scripts read it, never recompute from `FAIL=`/`STALE=`). Codec + `-WriteBaseline` builder (FAIL/WARN only, reason-preserving update, stale prune with `-KeepStaleBaselineEntries` opt-out) live in `Parsek.Tests`. Report schema `AnalyzerVersion` bumped `1` -> `2`. Plumbing: `PARSEK_ANALYZER_BASELINE_MODE` env; `analyze-recordings.ps1 -UseBaseline/-WriteBaseline`; `analyze-historical-saves.ps1` Apply-mode floor.

## M-A5 - Harness core: the unattended orchestrator [LANDED, branch `autotest-harness`]

- ~~The external Python orchestrator (`harness/run.py`) that ties M-A1/M-A2/M-A3/M-A6 into an unattended pipeline: select scenarios, admit the instance, stage the fixture, launch KSP with the scenario env, drive the seam under a wall-clock budget (timeout -> process-tree kill -> KILLED, never a hang), run the verifier chain, classify into the plan's verdict taxonomy, snapshot diagnostics on failure, and record a per-run result + coverage ledger.~~ DONE (v1, seam-driven only; autopilot flight is M-B1). Design: `docs/dev/design-autotest-harness-core.md`.

**Shape:** pure decision library `harness/lib/hlib.py` (spec validation, selection, response-stream eval, verdict classification/retry/expected-fail overlay, expectations, log-validate profile selection, budget arithmetic, admission reuse over `provlib`, coverage/flake, result serialization + schema gate; 126 pytest-free unit tests) + thin I/O shell `harness/run.py` (all OS I/O behind an injectable `Runtime` seam; every decision delegated to hlib). Verdict enum `{PASS, PARSEK-FAIL, INVALID, KILLED, EXPECTED-FAIL, XPASS}` (XPASS added for an expected-fail scenario that unexpectedly passes; FLAKE dropped in favor of a `flakedThenPassed` note on a PASS). Two additive dev-script seams: `analyze-recordings.ps1 -FreshSaveGate` (programmatic analyzer Forbid, mutually exclusive with `-UseBaseline`/`-WriteBaseline`) and `validate-ksp-log.ps1 -KilledRun`/`-NoRecordingRun` (set `PARSEK_LIVE_SUPPRESS_RULES` to the marker-pairing rule codes; the C# `ParsekLogContractChecker.ParseSuppressionList` rejects any request to suppress FMT-001/FMT-002/WRN-001 - the cannot-mask guarantee). Fake-KSP smoke test (`harness/lib/test_run_smoke.py` + `_fake_ksp.py`) drives a full PASS + KILLED + boot-crash run through the shell with no real game, plus a direct stage_fixture containment test (a runSaveName that escapes `saves/` aborts before any rmtree).

**Adaptations (v1):** (1) admission projects the on-disk provision manifest as the expected baseline and substitutes only the deployed `Parsek.dll` sha as the substantive drift check, because the provisioner's live manifest content-hash recipe (`phase_deploy`) is not yet implemented; this detects POST-PROVISION CLOBBER only (the deployed DLL was changed AFTER the manifest was stamped), NOT a stale deploy (Parsek rebuilt in source but never redeployed, so the manifest and the deployed DLL still agree on the old hash) - stale-deploy detection needs the provisioner live hashing path and is deferred; the remaining fields admit as-recorded. (2) expected-fail signature matching supports an optional `expectedFail.subkind` that narrows the demotion to one PARSEK-FAIL class; when `subkind` is empty the match falls back to bugId-only (any PARSEK-FAIL on the scenario matches, warned at demotion time). (3) coverage/flake are refreshed IN-RUN at the end of a `run.py` invocation (`refresh_coverage_and_flake`) rather than by a standalone `coverage.py` module. (4) a retryable INVALID re-runs the WHOLE attempt (fresh stage + boot); subprocess-scoped retry (re-run only the wedged verifier subprocess, not a fresh boot) is deferred to M-A5.1.

**M-A5.1 follow-ups (harness-core, branch `autotest-ma51-followups`):**
- ~~Subprocess-scoped tooling retry (adaptation 4).~~ DONE. A wedged verifier subprocess (analyzer / log-validate tooling fault, NEVER a Parsek verdict - analyzer RED=1 is a verdict, analyzer CRASH is tooling) is re-run ONCE over the same already-produced save/log before the whole-attempt retry burns a fresh ~10-min boot; pure retry-scope classifier `hlib.classify_retry_scope`, both attempts' outcomes logged so a retry never masks nondeterminism, whole-attempt policy + INVALID taxonomy unchanged for everything else. **SF1 (review follow-up):** a subprocess-RECOVERED flake now (a) records a self-contained `verifiers.subprocessRetry` detail entry (`{stage, retried, attempt1, attempt2, recovered}`) in the durable result JSON so the recovery is auditable, and (b) accrues toward that scenario's flake/quarantine numerator via `hlib.flake_attempt_entries` (a synthetic INVALID alongside the PASS, mirroring a whole-attempt flakedThenPassed) - previously a recovered run wrote a single PASS JSON, so a chronically-wedging pwsh tool never reached the 20% quarantine threshold. **NIT 3:** the triage-only analyzer run on a driver-INVALID save (non-verdict) no longer subprocess-retries a wedged analyzer (pure waste over an already-INVALID save).
- ~~Multi-category BATCH_COMPLETE aggregate (design note N3).~~ DONE. A multi-category RunTests (`all` / `A,B`) is now gated on the `category=multi:<count>` aggregate line (union `failed=0` means EVERY category passed, defended against a mis-summarized aggregate) via pure `hlib.resolve_batch_complete`; a missing aggregate with per-category lines present reds batch-incomplete instead of silently passing off one category's per-category line. **SF2 (review follow-up):** the aggregate's `multi:<count>` is now cross-checked against the per-category line count via STRICT EQUALITY (the count IS the number of categories the autorun ran, so exactly that many per-category lines must be present) - a mismatch either way (a cut-off category batch OR an unexpected extra batch) reds `category_count_mismatch` (same treatment as `aggregate_missing`: `present=False`), never a silent pass off a mis-counted aggregate; this also un-deadens the previously-parsed-but-unread regex count group (NIT 2). Tests: pure hlib cells (`RetryScopeClassifierTests`, `MultiCategoryBatchCompleteTests`, `SubprocessRecoveredFlakeAccrualTests`) + fake-runtime smoke cells (`SubprocessScopedRetrySmokeTests`, `MultiCategoryBatchSmokeTests`).

**Operator runbook (pending, PENDING-OPERATOR):** the LIVE end-to-end path needs a provisioned instance + a real KSP, which an agent cannot pilot. On a provisioned `stock-minimal` instance, run `python harness/run.py --tier daily` and confirm: the `[Harness]` admit/launch/verdict lines, a `BATCH_COMPLETE v1 ... failed=0` line, a `RED=0` analyzer header, the recording-rules log suppression on the no-recording B10 loop, a PASS `harness/results/<runId>.json`, and a coverage line; then force a KILLED (an over-budget scenario) and confirm the KILLED verdict + killed-run log mode + the collect-logs snapshot. This is the plan section 11 Phase 2 exit criterion.

## M-A6.1 - Automation-stack provisioner: live execution phases [LANDED, branch `autotest-provision-live`]

- ~~The heavy live provisioning phases of `harness/provision/provision.py` (CLONE / BUILD-TT / INSTALL / VERIFY, plus the SETTINGS/DEPLOY/MM-CACHE/MANIFEST writes and `--repair`), which previously aborted loudly at an `EC-LIVE` guard so a non-dry-run could not half-provision.~~ DONE. Design: `docs/dev/design-autotest-stack-setup.md` ("Deferred to live execution").

**Shape:** every live phase is thin I/O over a pure, unit-tested `provlib` decision (`harness/provision/test_provlib.py`, 96 tests). CLONE writes the `.provision-incomplete` marker FIRST, pre-checks free space (EC-6), copies the mutable surface via `clone_toplevel_disposition` through the `\\?\` extended-length path form (EC-7), junctions StreamingAssets + Squad + SquadExpansion (mklink /J, EC-8), and content-tree-hashes each dev-sourced mod (EC-3). BUILD-TT exports OrbitTools.cs + TestingTools.cs via `git show` at the pin (AutoLoadGame.cs / AutoSwitchVessel.cs dropped, GT-4), authors an SDK-style net472 shim csproj + AssemblyInfo, `dotnet build`s it, and asserts the AutoLoadGame type is ABSENT from the built assembly via a UTF-8 metadata reflection proxy (S-4). INSTALL extracts the kRPC subtree, drops the built TestingTools.dll, extracts the prebuilt KRPC.MechJeb.dll, and hashes every installed DLL into the manifest with the exact field names (`dllSha256`, `installedDlls`) `hlib.admit_instance` diffs. VERIFY re-hashes every component DLL + the Parsek UTF-16 grep + settingsFinalSha256 (N-4) + buildID64.txt (N-5) + junction realpaths, collecting structured drift; `--repair` converges ONLY the drifted components via the pure `plan_repair`, then re-VERIFYs; the marker is cleared LAST on success. The `EC-LIVE` guard is removed.

**Pins resolved (EC-13 fill-and-commit = the v1 resolution of the OPEN items):** all three release pins are now filled and committed. `pins.toml` `krpc.releaseZipSha256 = b09dddf5...` (official kRPC v0.5.4 GitHub release; GT-5/O-2 also confirmed: three compile DLLs present, no TestingTools.dll); `krpc_mechjeb` sources the PREBUILT KRPC.MechJeb.dll from the genhis v0.7.1 GitHub release (`downloadUrl` + `releaseZipSha256 = 5922a6d7...`) instead of a second from-source build (smallest adaptation of the design's PAIR-builds-from-source; the release is the reproducible ABI-matched artifact, the git tag/commit stay the pin identity); and (O-3 RESOLVED 2026-07-12) `mechjeb2 = 2.15.1.0` pinned via the CKAN-meta record (`MechJeb2-2.15.1.0.ckan`, ksp_version_max 1.12) to the persistent `ksp.sarbian.com` MechJeb2-Release Jenkins job artifact (#45, distinct from the GC'd rolling dev job), content-pinned by `sha256 = 3bd39e02...` independently computed and matched against CKAN-meta's `download_hash.sha256`; if the URL rots, re-fetch from the archive.org CKAN mirror. A full live `run()` is therefore no longer blocked at DOWNLOAD's EC-13.

**Fix round (branch `autotest-provision-live`, reviewer findings):** BLOCKER 1 - dev-sourced mods are now verified INSTANCE-side (CLONE hashes the instance copy and aborts EC-3 on a partial copy; VERIFY re-hashes each instance `GameData/<mod>` against the manifest and emits `devSourcedMods.<name>` drift; `--repair` scoped-deletes the instance mod dir behind the EC-16 fence before re-copying so an injected extra file converges). SF2 buffers live log lines until the EC-16 gate passes (no write at a mis-aliased target). SF3 wraps the live phase sequence in try/except -> clean EC abort + `_finish(2)` + lock release, marker left in place. SF4 `_assert_krpc_zip_layout` failure ABORTS EC-3. SF5 zip-slip guard (`gamedata_dest_escapes`) rejects any extraction dest that escapes instance GameData. SF6 junction-aware hardening (realpath EC-16 re-check before the marker write; reparse-point subdirs skipped in copy/hash walks). SF8 DEPLOY aborts EC-9 demanding `--parsek-dll` when this worktree has no `bin/Debug` build (no hardcoded sibling fallback). Nits: N11 real GameData folder labels in the plan, N12 BUILD-TT git-shows at the peeled commit, N17 asserts the six capability RPCs present, N18/N20 comment + skip-list fixes.

**Validated:** unit tests (provision 123 / lib 131, green) + `--dry-run` for both profiles exit 0; and directly against the dev install for the earlier round - BUILD-TT compiled TestingTools.dll from `mods/krpc@v0.5.4` (dotnet 6.0.428, S-4 passed, all six capability RPCs present), INSTALL produced the correct GameData/kRPC layout with real per-DLL sha256s, and VERIFY/`--repair` detected a clobbered KRPC.dll and converged it.

**Operator runbook (pending, PENDING-OPERATOR):** the REAL provisioning smoke (create the actual `automation/stock-minimal` instance) is the coordinator's next step and is no longer gated on filling a pin. Build this worktree's `Source/Parsek/bin/Debug/Parsek.dll` (or pass `--parsek-dll`), then run `python harness/provision/provision.py --profile stock-minimal` and confirm: the CLONE junctions resolve, BUILD-TT's `AutoLoadGame-absent OK` + `capability RPCs present=6/6`, every INSTALL DLL hash, `Verify ... result=OK`, the cleared `.provision-incomplete` marker, and a written `provision-manifest.json`; then clobber a component DLL (and separately inject an extra file into a dev-sourced mod dir) and re-run with `--repair` to confirm the targeted re-install + scoped-delete re-copy + re-VERIFY.

**Modularity follow-up (branch `autotest-provision-live`, self-contained kRPC source + boundary doc):** BUILD-TT and PIN no longer read the umbrella `mods/krpc` / `mods/KRPC.MechJeb` clones (a research-era convenience outside the module). Both now read a module-owned blobless git clone under `harness/provision/.cache/<comp>-src` (`provlib.resolve_git_source` decides reuse/refetch/clone/override; `provision._ensure_git_source` materializes it; `pins.toml` gains the `sourceRepo` URL per git-pinned component). `--krpc-src <path>` optionally overrides the kRPC source with an existing clone. This closes the last umbrella-`mods/` dependency so `harness/` is fully self-contained / submodule-ready. Added `harness/README.md` (ownership boundary) and a "Module boundary and submodule readiness" section to `docs/dev/design-autotest-stack-setup.md` (cross-referenced from `design-autotest-harness-core.md`) enumerating what `harness/` owns, what lives at the umbrella root outside git, the Parsek-repo contract surface the harness consumes, and the split recipe. Tests: `resolve_git_source` pure decision (`GitSourceResolutionTests`) + shell wiring (`EnsureGitSourceShellTests`).

**First live end-to-end run fixes (branch `autotest-provision-live`, PR #1308):**

- ~~**F3 - provisioner stamps kRPC settings for unattended operation.**~~ DONE. kRPC ships `GameData/kRPC/PluginData/settings.cfg` with `autoStartServers=False` / `autoAcceptConnections=False` / `confirmRemoveClient=True`, which force a manual in-game click every launch and defeat unattended runs. INSTALL now stamps those three flat `KRPCConfiguration` keys to `True` / `True` / `False` (pure `provlib.stamp_krpc_settings`: edit-in-place when the shipped file is present, synth a minimal node when absent, idempotent on re-stamp, no other key hardcoded), records `krpcSettingsSha256` over the LF-written bytes, and VERIFY re-hashes it (drift on a later manual kRPC settings edit) mirroring `settingsFinalSha256`. The settings.cfg is not a DLL, so it stays outside the `installedDlls` hash set. Tests: `KrpcSettingsStampTests` (pure) + `KrpcSettingsStampShellTests` (shell/VERIFY) + the `--dry-run` plan WRITE line.
- ~~**F2 - LoadGame two-phase completion detects a failed load instead of hanging PENDING.**~~ DONE. On the first live run KSP's `FlightDriver.Start()` threw an NRE loading an incompatible save (a mod-part active vessel absent from the instance) and the seam's two-phase LoadGame completion stayed PENDING (would ride to the harness run budget -> wrong verdict). The pure `TestCommandLoadGame.DecideLoadCompletion(elapsed, currentScene, currentGameNonNull, budget)` now returns `{StillWaiting, CompleteOk, LoadTimeout, LoadFailedMenu}`: a settled FLIGHT scene with a loaded game -> OK; a settle-back to MAINMENU -> terminal `ERROR msg=load-failed-returned-to-menu` (fast, since completion polling only runs at settled scenes and the transition flag is raised synchronously at initiation, a MAINMENU observation reliably means the load bounced); the LoadGame budget (300 s) -> terminal `ERROR msg=load-timeout`. Either terminal ERROR lets the harness classify a driver-INVALID (fixture) instead of hanging. Design R1 resolved. Tests: `TestCommandLoadGameTests.DecideLoadCompletion_*`.

**First live automation-instance smoke fixes (branch `autotest-instance-fixes`):**

- ~~**LG1 - seam LoadGame NRE'd stock `Game.Load()` on every cold-MAINMENU boot.**~~ DONE. On the `stock-minimal` automation instance the seam's `LoadGame` verb always failed: KSP booted to MAINMENU fine, initiated the flight load, then `Game.Load()` threw a single `NullReferenceException` (`Game.Load () / FlightDriver.Start ()`, no inner frames), the active vessel never materialized, and the scene wedged in per-frame `FlightAutoSave.Start` / `FlightGlobals.GetFoR` / `VesselAutopilotUI` / `ToolbarControl.OnGUI` NREs until the 300 s load-timeout fired ERROR. Root cause (decompiled `Game.Load`, KSP 1.12.5): `Game.Load()` dereferences `HighLogic.CurrentGame.Mode` on its FIRST statement but only assigns `HighLogic.CurrentGame = this` LATER; `FlightDriver.StartAndFocusVessel(Game, idx)` does NOT set `HighLogic.CurrentGame`. `ParsekTestCommandAddon.LoadGameImpl` called `StartAndFocusVessel` straight from MAINMENU (mirroring the in-flight quickload path, which only works because a running game keeps `CurrentGame` non-null), so cold from the menu the first deref NRE'd. Fix: adopt the loaded game as `HighLogic.CurrentGame` before the flight scene change (one line, in the already-100%-broken cold-boot verb; the in-flight quickload path is untouched). This was NOT an instance-composition fault - an automated seam-driven composition bisect (base+Parsek pure-stock, and again with the injected Parsek save data removed) both still crashed, and the instance-core config (PartDatabase / Physics / settings deltas / junctions / buildID) is all clean or benign; the dev install "loads fine" because its Parsek.dll has no seam, so those loads were manual, not the cold-MAINMENU seam path. Proven: a final full-composition seam boot returned `LoadGame verdict=OK ... scene=FLIGHT` with zero Game.Load/FlightAutoSave/ToolbarControl NREs. Runtime-only (Unity `HighLogic.CurrentGame` adoption); covered by the in-game proving boot, not a pure unit test (the `TestCommandLoadGame` focusability/completion helpers are unchanged and already tested).
- ~~**LG2 - provisioner DEPLOY installed only the plugin, flooding ToolbarControl.OnGUI with a missing-icon NRE.**~~ DONE. `provision.py` DEPLOY installed only `Plugins/Parsek.dll`, so the deployed `GameData/Parsek` lacked `Parsek.version` and `Textures/parsek_{24,32,38,64}.png`; the missing `parsek_32`/`parsek_64` toolbar icons made `ToolbarControl.AddToAllToolbars` register a button whose texture cannot be found, NRE'ing every OnGUI frame (a SEPARATE cause from LG1). DEPLOY now resolves + installs the version file + all four textures via the pure `provlib.resolve_parsek_aux_payload` (worktree `GameData/Parsek` -> repo `img/` -> dev-install `GameData/Parsek`, per dest; required 32/64 must resolve or EC-9), hashes each into `components.parsek.auxFiles`, VERIFY re-hashes them, and `--repair` routes aux drift to a parsek re-deploy. Tests: `ParsekAuxPayloadTests`. Applied to the live instance; the successful proving boot showed zero pre-flight ToolbarControl NREs.

- ~~**LG3 - in-game test-runner restore wait could never complete on a zero-stage vessel.**~~ DONE. `InGameTestRunner.IsStageManagerReadyForActivateNextStage` required `StageCount > 0`, which a staging-free vessel (the single-pod smoke fixture) never reaches, so every batch-baseline restore hit `WaitForStockStageManagerReady timed out after 10s` and logged a `[Parsek][ERROR]` that the harness expectations' forbidden-pattern correctly flagged. The predicate now takes `vesselExpectsStages` (new `ActiveVesselExpectsStages()` scans `part.hasStagingIcon && part.stagingOn`, strict-true fallback on null vessel/parts; NOT bare `stagingOn`, which is constructor-true on every part per the decompiled `Part..ctor` and made the first fix attempt a no-op) and accepts `StageCount == 0` when no part contributes a stage icon; the strict wait is unchanged for staged vessels. Tests: `InGameTestRunnerTests.StageManagerReady_ZeroStageVessel_*`.
- ~~**LG4 - EC-3 dev-mod drift contract broke on runtime PluginData writes.**~~ DONE. One instance game launch wrote 129 `KSPCommunityFixes/PluginData/TextureCache/*` files; the CLONE-path re-copy merged over them and aborted EC-3 (post-copy hash != source), and the hash covering runtime writes would have re-drifted per launch regardless. Tree-hash now prunes `PluginData` (pure `provlib.is_runtime_writable_dir`); CLONE pre-clears a pre-existing mod DIRECTORY via the scoped delete (single-file mods are exactly replaced). See the EC-3 section of `design-autotest-stack-setup.md`.

## M-A6.2 - Automation-stack provisioner: idempotency + installed-file inventory [BUILT, branch `autotest-ma62-followups`]

- ~~**(SF9) idempotency.** Re-running `provision.py` against an already-provisioned, non-drifted instance redid every copy/build/extract.~~ DONE. Each heavy phase now hash-short-circuits when a prior COMPLETE provision (manifest present AND no `.provision-incomplete` marker) left the thing that WOULD be installed already on disk. Pure per-component decision `provlib.decide_idempotent_skip` (compare two hashes under the `prior_complete` gate; every other case re-installs); the caller picks each hash's meaning - the manifest-recorded pinned hash for the fixed-by-pin release components (kRPC/MechJeb2 zips, with a `_install_pin_stable` pin-identity gate so a moved release pin re-extracts, never skips on a stale on-disk-equals-old-manifest match), and the fresh SOURCE hash for the components whose input changes between runs (the rebuilt Parsek.dll, the freshly built TestingTools.dll) so a changed source still re-installs. **Every skip gate is a FRESH-SOURCE / fresh-instance-integrity check, never a proxy** - on any skip the manifest carries the prior-recorded hashes forward, so VERIFY (re-hash instance vs that same manifest) structurally cannot catch a false skip; the skip decision is the only defense, so each gate re-derives current truth from the actual input. Wired into CLONE (mutable-surface copy + junction (re)creation - skipped only when buildID64 matches, junctions resolve, AND the instance `KSP_x64_Data/Managed` file-count+bytes stat `mutableSurfaceManagedStat` still matches, so a partially-deleted / swapped-DLL instance that buildID64 alone misses re-copies, `provlib.mutable_surface_stat_matches`; residual risk: a same-count same-size in-place DLL edit is not caught, per-file size+mtime digest deferred; per-dev-mod copy - skipped only when the FRESH dev-SOURCE tree-hash == recorded == instance so a dev-side mod update propagates, not the old instance-vs-manifest proxy), BUILD-TT (the dotnet build - skipped only when the kRPC source commit, the kRPC release-zip pin `_install_pin_stable("krpc")`, AND the dev KSP buildID64 are ALL stable, since the shim HintPaths into both the release binaries and the dev Managed reference DLLs), DEPLOY (Parsek.dll stage+install + each aux), and INSTALL (each stack component; the KRPC.MechJeb pin gate now compares tag AND commit). VERIFY still runs FULLY (it is the proof); SF9 only elides redundant writes. Every skip is logged (batch-summary convention).
- ~~**(SF10) per-component installed-file inventory.** VERIFY could not detect a file ADDED inside a stack component's own folder (`GameData/kRPC` beside the hashed DLLs); only dev-sourced mod trees got whole-tree content-hash coverage. The LG4 PluginData hash-prune was a second blind spot (content injected under a dev-sourced mod's PluginData was invisible to EC-3, bounded because KSP's assembly loader + GameDatabase skip PluginData).~~ DONE. The manifest gains a top-level `componentInventories` (relpath + sha256 per file each stack component wrote), deliberately OUTSIDE the admission projection (`ADMISSION_KEYS`) so the M-A5 harness admit path is unchanged; VERIFY groups inventories by shared install folder, re-scans each, and diffs via `provlib.diff_inventory` (missing/changed authored files drift; an unrecorded on-disk file is an ADDED drift). PluginData paths are recorded for visibility but tolerated on all axes (runtime-writable). `--repair` converges by scoped-deleting the drifted folder behind the EC-16 fence (removing an injected file, which a merge-copy could not) then re-installing every folder-sibling. Backward-tolerant: an old manifest with no inventories verifies as before, logging an amber "inventory absent - re-provision to arm".
- ~~The CLONE-path pre-clear abort branch (`_copy_and_verify_dev_mod` stale-instance-dir cannot-clear -> EC-3) and the VERIFY auxFiles re-hash loop were exercised only by the live run.~~ DONE - direct shell tests added (`ClonePreClearAbortShellTests`, `VerifyAuxFilesRehashShellTests`).

**Tests:** pure cells for the skip classifier + `installed_map_digest` (`IdempotentSkipTests`), the inventory diff + PluginData tolerance + folder grouping + old-manifest tolerance (`InventoryDiffTests`), the two gap shell tests, SF10 VERIFY wiring (`Sf10InventoryVerifyShellTests`: added/missing/PluginData-tolerated/old-manifest-amber), SF9 INSTALL skip wiring (`Sf9InstallSkipShellTests`: hash+pin match skips, moved pin re-extracts), and an end-to-end idempotent-second-run fixture (`IdempotentSecondRunE2ETests`: provision twice into a temp instance with fakes, assert every heavy phase short-circuits on run 2 and VERIFY stays clean, plus the `--repair` injected-file convergence).

**Two-reviewer fix round (same branch).** Every skip gate re-audited as a fresh-source check (reviewer A's framing: on a skip the manifest carries prior hashes forward, so VERIFY cannot catch a false skip). S1: BUILD-TT skip now also gates on `_install_pin_stable("krpc")` (moved `releaseZipSha256`) + dev buildID64 stability, so a moved release pin or a KSP version bump rebuilds the shim instead of reusing a stale-linked TestingTools.dll (`Sf9BuildTtSkipShellTests`). S2: the CLONE mutable-surface skip adds the `mutableSurfaceManagedStat` fresh re-scan (`Sf9MutableSurfaceStatTests`, incl. the required delete-a-Managed-DLL-must-not-skip case) since buildID64 is a single-file proxy and VERIFY never re-hashes the stock Managed tree. S3: the dev-mod skip gates on dev-source == recorded == instance so a dev-side update propagates (`Sf9DevModSourceGateTests`). N1: `_install_pin_stable` krpc_mechjeb gates on tag AND commit (`PinStableKrpcMechjebCommitTests`). N2: the abort-then-rerun fence (`Sf9PriorProvisionFenceTests`: marker-present + unreadable-manifest -> `prior_complete` False). B1: `_repair_stack_folders` writes the `.provision-incomplete` marker before the scoped delete. B2: the DEPLOY aux copy uses the long-path-safe `_copy_one`. B3: `_verify_inventories` caps per-file drift Error lines (first 10 + a suppressed-count line; every diff still lands in the drift list). Provision suite 149 -> 177 -> 194, `--dry-run` still exits 0.

**Remaining (from the LG-round review, not blocking):** pre-branch provisioned instances self-inconsistently verify (manifest lacks `components.parsek.auxFiles`, still lists the removed StreamingAssets junction the corrected dangling probe flags forever) - they converge only on a full re-provision, fine for the single live instance. Cleanup: a leftover `automation/stock-minimal/GameData/Parsek/provision-log.txt` from an earlier validation run sits at the umbrella root (outside the repo, gitignored); remove it when convenient (a subagent cannot delete it under the file-deletion policy).

## M-A5/M-A6/M-B2 integration fixes [BUILT, branch `autotest-integration-fixes`]

Two Fable audits (integration + deep PR review) of merged main. Python-side findings only (the C# / M-C1 side is a sibling's). Suites: lib 331 + provision 197 + missions/lib 89, `--dry-run`s exit 0, `CommittedSpecValidationTests` green.

- ~~**Item 1 - ledger + rewind/merge-dialog spec must hard-error.**~~ DONE. `validate_spec` now rejects an `[expectations.ledger]` block paired with an `InvokeRewind` or `AnswerMergeDialog` step: a rewind/merge rewrites the career pools the seed+manifest contract cannot model, and the rewound-career oracle is DEFERRED to L4 (the error names the deferral). `TimeJump` + ledger stays allowed (design-blessed forward jump).
- ~~**Item 2 - provisioner inventory carry-forward armed from disk.**~~ DONE. The krpc / krpc_mechjeb / mechjeb2 SF9 skip paths carried `_prior_inventory()` with no fallback, so a pre-inventory (pre-M-A6.2) manifest stamped `componentInventories` present-but-EMPTY, the amber path (keyed on the whole map being None) never fired, and the next real VERIFY red every on-disk file as ADDED drift. All three now use the testingtools-style `... or _inventory_of_folder(ctx, comp)` fallback (scans the install folder when the prior inventory is absent). Shared-folder scans record a superset, harmless under the folder-UNION VERIFY.
- ~~**Item 3 - dispatch-deferral margin for non-two-phase verbs.**~~ DONE. `AnswerMergeDialog` (120s dialog) / `KscAction` (60s career) are deliberately NOT in `DEFERRED_SEAM_VERBS` (they complete quickly), but their nonzero seam-side deferral budget meant `drive_seam`'s bare per-step wait could KILL a genuinely-deferring verb before the seam self-emitted its TIMEOUT (retryable driver-INVALID). New `hlib.dispatch_deferral_budget` / `required_dispatch_step_wait` (mirroring the C# `DeferralBudget.BudgetSeconds` table) out-wait each non-deferred verb's budget + the 60s margin.
- ~~**Item 4 - fixture-less daily specs self-quarantine.**~~ DONE. B1/B2/B10 were `tier=daily` but reference uncommitted fixture saves, so `--cadence daily` INVALID(staging)'d them terminally and `compute_flake`'s sticky quarantine benched scenarios that never ran. Re-tiered to a new `pending-fixture` tier (added to `TIERS`, mapped to NO cadence) so no `--cadence` run selects them until an operator commits the fixture and re-tiers to `daily`; `--tier pending-fixture` still selects them for a smoke run.
- ~~**Item 5 - stale `mb2-not-landed` skip reason.**~~ DONE. Renamed to `no-ledger-block-declared` (M-B2 has landed) at both ledgerOracle SKIP sites.
- ~~**Item 6 - the five driver-* subkinds were dead vocabulary.**~~ DONE. The M-C1 verb-refusal `msg=` reason is now threaded onto `StepOutcome.msg` and mapped by the pure `hlib.classify_seam_refusal_subkind` to the finer subkind (refly-gate->driver-gate, unknown-rp/slot->driver-arg, no-live-dialog/choice-unavailable->driver-dialog, career-not-ready+career-state declines->driver-career, backward/refused-jump->driver-rewind), falling back to driver-verdict-mismatch on an unrecognized reason.
- ~~**Item 7 - `STOCK_AWARD_PATTERNS` dead against real stock logs.**~~ TODO-DOC ONLY (see the M-B2 PENDING-OPERATOR above). The patterns key on `delta=`/`funds=`/`reputation=` tokens Assembly-CSharp never emits (real stock logs read `[Research & Development]: +N data on ...`), so the capture cross-check is a structural no-op - safe under save-diff-primary. Corrected the PENDING-OPERATOR to a REWRITE (operator captures the real lines first), not a mere verification. No code change. **The rewrite itself landed 2026-07-29** (branch `harness-fail-open-gates`) from lines the CL-1 flights had already measured; the guess in this item's own parenthetical (`[Research & Development]: +N data on ...`) was ALSO never measured and is not what the patterns were built from - see the entry above for the four literal lines that were.
- ~~**Item 8 - oracle seq_key int/float type confusion.**~~ DONE. A null-UT entry's ordinal seq (int 3) compared EQUAL to a captured award's UT 3.0 (3 == 3.0, hash-equal) in the fill + unmatched matchers. Both `seq_key` properties (oracle.ManifestEntry, hlib.CapturedAward) + the fill local now return type-tagged `("ut", <float>)` / `("ord", <int>)`, so ord-3 never matches ut-3.0.
- ~~**Item 9 - `apply_rep_curve` unbounded loop on a huge rep constant.**~~ DONE. The curve splits `abs(nominal)` into integer steps, so a huge finite rep author constant (1e12) spins ~1e12 in-process iterations (a hang). `parse_manifest_entries` now rejects `|reputation| > 10000` at parse (rep range is +-1000) with a precise reason.
- ~~**Item 10 - NIT cluster.**~~ DONE. `parse_seed_baseline` now RAISES on a hasX=true facet with a non-numeric/missing/non-finite value (routed to INVALID(tooling), never a silent facet-absent degrade); a produced-save `careerSave {parsed:false}` on an active ledger scenario classifies tooling INVALID (parse condition), not PARSEK-FAIL missing-facet; `unmatched_captured_awards` compares against ALL of an entry's subject_ids (fail-closed), not just `[0]`; `_build_and_write_manifest` docstring tuple arity corrected to the 3-tuple; `flake_attempt_entries` emits the synthetic INVALID for ANY non-numerator verdict carrying a recovered retry (a wedging analyzer on a FAILING scenario still accrues), not PASS alone; `resolve_batch_complete` reds two aggregate lines as a defined `duplicate_aggregate` fault instead of silent first-wins; the CLONE mutable-surface skip gate now also consults the DEV install's current buildID64 (the fresh-source rule, mirror of the BUILD-TT gate) so a dev-side KSP version bump re-copies (exe/top-level residual gap noted in `design-autotest-stack-setup.md`); `diff_inventory` compares paths case-insensitively (mirror of the PluginData `.lower()` handling) so a case-only folder difference on Windows is not a false missing+added.

## ~~FIXED~~ - Supply-route inventory delivery only targeted the FIRST inventory module on the destination (2026-07-11, branch `fix-delivery-multimodule`)

**Bug:** `LiveDeliveryCapacityProbe.ProbeLoadedFirstEmpty` / `ProbeUnloadedFirstEmpty` stopped at the first `ModuleInventoryPart` and reported "no slot" when that one module was full, and `LiveDeliveryWriters.WriteInventoryLoaded` / `WriteInventoryUnloaded` wrote into the first inventory module they found. A destination whose first cargo container is full but whose later containers have free slots reported inventory items as undelivered. `consumedSlots` was a bare slot-index HashSet, only correct under the single-module assumption. Pickup and origin-debit (`LiveInventoryPickupWriter`) already scanned ALL inventory modules; this was a delivery-side-only gap.

**Fix:** widened the probe/planner/writer slot contract from a bare `int` to the module-qualified `InventorySlotAddress` struct (part index, module index, slot index; deterministic order = vessel part order, then module order within the part, then ascending slot index). The probe now walks ALL inventory modules on the captured loaded/unloaded branch, `consumedSlots` keys by address, and the writers resolve and store into exactly the (part, module, slot) the planner assigned, on the same captured `isLoaded` branch (defensive range/type checks Warn and report a failed store on mid-tick divergence). The unloaded slot-count fallback of 9 (unpersisted `InventorySlots`) is preserved per module. Plans are built fresh per delivery and never persisted (verified: RouteCodec / ledger rows carry no assigned slots), so no schema change.

**Tests:** planner multi-module address propagation (`RouteDeliveryPlannerTests`), address value semantics + pure unloaded scan/store helpers + the "Inventory store" log contract (`LiveDeliveryMultiModuleTests`), and the in-game `Delivery_MultiModule_FirstContainerFullSecondReceives` (loaded branch: fills the first container live, asserts the probe hands out the second container's slot and the writer stores there; the unloaded branch's logic is the headless-pinned ConfigNode path).

**Operator runbook (pending):** run the in-game `Delivery_MultiModule_FirstContainerFullSecondReceives` in a disposable FLIGHT session on a vessel with two cargo containers (Run All + Isolated or the row play button). Additionally, an UNLOADED-destination delivery onto a small container (e.g. a 3-slot SEQ) would exercise the prefab slot-count fallback (`ResolveUnloadedSlotCountFallback`), which needs `PartLoader` and has no headless test.

**Review follow-ups (same PR):** (1) unloaded phantom-slot fix - `InventorySlots` is a non-persistent KSPField so proto modules never carry it; the old hardcoded 9-slot assumption handed out phantom slot indices on smaller containers (stock 3-slot SEQ), which the writer persisted as UI-inaccessible stores, and the multi-module walk was widening that exposure; the probe now resolves the real count from the part PREFAB's module (`ResolveUnloadedSlotCountFallback`), falling back to 9 only when the prefab is unresolvable. (2) A present-but-unparseable `InventorySlots` value no longer zeroes the count via `int.TryParse`'s out-param (module falsely full). (3) `default(InventorySlotAddress)` is now invalid instead of reading as the valid (0,0,0) root-slot address. (4) The probe caches exhaustion - once a full walk returns None, later manifest items short-circuit instead of re-scanning the vessel per item. Known accepted lenience (pre-existing, unchanged): a STOREDPART with a missing/unparseable `slotIndex` is not counted occupied (corrupted-save-only double-book risk), and the unloaded branch matches modules by exact `moduleName == "ModuleInventoryPart"` while the loaded branch matches subclasses via `is` (same asymmetry as `LiveInventoryPickupWriter`).

**Merge reconciliation with `inventory-delivery-parity` (PR #1294, landed on main first):** the two changes are orthogonal (this one = WHERE items go across containers; that one = HOW MANY units / WHETHER they fit by volume/mass) and were merged into one unified contract: `InventoryDeliveryLine` carries both a module-qualified `AssignedSlot` and a `Units` count; the planner splits by stack capacity, admits by volume/mass, then assigns a per-stack (part, module, slot) address across all containers; the writers store `Units` into the exact assigned address. The parity branch's volume/mass budget originally read the FIRST inventory module only; because this branch made slot assignment multi-module, the budget was widened to SUM `packedVolumeLimit` / `massLimit` and occupancy across ALL inventory modules (any unlimited module makes that axis unlimited). Residual bounded imprecision (documented in `TryReadContainerBudget`): the budget is vessel-granularity while stock enforces per-container, so a specific container could individually over/under-fill; only the vessel TOTAL is bounded (stock automated delivery bypasses per-slot volume enforcement anyway). Per-container budgets coordinated with slot placement are a deferred refinement.

---

## ~~FIXED~~ - Supply routes silently lost cargo when the destination was full (found in the 2026-07-11 logistics inventory review; branch `logistics-destfull-gate`)

**Was:** `LiveRouteRuntimeEnvironment.DestinationHasCapacity` was the v0 always-true stub, so a cycle always dispatched, the origin was physically debited (or KSC funds charged) for the FULL manifest, and the apply-time partial fill dropped whatever did not fit - the remainder vanished (the transport is a ghost; nothing comes back) with only a "delivered-partial" status reason. The design doc's "implemented v0 gate" note even claimed a zero-capacity destination still blocked the cycle, which was never true.

**Fix (all-or-nothing, per the 2026-07-11 maintainer decision):** the gate is un-stubbed via the pure `RouteDestinationCapacityCheck`: every stop's full delivery manifest (resources AND stored-part inventory slots) must fit its resolved destination - evaluated with the SAME planner+probe the delivery applier uses, so the gate cannot drift from the write - or the route holds `DestinationFull` naming the first item that does not fit (`stored-part:<partName>` for inventory slots). Unresolvable stop vessels fail OPEN (the endpoint gate owns them). The apply-time clamp stays as the backstop for capacity that shrinks mid-cycle; such a partial now records `Route.LastPartialDeliverySummary`/`UT`/`CycleId` (sparse in the codec; same-cycle partial windows APPEND into one report and only a LATER cycle's full delivery clears it, so a multi-stop cycle's later full window cannot erase an earlier window's loss) and the Logistics detail panel shows "Last delivery was partial: <actual/requested per short item>". Same-destination stops share one probe across the gate walk so their COMBINED manifest is checked (review fix - a fresh probe per stop let two windows to one station each claim the full tank).

**Also in the branch (hold-reason legibility):** inventory shortfall holds name the PART instead of the identity hash (`inventory:<partName>`, origin + pickup-source gates; 64-hex tails from pre-legibility persisted holds keep the generic text), and a NEW near-miss token `inventory-state:<partName>` fires when the origin physically holds the part but its state (charge/fuel/contents) differs from the recorded cargo (`LiveInventoryPickupWriter.CountStoredByPartName`, classification only - admission stays hash-exact).

**In-game verification pending (operator):** a route to a full destination must show "Held: no room for X" / "Held: no slot for 'part'" and not debit the origin; filling the destination mid-transit is not scriptable headless.

## ~~FIXED~~ - Supply-route inventory delivery loaded-vs-unloaded parity gaps: stacked quantity lost on the loaded path, no volume/mass admission on the unloaded path, non-stackable over-compression (branch `inventory-delivery-parity`)

Three parity gaps in the inventory delivery writer bundle (`Logistics/LiveDeliveryWriters.cs` + `LiveDeliveryCapacityProbe.cs` + `RouteDeliveryPlanner.cs`), verified against decompiled stock (KSP 1.12.5):

- **Gap A (stacked quantity lost, loaded path):** the route inventory manifest compresses identical stored parts per identity hash and carries the delivered count in the STOREDPART wrapper's `quantity`; `WriteInventoryLoaded` rebuilt a `ProtoPartSnapshot` from the inner PART node only and called stock `StoreCargoPartAtSlot` once, which unconditionally stores `quantity = 1`. A Quantity=10 item delivered 10 units to an UNLOADED destination but 1 unit to a LOADED one. **Fix:** after the store, the writer raises the slot's stack via stock `UpdateStackAmountAtSlot` (which clamps to the StoredPart's `stackCapacity`, sourced from the snapshot's `moduleCargoStackableQuantity` that stock `ProtoPartSnapshot.Save` persists) and reads the slot back for the unit-accurate actual. The store's success is also verified by slot read-back because stock `StoreCargoPartAtSlot(ProtoPartSnapshot, int)` returns true even when a null `partInfo` makes it store nothing.
- **Gap B (no volume/mass admission, unloaded path):** decompilation showed stock's packed-volume/mass enforcement lives in the storage UI (`HasCapacity` / `PartDroppedOnInventory`), NOT in `StoreCargoPartAtSlot`, so BOTH automated branches bypassed it. **Fix:** explicit admission at probe time (`LiveDeliveryCapacityProbe.ProbeInventoryUnitsThatFit` + `ConsumeInventoryCapacity`, pure core `ComputeUnitsThatFit`), mirroring stock's accounting: prefab `ModuleCargoPart.packedVolume * units` and `(prefab.mass + prefab.GetResourceMass()) * units` against the container prefab's `packedVolumeLimit` / `massLimit` (read from the container part's PREFAB on BOTH branches so admission is branch-symmetric; limit <= 0 = unlimited; `packedVolume < 0` = not storable; an unresolvable ITEM or CONTAINER prefab fails closed). An exact fit is protected from float flooring by a 1e-9 unit epsilon (60L/0.6L must admit 100, not 99). The budget read and per-part footprints are memoized per planning pass. The planner marks non-fitting units as skipped (`AssignedSlot = -1`, `Units` = unplaced count, surfaced as `inventoryUnitsSkipped` in the delivery summary log), so the writers never receive them - probe/writer symmetry preserved on both branches.
- **Gap C (non-stackable over-compression):** the planner assigned ONE slot per manifest item regardless of Quantity, so 10 non-stackable units recorded across 10 slots persisted as an invalid `quantity=10` in a single slot on the unloaded path. **Fix:** the planner splits each item into ceil(Quantity / stackCapacity) slot-sized stacks (`InventoryDeliveryLine.Units`), resolving stackCapacity from the STOREDPART wrapper's `stackCapacity`, then the inner PART's `moduleCargoStackableQuantity`; the live prefab's `ModuleCargoPart.stackableQuantity` is consulted ONLY for snapshot-less items (a snapshot silent on both values reconstructs as stack-1 on both stock load paths, so the plan must not widen past 1); resource-bearing payloads are forced to 1 (stock forces those to stack size 1 on load).

Delivery actuals are now unit-accurate: `LiveDeliveryWriters.ReadInventoryActualCount` sums stored UNITS (was: manifest lines), `ApplyDeliveryContext.InventoryWriter` carries `(item, slot, units)`, and the delivery summary log reads `inventoryUnits=actual/attempted`. UI formatters read the manifest items (unchanged shape) and needed no change. xUnit: planner multi-slot splitting / volume rejection / stack-capacity resolution (`RouteDeliveryPlannerTests`), admission core + unloaded STOREDPART node builder (`InventoryDeliveryParityTests`), unit-accurate apply (`RouteOrchestratorDeliveryTests`), stack-quantity codec round-trip (`RouteCodecTests`). In-game: `Delivery_LoadedVessel_StacksInventoryQuantityIntoSlot` (PENDING-OPERATOR: needs a FLIGHT vessel with an inventory container holding a stackable cargo part, e.g. an EVA Repair Kit, plus an empty slot) and `Delivery_Probe_AdmissionMatchesIndependentBudget` (read-only, batch-safe: cross-checks the live probe's admission wiring - budget walk, prefab limits, consumption tracking, fail-closed unknown prefab, stackable-quantity read - against an independently computed budget; needs only an inventory container on the FLIGHT vessel).

## ~~FIXED~~ - Batch baseline prime NRE (2026-07-10 verify run) - ROOT-CAUSED via the shipped diagnostics: KSP `EventData<T>.Add` cannot take a STATIC-method delegate (branches `fix-prime-diagnostics` + `fix-restore-rethrow-stack`)

**RESOLUTION (2026-07-10 rerun3, `logs/2026-07-10_2324_rerun3-stack/KSP.log:16339`):** the stack-preserving diagnostics captured the true thrower on the first re-run: `EventData'1+EvtDelegate..ctor -> EventData'1.Add -> FlightCameraReloadPin.Arm -> QuickloadResumeHelpers.CommitValidatedGameLoad`. KSP's `EventData<T>.Add` constructs an `EvtDelegate` whose ctor reads `evt.Target.GetType().Name` (decompiled, 1.12.5), so a delegate to a STATIC method (null `Target`) throws NullReferenceException inside `Add`. `FlightCameraReloadPin` is a static class with static handlers: `Arm` threw on its very first in-game call, so the #1282 camera re-pin window NEVER armed and every batch since aborted at the first isolated-restore prime (the "10-second batch": the entire isolated tier, the slow per-test scene-reload restores, was skipped). `Disarm` never threw because `Remove` compares delegate equality without constructing an `EvtDelegate`. SECOND project occurrence of this trap (the first: a static `GameEvents` OnLoad handler NRE wiped a persistent.sfs index, 2026-06-19). Headless tests could not catch it (no GameEvents at xUnit time) and compile-only review passes it. **Fix:** `Arm`/`Disarm` subscribe through cached non-capturing-lambda delegate fields (`VesselChangeHandler` / `LevelLoadedHandler`; a non-capturing lambda's `Target` is the compiler's closure singleton, non-null; the same instance keeps `Remove`'s equality match). **Pins:** `EventData<T>` is plain C# (no Unity natives), so the trap itself is now pinned headless (`KspEventDataAdd_StaticMethodDelegate_ThrowsNre...` in `FlightCameraReloadPinTests`), plus the cached-lambda idiom and a source-text gate that Arm never regresses to a naked static method group. **Verify:** re-run the FLIGHT Run All + Isolated batch: the prime must proceed (grep `Camera re-pin window armed`), the isolated tier must run (batch takes minutes again, ~441 captured), and the #1282 protection is finally live.

**Original report (2026-07-10 verify run, `logs/2026-07-10_2220_verify-run-followup/KSP.log`):** the FLIGHT batch's first isolated-test prime (`InGameTestRunner.PrimeBatchFlightBaselineBeforeFirstRestoreBackedTest` -> `RestoreBatchFlightBaselineCore`) failed with a bare `NullReferenceException` and aborted the batch at 383/441 captured results.

**Evidence chain (why the site is unrecoverable from this log):** the prime ran before `DiscardEconomyPreservationInGameTest`; all 7 `ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore` prep resets logged cleanly; NO `onGameSceneLoadRequested` handler logs appeared (so the throw landed in the window between the prep returning and `HighLogic.LoadScene` firing); the pre-wipe rollback WARN followed 1ms later. Every catch site on that path logged only `ex.Message` ("Object reference not set to an instance of an object") with no exception type or stack trace, so the throwing statement was swallowed.

**Null-safe-on-inspection candidates (all read clean, none confirmed):** `RevertDetector.ResetForTesting`, `FlightCameraReloadPin.Arm` argument interpolation, `StartAndFocusVessel` statics.

**Regression suspicion:** the previous session primed the SAME test successfully on the pre-#1281/#1282 DLL, so a regression from those PRs cannot be excluded; no mechanism was found on inspection.

**Shipped in the first diagnosability branch (`fix-prime-diagnostics`, PR #1285):**
1. Every restore/prime failure catch site in `InGameTestRunner` now ALSO logs a `ParsekLog.Error` line with the full exception detail (type + message + inner exceptions + stack trace) via the pure, xUnit-pinned `DescribeRestoreFailure(Exception)`; the test-result row keeps the short message. Sites: the prime wrapper, the per-test restore wrapper, `RestoreBatchBaselineWithRecovery` (attempt-1 retry + persistent failure; covers the final-restore and cancel-restore paths), the `PrepareBatchFlightRestoreExecution` catches, and the `CaptureBatchBaseline` isolation-capture catch.
2. The green-sphere leak seen in the same run (a leftover fallback ghost sphere riding the vessel) got a first-pass fix: `PerformPostAbortSceneCleanup` in RunBatch's always-runs batch-end region destroys the tracked `cleanupRegistry` objects and clears timeline ghost visuals (via the idempotent `PerformBetweenRunCleanup`), exception-safe, gated on the abort flag. Residual: the exception-storm abort ALSO sets `abortBatchAfterRestoreFailure` (both storm-detection sites, `TryDetectExceptionStorm` and the reload-guard flood path), so storm endings get the sweep too; the remaining uncovered ending is a user Cancel, which stops the RunBatch coroutine before the batch-end region ever runs, so a cancelled batch still leaves scene debris until the next Run* entry's `PerformBetweenRunCleanup` (pre-existing behavior, accepted).

**2026-07-10 rerun2 result (`logs/2026-07-10_2258_rerun2-fast/KSP.log` line 16364): both #1285 pieces fired but neither yielded the answer.**
- The new diagnostics captured the prime NRE, but the stack showed ONLY the reload-guard wrapper frame (`RestoreBatchFlightBaselineCoreWithReloadGuard.MoveNext` + `RunCoroutineSafely.MoveNext`). Cause: `RestoreBatchFlightBaselineCoreWithReloadGuard` re-raised the captured core failure with a bare `throw coreFailure;`, which RESETS the exception's stack trace to the rethrow site, destroying the original stack `RunCoroutineSafely` had captured from `RestoreBatchFlightBaselineCore`. The evidence window is unchanged: the throw lands after `ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore` returns and before `HighLogic.LoadScene` fires.
- The green sphere recurred despite the #1285 sweep: `PerformPostAbortSceneCleanup` ran but found nothing ("DestroyAllGhosts: clearing 0 primary + 0 overlap entries", "destroyed 0 tracked object(s)"). Root cause: the sphere was never the LIVE engine's. The `ReFlyPostLoadSettle_GhostMeshHiddenDuringWindow` in-game test (RuntimeTests.cs) builds a PRIVATE `GhostPlaybackEngine`; its `UpdatePlayback` spawned a sphere-fallback ghost visual for the snapshot-less "Re-Fly Settle Anchor" recording (log line 14881, GameObject "Parsek_Timeline_0") and the test's teardown only reset the settle tracker, abandoning the private engine WITHOUT `DestroyAllGhosts`. The orphaned mesh was invisible to every cleanup path (they all tear down the live `ParsekFlight` engine only), so the green sphere stayed riding the vessel.

**Shipped in branch `fix-restore-rethrow-stack` (diagnosability round 2 + sphere root fix):**
1. Stack-preserving rethrow: the reload-guard wrapper now re-raises via `ExceptionDispatchInfo.Capture(coreFailure).Throw()` (same exception object, original frames preserved, rethrow site appended). It was the only bare `throw capturedVariable;` in the file (all other sites use catch-callback or plain `throw;`). xUnit pin: an EDI rethrow of a captured exception preserves BOTH the original throwing frame and the rethrow wrapper frame in `DescribeRestoreFailure` output.
2. Capture-point logging: the wrapper logs `Restore core failure detail (captured at reload-guard, original stack):` with the full `DescribeRestoreFailure` detail IMMEDIATELY where the core failure is first observed (right after the `RunCoroutineSafely` yield), where the original stack is provably intact, making the diagnostics immune to any other stack-eating rethrow downstream.
3. Sphere root fix: the settle in-game test now calls `engine.DestroyAllGhosts()` on its private engine in its `finally`, destroying the engine-spawned sphere at test end. No production ghost-engine seam was at fault (the live engine never referenced the mesh).
4. Defensive orphan sweep: `PerformPostAbortSceneCleanup` gained a final step that scans root GameObjects for the engine's `Parsek_Timeline_` naming convention (pure xUnit-pinned `IsOrphanedGhostMeshName`), skips any the live engine still owns (`GhostPlaybackEngine.OwnsGhostGameObject`), destroys the rest, and reports `orphanedGhostMeshes=N` in the existing summary line. Post-abort one-shot only, exception-safe, unreachable on the normal batch path.

**Next step:** re-run the FLIGHT batch. If the prime fails again, the capture-point `Restore core failure detail (captured at reload-guard, original stack)` line (or the prime wrapper's detail line, now stack-preserving) names the true throwing statement inside `RestoreBatchFlightBaselineCore`; diagnose from there. The NRE itself remains UNDIAGNOSED.

---

## ~~FIXED~~ - Test-runner batch soft-freeze recurrence: late vessel switch destroys the FlightCamera after the pre-reload guard (2026-07-10, branch `fix-runner-reload-camera-pin`)

**Found by:** the 2026-07-10 FLIGHT Run All + Isolated re-run (`logs/2026-07-10_2114_rerun-freeze`, KSP.log lines 50840-51260): KSP soft-froze at batch end (black background, no scene change possible, ~250k per-frame NREs, 107MB log). Same stock Bug #4803 class as the 2026-07-05 freeze, but through a NEW hole the existing prevention does not cover.

**Root cause (evidence chain):** after the last isolated test (`EvaTwiceFromSameCapsuleProducesTwoBranches`, which leaves two EVA-kerbal vessels in the scene) the restore ran: (1) 21:11:57.248 the existing pre-reload camera guard (`EnsureFlightCameraSurvivesReload`) fired and re-homed the pivot onto 'Kerbal X' - the 2026-07-05 prevention worked as designed; (2) 21:11:57.464 `QuickloadResumeHelpers.CommitValidatedGameLoad` called `FlightDriver.StartAndFocusVessel(game, activeVesselIdx=9)`, which synchronously fires `onGameSceneLoadRequested`, so stock `FlightCamera.OnSceneSwitch` ran its PSystemSetup DDOL-root rescue INSIDE this call; (3) same frame, AFTER the commit returned, a LATE vessel switch to the transient EVA kerbal 'Hudmy Kerman' fired ("[FLIGHT GLOBALS]: Switching To Vessel Hudmy Kerman"; the live scene's vessel index 9, while the save's index 9 is Kerbal X - an index-space mismatch; the exact caller was never identified, so the fix must not depend on knowing it). The switch fired stock `FlightCamera.OnVesselChange`, re-parenting the pivot under the doomed EVA vessel AFTER the rescue; (4) the unload destroyed the EVA vessel with pivot+camera under it (`FlightCamera.OnTargetDestroyed` refuses to re-home when the dead target is the active vessel), `fetch` went null, and 21:11:58.216 the new scene's `FlightDriver.Start` NRE'd in `SetModeImmediate`, leaving FlightGlobals half-initialized and every per-frame consumer flooding permanently.

**Fix (test-runner/helper code only, two pieces):**
1. **Late-switch camera re-pin window** (`InGameTests/Helpers/FlightCameraReloadPin.cs`): armed inside BOTH batch-restore commit seams (`QuickloadResumeHelpers.CommitValidatedGameLoad` and `CommitNonFlightSceneLoad`, so every caller including the quickload-resume tests is covered) just before the scene-load dispatch. While armed, every `GameEvents.onVesselChange` immediately re-pins the pivot back onto the DDOL PSystemSetup root via `SetTargetTransform` (the exact stock OnSceneSwitch rescue, re-applied after the late switch; it also detaches the on-destroy callback from the dying vessel) and Warn-logs the switched vessel. The runner's restore core additionally does one unconditional end-of-frame pin after each commit (covers same-frame switches that bypass onVesselChange). Disarm: `GameEvents.onLevelWasLoaded` (fires in the new scene before Start() methods, so the new scene's legitimate `FlightDriver.Start` -> `SetActiveVessel` is never intercepted), plus a 60s realtime TTL fail-safe and explicit idempotent disarms in the runner's Cancel/batch-end/cancel-restore teardown paths (StopCoroutine skips finally blocks; mirrors the batch exception monitor teardown). All handler bodies are exception-safe.
2. **One-shot Space Center bounce recovery** (`InGameTestRunner.RunBatch` tail): after the final restore (and on the storm-abort path), the runner samples the existing flood detector over a settle window while the batch exception monitor is still live; if still flooding it logs ERROR + an on-screen message and dispatches EXACTLY ONE `HighLogic.LoadScene(SPACECENTER)` after the disk teardown + results export complete, guarded by a once-per-batch bool (pure decision `ShouldAttemptSpaceCenterBounce`, xUnit-covered).

**Explicit non-goals:** NO reload-retry loops (the disproven model - 2026-07-05 confirmed the corruption is process-permanent for the FLIGHT scene; 4 retries all flooded and produced a 469MB log; prevention + a single SC bounce are the only working models), and NO product-code Harmony patch of stock FlightCamera (test-infra only; the underlying stock bug remains KSP Bug #4803, and the idea of reporting it upstream to KSPCommunityFixes still stands).

**Tests:** `FlightCameraReloadPinTests` (arm-window decision core: ignore/re-pin/TTL-auto-disarm/disabled-TTL) + `ShouldAttemptSpaceCenterBounce` cases alongside the existing `IsExceptionStorm`/`ReloadStillFlooding` tests in `InGameTestRunnerTests`. Full suite green.

**Verify (operator):** re-run the FLIGHT Run All + Isolated batch. The batch must end with a healthy FLIGHT scene (grep for the late-switch Warn to see whether the re-pin window actually caught a switch this run). If the corruption ever recurs anyway, the runner must land you in the Space Center with a `[TestRunner]` ERROR line ("Batch ended with the flight state still NRE-flooding") instead of a frozen game.

---

## ~~FIXED~~ - S4 arrival re-stitch rotation sign inverted on live KSP (found by the 2026-07-10 in-game sweep, branch `fix-s4-restitch-bearing`)

**Found by:** the first full Ctrl+Shift+T sweep on 0.10.3 main (`logs/2026-07-10_1935_ingame-test-sweep`, 5 scenes, 2 failures). `ReaimLandingCoincidenceInGameTest.S4Restitch_RotatedDeorbitPoint_MeetsRecordedSiteAtTrigger` - the exact canary the S4 PR added for the sign question headless tests cannot prove - measured `ComputeRestitchRotationDeg` reading a live LAN=+30 orbit pair as -30 (`S4 frame validation` log line; the physical LAN-advance and body-spin rotations both measured +30 about the live spin axis `(0,-1,0)`).

**Root cause:** the adversarial-review commit (`26922fd28`, S4 BLOCKER 1) fixed the latitude AXIS (Y, not Z) but picked the bearing SENSE as `atan2(-z, x)` from the derivation "Cross(r, v_prograde) points +Y". Live KSP refutes the premise: prograde angular momentum points -Y in the world frame (`CelestialBody.angularVelocity` is -Y for prograde rotators), so `atan2(-z, x)` reads every prograde advance inverted. Production consequence: `RotateLanForParkRephase` turned the arrival chain by -theta instead of +theta (a 2*theta tear at the SOI-entry seam - the exact defect S4 exists to close); the landing site itself was never at risk (the rotation/trigger-offset pairing is sign-agnostic, so the touchdown stayed at the recorded site).

**Fix:** `ArrivalRestitch.TryBearingAndLatitude` bearing = `atan2(z, x)` (live-KSP-calibrated by the canary, not re-derived); doc comments rewritten to name the canary as the calibration source; the four sign-sensitive `ArrivalRestitchTests` pins re-pinned to the corrected sense (quarter-turn prograde/retrograde, magnitude-irrelevance, both velocity-residual cases). The connectivity / recorded-site-invariant / assembler pins are sign-agnostic and unchanged.

**Verify:** re-run the FLIGHT sweep (or just the Periodicity category) in-game: the canary must read +30/+30 with residual 0. The Duna One (s15) looped landing playtest remains the S4 merge-gate observation for the approach visibly connecting.

---

## ~~FIXED~~ - EVA spawn walkback clobbered by the degraded fallback + flaky zero-velocity landed reseed (found by the 2026-07-10 in-game sweep, branch `fix-spawn-walkback-fallback`)

**Found by:** the 2026-07-10 re-run sweep (`logs/2026-07-10_2114_rerun-freeze`, KSP.log lines 14014-14094). `FlightIntegrationTests.EvaSpawnWalkbackOnOverlap` failed with "Walkback should move the EVA off the overlapping endpoint (was 0.0 m)": the walkback found a clear position (cleared at segment [21-22]) and `OverrideSnapshotPosition` wrote it into the snapshot, yet the EVA materialized exactly at the overlapping endpoint (distFromParent=17.3 m = the original overlap distance). The 1935 sweep had passed the same test - run-to-run flaky. Beyond the test this is a real product defect: on the degraded fallback path a vessel could materialize inside an overlap the walkback had just cleared (collision/explosion risk in real gameplay).

**Root cause (two stacked):**

1. *Flaky #620 rejection of the primary spawn.* `SpawnAtPosition` rebuilds the ORBIT node via `OrbitReseed.FromWorldPosAndRecordedVelocity` with the recorded endpoint velocity. A landed endpoint with EXACTLY zero recorded velocity reseeds to h=0 / ecc=1, and KSP's `Orbit.UpdateFromStateVectors` computes SMA = -semiLatusRectum/(ecc^2-1) = 0/0 = NaN; `TryValidateFiniteMaterializationMetadata` then rejects the spawn ("orbit metadata 'SMA' is missing or non-finite", #620) and `SpawnAtPosition` returns 0. A float-residue near-zero velocity yields a finite ~r/2 SMA instead - hence the run-to-run flakiness.
2. *Endpoint clobber in the landed-like repair.* The caller fell through to the degraded fallback (`RespawnValidatedRecording` -> `BuildValidatedRespawnSnapshot` -> `TryRepairSnapshotBodyProvenance`). The landed-like repair branch unconditionally overrode the snapshot position with the recording ENDPOINT coordinates (`bool useEndpointCoords = hasEndpointCoords;`), clobbering the deliberate walkback correction.

**Fix:**

- *Deterministic landed reseed:* when the computed situation is LANDED/SPLASHED and the reseeded orbit carries any non-finite element (`OrbitReseed.HasNonFiniteOrbitElement`, pure + pinned), `SpawnAtPosition` substitutes the canonical surface orbit tuple instead (single implementation `WriteCanonicalSurfaceOrbitValues`, shared with `ApplySurfaceOrbitToSnapshot`). Non-landed situations keep the current rejection (correct there).
- *Deliberate-override stamp:* `OverrideSnapshotPosition` stamps the snapshot with `parsekDeliberatePosOverride=True`; the landed-like repair dispatches through the pure decision predicate `DecideSurfaceRepairCoordinateSource(hasStamp, hasSnapshotPos, hasSnapshotBody, hasEndpointCoords, bodyMismatch?)` -> {StampedSnapshot, Endpoint, Snapshot, Reject}. A stamped snapshot with a finite position on a non-mismatched body keeps its coordinates (the orbit is still repaired); unstamped snapshots keep the endpoint-first contract EXACTLY (that path moves stale EVA-start snapshots to the trajectory endpoint); a stamp never blocks a genuine repair (missing/NaN coords or a body mismatch still get the endpoint repair). The stamp never reaches `persistent.sfs`: it is stripped from every spawn copy before ProtoVessel load (`BuildValidatedRespawnSnapshot`, `SpawnAtPosition`, `RespawnVessel`), and (review follow-up) it is also stripped from the DURABLE `rec.VesselSnapshot` in a `finally` on every spawn-resolution exit of `SpawnOrRecoverIfTooClose` (`StripDeliberatePositionOverrideStampFromRecording`), so a dirty-recording sidecar save (`<id>_vessel.craft`) cannot carry it into a later session where spawn paths that do not re-run the resolved overrides first (KSC end spawn, ghost tip respawn) would mistake stale snapshot coordinates for a fresh deliberate override.
- Tests: exhaustive 48-row truth table for the decision predicate, non-finite-element pins, stamp lifecycle pins, and repair-precedence integration pins through `BuildValidatedRespawnSnapshot` (`SpawnWalkbackFallbackTests.cs`).

**Verify:** isolated re-run of `FlightIntegrationTests.EvaSpawnWalkbackOnOverlap` in FLIGHT (Ctrl+Shift+T): distFromEndpoint must be > 0 and the #620 "orbit metadata 'SMA'" rejection must not appear on the landed EVA spawn (grep for the new "substituting canonical surface orbit" Info line instead).

---

## Added (headless-verified, in-game pin pending) - Pre-Parsek save safety backup (branch `pre-parsek-save-backup`)

**Feature.** The first time Parsek cold-loads a save with no Parsek footprint, it copies that save - before any Parsek write - into a sibling `saves/<Name> (pre-Parsek <local-ts>)/` folder that appears in KSP's Load menu, so a player who tries Parsek and uninstalls it can return to their pristine career. Runs once per save; skips brand-new empty careers; toggle under Settings > Data Management (`autoBackupExistingSaves`, default on).

**Design.** Hook at the top of the cold-load path of `ParsekScenario.OnLoad` (gated `!initialLoadDone`, before `LoadExternalFiles`): a scenario module's `OnSave` cannot precede its own `OnLoad`, so the copied `persistent.sfs` is gameplay-state-pristine (no Parsek funds/science/crew/tech/contract/facility footprint - NOT byte-identical; the empty `SCENARIO{name=ParsekScenario}` KSP injects carries no gameplay data). Idempotency is measured from the on-disk footprint (`Parsek/` dir or a populated `ParsekScenario` node), not the in-memory OnLoad node, so a prior aborted session is caught; the marker file is only a fast-path. The copy is staged into a `.parsek-backup-staging-*` dir in the save folder (not under `Parsek/`, so a failed copy leaves no empty-`Parsek/` false footprint) and atomically `Directory.Move`d into `saves/` as the last step (a mid-copy failure never strands a half-save in the Load menu; orphan staging dirs are swept on load). Scope: persistent.sfs + loadmeta + Ships/ + Subassemblies/ (excludes quicksaves, `Parsek/`, KSP `Backup/`). Fail-open (any parse doubt backs up); fail-loud (Error + on-screen warning, no marker written -> retry next cold load). A missing on-disk persistent.sfs is skipped (and asserted before publish) so a capture failure never fabricates a payload-less "backup". Progress decision parses the on-disk file via `CareerSaveParser`, not fragile live singletons at cold OnLoad. `PreParsekBackup.cs` + `FileIOUtils.CopyDirectory`.

**Tests.** 36 xUnit cases in `PreParsekBackupTests.cs` (ShouldBackup truth table with pinned reason literals incl. footprint-beats-brand-new, SanitizeSaveName, BuildBackupFolderName format + collision, IsBrandNewEmptySave fail-open, HasParsekGameplayFootprint empty/value-only/populated node, IsParsekBackupFolder sentinel/name, CopyDirectory tree/exclude/no-op/failure-warn, settings round-trip + defaults). Full settings suite green.

**PENDING OPERATOR (in-game pin, cannot run KSP headlessly).** These KSP-runtime properties are not unit-testable:

1. **V2/V3 (pristine timing).** Back up a real pre-existing career (make a manual copy of a `saves/<Name>` first), install this DLL, load that career once. Grep `KSP.log` for `[Backup] First-contact backup:` and `[Backup] Captured pre-Parsek backup:` and confirm both appear BEFORE the first Parsek OnSave line for that session. If instead you see `[Backup] Skip: reason=already-parsek-footprint` on a save you know never had Parsek, the OnLoad-before-OnSave assumption is wrong and needs the earliest-pre-write-seam fallback.
2. **V1 (appears in Load list).** After step 1, open Resume Saved Game and confirm the `<Name> (pre-Parsek <ts>)` folder is listed and loads. Note whether its card shows the source save's title (copied `.loadmeta`); if that is confusing, regenerate the title or rely on the folder name.
3. **Idempotency.** Reload the same save several times / quickload / revert: `[Backup]` must log `Skip: reason=marker-present` (or `already-parsek-footprint`) and create no second folder. Then resume the pre-Parsek backup itself: it must log `Skip: reason=is-backup-folder` and not back up the backup.
4. **Disabled + brand-new.** With the setting off, a first-contact save logs `Skip: reason=disabled`. A brand-new empty career (Parsek installed) logs `Skip: reason=brand-new-empty` and gets no twin.

---

## Backlog - prioritized "what to develop next" (compiled 2026-07-06, v0.10.3; Tiers 1-4 freshness-checked 2026-07-11)

Session-compiled prioritized development backlog (survey of git log / open PRs / roadmap / design docs / this file). Ordering doctrine: correctness-first, land-shipped-work-before-new, gameplay-value-per-effort. Two premises corrected during the survey: (1) `roadmap.md` §19.4 lags - logistics **M1-M4 are all SHIPPED** in 0.10.3 (M5 inter-body + M6 legibility were the last two, both since MERGED - see the Tier 1 CLEARED note below); (2) there is **no CI** on the repo (`get_status` = 0 checks), so "ready" PRs are review-gated only (suite run locally).

### Tier 1 - CLEARED (2026-07-11): merge queue drained
Every Tier 1 merge-queue item below LANDED on `main` (verified 2026-07-11 via `gh pr view`; `gh pr list --state open` returns 0 open PRs). Kept here for history:
- **#1242** (logistics Rec-1 rewind-redelivery) - MERGED (gate playtest passed 2026-07-08). Was the one open correctness bug (rewind past a route dispatch charged funds but never re-delivered cargo).
- **#1237** (M-MIS-11 loop-unit API) - MERGED. Keystone zero-behavior refactor.
- **#1239** (M-MIS-5 P1 dock-as-interval-boundary) - MERGED.
- **#1238** (Logistics M5 inter-body) - MERGED (gate passed in-game 2026-07-08). Last logistics "Reach" milestone.
- **M6 legibility batch** #1232 / #1233 / #1234 / #1235 / #1236 - all MERGED.
- **#1220 / #1221** - CLOSED as superseded by #1242 (their docs shipped inside it).

### Tier 2 - NEXT: highest value-per-effort new work
- **Route-timeline events** (branch `logistics-route-timeline`) - SHIPPED: player Pause / Activate now emit free-standing `RoutePaused` / `RouteResumed` (new type 30) ledger rows (armed pause-after-cycle emits `RoutePaused` at the delivery applier with the delivered reason token), Send Once provenance is persisted (`Route.SendOnceArmed`, sparse) and stamped on the dispatched row (`GameAction.RouteSendOnce`, sparse) so the one-shot run is bracketed dispatch-to-pause in the timeline, and `Route.CreatedUT` (sparse) records the creation point at `RouteStore.AddRoute`. All new rows are Rec-1-retired at rewind (types 23-30 in `RouteLedgerRetire.IsRouteActionType`). Auto-flip rows LIFTED 2026-07-19 (branch `logistics-dormant-ui`): a LIVE `RevalidateSources` pass now emits `RoutePaused` (`AutoPause:MissingSourceRecording` / `AutoPause:SourceChanged`) on the into-source-problem edge and `RouteResumed` (`AutoResume:SourcesRestored`) when recovery restores an Active-family status (a restored Paused emits nothing). Emission is caller-gated (`liveEmitUT` param, default -1 = silent). Live player-driven ERS-mutation sites route through `ParsekScenario.BumpSupersedeStateVersionLive` (re-fly merge commit, tree-discard purge + marker clear, Re-Fly discard dialog, revert Retry/Discard handlers, unfinished-flight Seal/Stash), which resolves the UT defensively AND forces -1 while `ParsekScenario.OnLoad` is on the stack (central guard - a scene-change load can see a stale nonzero Planetarium UT). Deliberately silent flips remain: the OnLoad revalidation call sites, `MergeJournalOrchestrator.RunFinisher`'s crash-recovery re-drive of `FlipMergeStateAndClearTransient` (explicit `onLoadContext: true`), and the mid-rewind supersede rollback in `RecordingStore.DropSupersedesRewoundOutOfExistence` (a row stamped there would land in the rewound-out future). Those silenced flips are repaired by the CATCH-UP net: every live pass emits `RouteResumed` (`AutoResume:CatchUp`) for any Active-family route whose latest kept lifecycle row (via ELS) still says paused - idempotent, so pause-history desyncs (e.g. the RouteModule dispatch-on-paused Warn loop) self-heal on the next live pass. Still not built: any UI surfacing of the pause history. The `delivered-replay` idempotency-branch contract hole the markers inherited (review finding 3, PR #1327) is FIXED (branch `route-rewind-status-fidelity`): the replay branch now honors an armed `PauseAfterCurrentCycle` - it consumes both flags, transitions `Paused` (reason `delivered-replay-then-paused`), emits the `RoutePaused` marker at the window's stride slot (+4), flushes the owed recovery credit, and drops held escrow; the delivery/funds dedup semantics are unchanged (the marker is the only new row).
- **Route rewind-visibility extension (dormant routes)** - SHIPPED (branch `logistics-route-dormant`, plan `docs/dev/plans/plan-route-rewind-dormant-visibility.md`). The plan review found the premise everywhere else assumed was FALSE: `RouteStore.LoadRoutesFrom` is cold-start-only, so an in-session rewind never reverted routes at all - post-cutoff routes kept firing before their creation point, and pre-cutoff routes kept abandoned-future loop cursors that silently swallowed re-flown cycles (partially defeating Rec-1). Shipped fix: `ReconciliationBundle.Restore(cutoff)` classifies captured routes via `RouteRewindClassifier` (post-cutoff -> DORMANT_ROUTES list, invisible + non-firing; pre-cutoff -> forward-looking cycle state reconciled), and `RouteStore.MaterializeDueDormantRoutes` (top of orchestrator Tick) re-materializes each dormant route Paused when the timeline reaches its `CreatedUT` (occupied source tree -> dropped, live intent wins; missing sources -> visible MissingSourceRecording; round-trip pairs re-link via LinkRoutes). Residuals: ~~dormant routes have no UI and are undeletable until they materialize~~ LIFTED 2026-07-19 (branch `logistics-dormant-ui`): the Logistics window now shows a collapsed "Dormant Routes (N)" disclosure (name + "appears at date" + Delete via `RouteStore.RemoveDormantRoute`), which also closes the resurrection-surprise corner (the player can see and delete the twin before its CreatedUT). ~~`CompletedCycles`/`SkippedCycles` stay inflated after a rewind~~ FIXED (branch `route-rewind-status-fidelity`): the rewind seam derives each KEPT route's timeline-correct pause state from the kept PLAYER-DRIVEN `RoutePaused`/`RouteResumed` rows (`DeriveTimelineStatus` + `ApplyDerivedTimelineStatus`; AUTO `AutoPause:`/`AutoResume:` rows skipped; a derived Active requires a kept player pause row; validity statuses keep their live status with the verdict landing on `PreMissingStatus`), unconditionally clears the armed one-shot flags, and reconstructs the cycle counters from kept rows (`ReconstructCycleCounters`; dispatched-but-undelivered counts as skipped UNLESS a kept in-flight cycle is retained, where the sum lands ON that cycle's ordinal so a straddling cycle keeps its id and dedups). Still accepted: legacy routes without `CreatedUT` never go dormant. The compat report's axis-A "definition/counters revert via .sfs" claim (risk #9 "sound") carries a correction addendum.
- **Go-back rewind route reconcile** - FIXED (branch `fix-goback-route-reconcile`, found by the 2026-07-19 preservation-branch forensic audit). The dormant-routes extension above only wired the FIRST in-session OnLoad exit (Re-Fly: `RewindInvoker.ConsumePostLoad` -> `ReconciliationBundle.Restore(cutoff)`); the SECOND exit - the plain go-back rewind / Rewind-to-Launch / warp-back path, `ParsekScenario.HandleRewindOnLoad` - had zero route handling AND no route-row retire (the audit's assumption that `Ledger.PruneOrphanActionsAfterUT` covers it was verified FALSE: that prune sits on the revert branch only, and the go-back path preserves the in-memory static ledger untouched). Consequences before the fix: kept routes carried abandoned-future loop cursors (re-played cycles silently swallowed, "funds spent, no goods" again), and routes created after the rewind target stayed committed, visible, and firing before their own creation point. Fix: the Re-Fly seam's route block was extracted into the shared `RouteRewindClassifier.ReconcileStoreAtRewind` (behavior-identical at the Restore(cutoff) call site; both exits now share one code path and cannot drift), and `HandleRewindOnLoad` now calls `Ledger.RetireFutureRouteActionsAtRewind` (in-place Rec-1-parity retire, cutoff = `RewindContext.RewindAdjustedUT`, the UT the loaded save reverted the world to) followed by the shared reconcile, before the career cutoff walk; no ledger actions are emitted on this OnLoad path. Gated by `RouteGoBackRewindReconcileTests` (both-exits parity fixture + in-place-retire semantics + source-text hookup/ordering gate).
- **Route-rewind wave automated coverage** (branch `route-rewind-autotests`, stacked on #1330/#1331/#1332/#1333) - BUILT: the manual playtest runbook for the wave is replaced by the in-game `RouteRewindTimeline` category (`Source/Parsek/InGameTests/RouteRewindTimelineRuntimeTests.cs`, 7 scene-agnostic batch-safe tests) plus the unattended harness scenario `H6-route-rewind-timeline` (tier daily; mirrors the H5 RunTests driver). Covered live: lifecycle rows at the real Planetarium UT via `TryPause`/`TryActivate`/`TrySendOneCycleNow`, `ReconciliationBundle.Capture -> Restore(cutoff)` dormanting + kept-route status derivation / armed-flag clears / counter reconstruction + the Rec-1 retire, pending-science cutoff drop (strict-> boundary + blind rollback), dormant re-materialization through the real `RouteOrchestrator.Tick` (one test waits on the production `ParsekScenario.Update` 1 Hz tick itself), live `RevalidateSources` AutoPause/AutoResume/CatchUp rows against real ERS/ELS, and the go-back seam components (`Ledger.RetireFutureRouteActionsAtRewind` in-place + the shared `ReconcileStoreAtRewind`). S1.5/S4.1 (operator tier) gained the Re-Fly Restore-side contracts (`ConsumePostLoad: restoring bundle with route-retire cutoffUT=`, `Restored: ... pendingScience=`). Still genuinely manual (external runbook `../parsek-route-rewind-playtest-runbook.md`): Logistics-window rendering (dormant section, Sending-one-cycle label states, screen messages) and the REAL scene-load exits (`HandleRewindOnLoad` go-back, live `ConsumePostLoad`), which need an operator-piloted rewind.
- **Rec-3 reverse-on-discard** - RESOLVED (2026-07-06, option C): the observability slice SHIPPED (PR #1243, branch `claude/development-priorities-ftr2ye`, stacked on #1242) and both-persist is RATIFIED as correct; reverse writers are DECLINED, not built. The attribution blocker (ambient route rows carry no RecordingId, so a UT-window reverse would wrongly undo concurrent committed routes) plus the 0.10.2 preserve-live-earned-gameplay doctrine make keeping both funds + cargo the correct behavior. No further code work. See `docs/dev/plans/fix-logistics-rewind-determinism.md` Phase 4.
- ~~**Map-view route lines** (M6 gameplay, M) - the one unbuilt M6 gameplay item; draw route paths on the map/TS via the MapRender Director surface. Reuse `GhostTrajectoryPolylineRenderer`.~~ SHIPPED (M6, verified 2026-07-11). `Display/RouteTrajectoryLineRenderer.cs` walks `RouteStore.CommittedRoutes`, reuses `GhostTrajectoryPolylineRenderer.BuildLegsForRecording` + `TryDrawLeg`, clips each route to `RecordedDockUT`, and draws on the flight map + Tracking Station behind the `showRouteLines` setting (default on); same-body routes draw all recorded non-orbital legs, inter-body routes draw the endpoint-body legs. Shipped in commits `008bb30bb` + `7b298582d` (inter-body follow-up), xUnit + in-game covered. Remaining deferred slice (by design, not "unbuilt"): the static orbital-coast overview arc stays head-gated on the stock conic.
- ~~**M-MIS-5 P2** (L) - lift the undock->undock shuttle mid-recording start-trim limitation (`MidRecordingStartTrimUnsupported=9`); unlocks multi-stop shuttle logistics routes rejected today. Prereq: #1239.~~ **SHIPPED 2026-07-08** via #1251 (P2a detector) + #1254 (P2b start-trim lift). Supported shape accepted, degenerate shapes still rejected (NOT a full removal of status 9): an undock->undock mid-tree docked origin with a committed tree, `>=2` completed connection windows, and a finite non-overlapping origin window is now admitted with origin = the first window's undock UT (`RouteAnalysisEngine.IsSupportedMidTreeDockedOrigin` wired into the analysis gate + stand-downs; `RouteBuilder` mid-tree-origin plumbing; `RouteBackingMission.ComputeStartExcludedIntervalKeys`; `Route.RecordedOriginUndockUT` persisted; updated reject text in `RouteCreationFormatters`). Status `MidRecordingStartTrimUnsupported=9` still fires for the degenerate remainder (null/legacy `AnalyzeRecording` tree, origin window overlapping the next stop, inverted origin window, the mid-tree-origin-proof variant), which stay intentionally out of scope per `docs/dev/done/plans/plan-mmis5-p2b-start-trim.md` section 7.

### Tier 3 - LATER: verification + hygiene
- **Validation debt (the real bottleneck)** - code-complete-but-in-game-unconfirmed fixes, clustering onto ~4-5 playtest sessions: (1) career-economy (Rec-1 #1242 gate PASSED in-game 2026-07-08; still open: career-freeze milestone-storm, contract-discard-desync, OnMainMenuTransition); (2) looped re-aim descent-render (reaim-descent cluster, arc truncation, M-MIS-2 P4, cross-SOI encounter observation); (3) eccentric-target Eeloo/Moho constant pinning (M-MIS-3); (4) cross-parent station resupply (M4c); (5) in-game test-runner camera-survival batch. KSP cannot run headless, so this is playtest-bound.
- **M-MIS-10 archetype verification sweep** - constellation deploy / booster flyback / off-Kerbin launch / claw couples / Elcano; cheap verify-and-file, no known break.
- **Remove `MapRenderWarpControl`** temporary debug aid once re-aim descent-render is signed off.
- ~~**Doc hygiene** - flip the stale "In progress - Forward trajectory rendering" header (shipped 0.10.2) + add SHIPPED markers to roadmap §19.4 M3/M4.~~ DONE (verified 2026-07-11): roadmap §19.4 already marks M1-M5 SHIPPED and no "In progress / Forward trajectory rendering" header remains in the roadmap or this file.
- **Deferred re-aim solver follow-ups** - ~~M-MIS-2 S4 re-stitch (product-decision-gated)~~ SHIPPED (PR #1263 `reaim-s4-restitch` + sign fix #1279); leg-less-chain forward-run gap remains (low-severity polish). (`SolveArrivalWindow` wiring SHIPPED on branch `mmis4-solve-arrival-window` - see the M-MIS-4 entry.)

### Tier 4 - LONG-HORIZON: the strategic arc
- **Gloops extraction -> Gloops.dll** (XL) - gateway to multiplayer. Docs UNDERSTATE the effort (engine coupling re-accreted; a parallel Gloops recorder #435 to consolidate first) AND the real user-facing prerequisite is the `.gloop` file format + export/import, NOT the assembly split (`.prec` is already a `.gloop` superset), so export/import can be built on existing serialization and the split treated as a parallel code-health track. Don't start until logistics/missions are done - every in-flight feature still edits the engine files.
- **Phase 14 co-op async multiplayer** -> **Phase 15 space race** -> **Phase 16 mod compat**.
- **Parked mission shapes** - ~~M-MIS-6 (multi-moon, needs a design note)~~ BUILT + MERGED (PR #1256; design note `docs/dev/design-mission-multimoon-alignment.md` done; gated only on an in-game looped-Jool playtest observation) and ~~M-MIS-8 (cross-tree foreign dock, low value)~~ MERGED (PR #1261). Still parked: **M-MIS-7** (intra-SOI re-aim, gated on M-MIS-6 playtest evidence). Hold pending a concrete player ask.

### Open maintainer decisions (surfaced this session)
- **Rec-3**: RESOLVED 2026-07-06 - ratified both-persist as correct (option C); reverse writers declined. See Tier 2 / plan Phase 4.
- **Rec-2** (inter-body route hard-block): RESOLVED 2026-07-19 - inter-body routes ratified as supported, no creation gate needed. M5 inter-body synodic-faithful scheduling SHIPPED in 0.10.3 (PR #1238, roadmap 19.4, in-game gate passed 2026-07-08), so the visual-faithfulness concern behind the decision (report risk #12/#13) is resolved; delivery was always functional.

Healthy / no action needed (verified this session): the ledger/economy audit (all 5 recs shipped), the observability plan (landed), the render rewrite (cutover complete, no visible artifacts left). The pure-refactor backlog is low-ROI - ride it along with features.

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
- **M-MIS-7 go/no-go:** observational evidence from a real looped Jool tour (encounter seams rendering connected across aligned windows, the amber/faithful outcome on an incommensurate shape) remains wanted; collect it opportunistically from normal play (not a merge blocker).
- **Viability:** ~~moderate~~ built - the resonant-inner-three + tidally-locked case maps onto shipped primitives; the general (Bop/Pol, non-resonant packs) case intentionally fails closed with amber (the design records the align-the-resonant-subset alternative as deferred to M-MIS-7 evidence).

### M-MIS-7 - Intra-SOI re-aim and multi-hop targets (Jool-like systems second cut; Ike-class targets) [GATED: on M-MIS-6 playtest evidence]

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

## ~~TODO - Overlapping / duplicate TrackSections written around on-rails seams~~ (observed 2026-06-12; producer FIXED 2026-07-11)

**Observed (2026-06-12 audit, log `logs/2026-06-12_1915_m4c-save-recording-crew-audit`, recording `041770246260406ab85b59495eb51f45` in the "orbital supply route" save):** the committed transfer recording contains pairs of TrackSections covering the same UT span twice. Sections [18] (ref=Absolute) and [19] (ref=2/on-rails) both cover 6675562.034 -> 6676373.107 (the Mun SOI entry), and on-rails section [46] (6737626.584 -> 6738050.344) exactly spans the union of its neighbors [45] + [47]. `FindTrackSectionForUT` is ambiguous inside those windows (which section wins depends on scan order). No playback warning was observed in the session, so the impact is latent; the flat points stayed monotonic.

**CONFIRMED + WIDESPREAD via the offline analyzer (2026-07-11, branch `autotest-analyzer-tuning`, INV2-NO-DOUBLE-COVER over 8 real saves).** The double-cover fires on every flown save with checkpoint bridging: c1 (25 overlaps), s15 (12), orbital supply route (12), mun transfer mission (1), and the c1_wiped snapshot (1). Two sub-patterns, both between `ReferenceFrame.OrbitalCheckpoint` / `TrackSectionSource.Checkpoint` sections (dump via the `RecordingSectionDump` triage tool, `Source/Parsek.Tests/Analyzer/RecordingSectionDump.cs`):
  1. **Exact-duplicate span**: an EMPTY physical section (`ref=Absolute, frames=0`) coexists with an OrbitalCheckpoint section covering the same span, e.g. c1 `1cc165308fbb4ac78016c28462bb17ff` sections [22] (Absolute, frames=0) and [23] (Checkpoint, checkpoints=1), both `[233256.190,234304.378]`.
  2. **Envelope + sub-spans**: a coarse checkpoint `[X,Z]` coexists with finer checkpoints `[X,Y]` + `[Y,Z]`, e.g. c1 `6a15ca9e-d734-4c99-9da5-7be92d2974ab` sections [24]`[288785.73,290565.24]` + [25]`[288785.73,453543.36]` (envelope) + [26]`[290565.24,453543.36]`.

**Producer (suspected 2026-07-11, CONFIRMED by the fix):** `OrbitSegmentCheckpointBridge` (`Source/Parsek/OrbitSegmentCheckpointBridge.cs`). Its anti-overlap machinery (`BuildSegmentsOutsidePhysicalSections`, `ClipExistingCheckpointSectionsAgainstPhysicalSections`, `SkippedCovered` / `AnyCheckpointMatches`) clips new checkpoint sections against PHYSICAL sections and dedups EXACT checkpoint matches, but it does NOT clip a checkpoint span against OTHER checkpoint sections, and it does not reconcile against an EMPTY physical section (a `frames=0` Absolute shell left by a re-fly `SplitAtUT` or flush-stitch). So an envelope checkpoint and its sub-checkpoints, or an empty physical shell and its checkpoint, can coexist over the same UT. The intent is disjoint producers (the machinery proves it), so overlaps are a real gap, not a legitimate coarse+fine representation — INV2 keeps them FAIL (no type-aware exemption added).

**Fix (2026-07-11, PR #1304, branch `fix-checkpoint-double-cover`):** producer-side, in `OrbitSegmentCheckpointBridge`. The bridge now (a) prevents checkpoint-vs-checkpoint overlap with direction-aware freshness: `EnsureCheckpointSectionsForTopLevelOrbitSegments` promotes flat-cache candidates sections-win (candidates come from the stale flat cache; only the uncovered remainder is promoted, attaching into an exact-span empty checkpoint shell when one exists), while the live `TryAppendClosedCheckpointSection` path is newest-wins (a fresh on-rails close clips overlapping EXISTING checkpoint sections instead of being discarded - physical frame-bearing sections still own their spans, exact duplicates skip); the flat-cache rebuild preserves any flat-only segment, predicted or real, whose span no payload section covers (replacing the fragile pure-predicted-suffix rule that dropped interleaved tails), and (b) reconciles payload-less shells (no frames, no bodyFixedFrames, no checkpoints, not seam-flagged): a shell fully covered by payload-bearing sections is removed, a partly covered one is trimmed to the uncovered remainder. The reconcile also runs for recordings with zero flat OrbitSegments (post-review fix; the original early return skipped atmospheric/surface-only recordings). Runs on every sidecar write, so all producer flows (recorder flush, BG close, split, merge) converge. Old-data contract is normalize-on-rewrite, NOT byte-freeze (ratified 2026-07-12): the sidecar READ sites pass `reconcileEmptySections: false` so the empty-shell reconcile never mutates a committed recording's sections at load (candidate clipping still runs there because it only constrains new adds, and the pre-existing checkpoint-vs-physical clip of existing sections also still runs there - a legacy heal seam that predates the gate), and files no sanctioned flow dirties stay byte-identical; a recording dirtied by any sanctioned flow (re-fly merge, tail finalizer, the dirty-marking legacy read seam, ...) is rewritten through the write-path Ensure and comes out reconciled. Historical saves stay RED on INV2 until rewritten or baselined (verified: c1 = 25 overlaps on the read path both pre- and post-fix; a freshly written synthetic save = 0). `reconciledEmptySections` counter + `reconcile=on/off` token in the bridge stats log line. Tests: `CheckpointDoubleCoverTests`.

**Sibling anomaly (WARN, same producer family):** a lone far-future 0.18s `OrbitalCheckpoint` section can sit ~1.5M seconds after a recording's real coverage ends, leaving a genuine uncovered gap. Example: c1 `14c10c43ad45417db38d5d9674fe239d` records an atmospheric ascent ending at UT 475816, then carries a single checkpoint section `[2054279.18,2054279.36]` (ORB #0 matches). This surfaces as `INV2-UNCOVERED-SPAN` WARN (above the 8.0s micro-seam tolerance floor) and is a stray far-future checkpoint / orphaned orbit tail, distinct from the double-cover.

**Harness impact:** these are TRUE POSITIVES; the affected historical saves (c1, s15, orbital supply route, mun transfer mission, c1_wiped) stay RED on INV2-NO-DOUBLE-COVER until their recordings are rewritten by a sanctioned flow or a per-save known-findings baseline lands (PR #1306, in progress). New recordings are clean.

## ~~TODO - INV2-NO-DOUBLE-COVER at the debris-split stop/resume seam on crashed-flight saves~~ (triaged + fixed 2026-07-22, branch `fix-inv2-stopresume-overlap`)

**Observed (2026-07-22, B5 Mun impact, harness runs 0024/0135/0206):** every debris-only staging separation leaves a one-tick (0.02-0.04s) interior TrackSection overlap: `FlightRecorder.FinalizeRecordingState` closes the active section at the stop-frame UT (overhanging its last recorded frame by up to one sample gap), then `RestoreTrackSectionAfterFalseAlarm` back-aligns the reopened section's startUT to the boundary-seed UT (the last frame). Normally healed by `SessionMerger.MergeTree` at CommitTree (`overlapsResolved=N`), but a vessel destroyed before commit routes through `FinalizeTreeRecordings` -> `StashPendingTree` and OnSave persists the pending tree raw, so the overlap reaches the `.prec` sidecar and the analyzer reds (FAIL=3 at the three booster-separation boundaries, deterministic: red iff destroyed-before-commit). NOT a #1304 regression (that was checkpoint-bridge overlaps; this is the physical stop/resume seam, a different producer family). Full triage: `docs/dev/research/inv2-double-cover-triage-2026-07-22.md`.

**Fix (2026-07-22):** producer-side, at the seam. `FlightRecorder.TryClampClosedSectionEndToBoundarySeed` (called from `RestoreTrackSectionAfterFalseAlarm` before the continuation section reopens) clamps the just-closed section's endUT down to the boundary-seed UT so the two sections touch exactly (which INV2 treats as clean), and recomputes the section's sampleRateHz for the shrunk duration. No data loss: nothing is recorded in the overhang window, and the clamp refuses (leaving the old shape for MergeTree to heal at commit) when the last section carries any authored payload - frames, body-fixed frames, or checkpoints - past the seed, when the seed would undercut the section's own startUT, or when there is no overhang. The two diagnosable refusals (`would-invert-section` / `payload-past-seed`) Warn-log with the section UTs; the common no-overhang no-op stays silent. MergeTree's commit-time overlap resolution becomes a no-op for this shape instead of load-bearing. Tests: `EnvironmentTrackingIntegrationTests` (B5-mirror seam test with the exact triage UTs + `TryClampClosedSectionEnd_*` guard matrix + refusal-Warn seam test).

**Known residual (accepted, warn-logged):** if the closed section's only frame is a boundary seed written before its own startUT (staging within one sample interval of an env transition), the back-align reopens BELOW the closed section's startUT and the clamp correctly refuses (would invert the section), so a containment double-cover can still reach the sidecar on a destroy-before-commit persist. Pre-fix behavior was identical, the shape needs an unlucky sampling coincidence, and the refusal Warn line makes it grep-diagnosable; revisit only if the analyzer actually reds this sub-shape in a real run.

**Deliberately NOT done:** the triage's belt-and-braces option (running MergeTree's overlap reconcile when OnSave persists a pending/stashed tree). OnSave fires mid-session (autosave, scene change) while the recorder is actively appending to the pending tree; mutating section topology there is a much larger behavioral risk than the source clamp, and the producer fix removes the only known raw shape. Revisit only if the analyzer reds a pending-tree persist from a different producer.

**PENDING-OPERATOR (one-flight live verification):** re-run the B5 Mun-impact scenario (staged craft, boosters separate, crash before commit, quit) on a build with this fix and confirm `scripts/analyze-recordings.ps1` over the save reports zero INV2-NO-DOUBLE-COVER findings (RED=0 modulo unrelated findings) and KSP.log shows the new `clamped closed TrackSection endUT` line at each separation. Historical crashed-flight saves (the 0355/0506/0537 collects) stay RED - recorded files are immutable and are not rewritten.

## ~~TODO - Offline-analyzer retroactive-review follow-ups (F1/F2/F3)~~ (done 2026-07-12, branch `analyzer-followups`)

Three optional follow-ups from the retroactive Fable review of PR #1302 (the INV9 tuning), all landed:
- ~~**F1 - INV9 severity split by MergeState.**~~ DONE (2026-07-12). A `CommittedProvisional` recording (a recent / still-active slot) whose own non-empty `RewindSaveFileName` dangles now -> INV9 FAIL (token `missing-rewind-save-provisional`); an `Immutable` (sealed) or any other recording stays WARN (`missing-rewind-save`). The split is a triage-severity heuristic, not a production contract: rewindability of the `parsek_rw_` save is MergeState-agnostic (`GetRewindRecording` / `CanRewind` never read `MergeState`), but a dangling Rewind-to-Launch save on an open provisional slot is far likelier a live bug than the shared-delete residue tolerated on sealed rows (narrow benign false-FAIL: a provisional root whose shared save a sibling deleted, recoverable by baselining). `Inv9RewindPoint` reads `Recording.MergeState` purely (the loader already hydrates it). Historical-saves impact: all 8 missing-rewind recordings across the 5 baselined saves (s15 4, orbital supply route 4) are `Immutable`, so none escalate; all 5 stay GREEN under Apply and no baseline refresh was needed. Tests: `Inv9RewindPointTests.MissingRewindSave_Provisional_Fails_DistinctToken` / `_Immutable_Warns_NotFails` / `PresentRewindSave_Provisional_NoFindings`.
- ~~**F2 - INV9 unparsable-rewind-save FAIL path unit test.**~~ DONE (2026-07-12). The only file-state FAIL in the rule (rewind save present on disk but not parseable as a ConfigNode) was untested; added `Inv9RewindPointTests.UnparsableRewindSave_Fails`, which writes a whitespace-only `.sfs` at the referenced `Parsek/Saves/<id>.sfs` (KSP's `ConfigNode.Load` returns null on it, which the rule reads as unparsable -> FAIL).
- ~~**F3 - RecordingSectionDump output out of the triaged save.**~~ DONE (2026-07-12). `RecordingSectionDump` wrote `<save>/analysis/<id>.sectiondump.txt` INSIDE the save under triage. The default now routes to the analyzer results-dir convention via `ResolveResultsDir()` (precedence: `PARSEK_DUMP_RESULTS`, else `PARSEK_ANALYZER_RESULTS`, else `AppContext.BaseDirectory/section-dumps` = the test output dir), so the tool never writes into a save and is literally read-only over saves. Header docs updated.

## TODO - Ensure with markDirty:false mutates committed recordings in memory on optimizer analysis passes (filed 2026-07-13, pre-existing class)

`RecordingOptimizer.FindSplitCandidatesForOptimizer` (RecordingOptimizer.cs ~594) walks committed recordings and calls `EnsureCheckpointSectionsForTopLevelOrbitSegments` with `markDirty: false`; the bridge can add/clip/reconcile sections in memory there, so the in-memory model silently diverges from disk until the next write. Pre-existing hazard class (the pre-#1304 Ensure already added/clipped at this site); the #1304 reconcile pass widened the mutation set. Benign today (mutations are payload-preserving and converge at the next write), but a strictly read-only analysis pass should not mutate. Candidate fix: an analysis-only Ensure mode, or compute candidates on a copy. Not fixed in #1304 (scope).

## TODO - Section-index-keyed .pann annotations can desync when Ensure shifts section indices (filed 2026-07-13, pre-existing class)

`SectionAnnotationStore` keys splines/outlier-flags/anchor-candidates by (recordingId, sectionIndex); freshness gates on (SidecarEpoch, formatVersion, configHash) and out-of-band sidecar writes deliberately do not bump the epoch (bug #290 note in RecordingSidecarStore.cs ~1159). Any Ensure pass that removes/inserts/re-sorts sections (pre-existing: `ClipExistingCheckpointSectionsAgainstPhysicalSections`, `EnsureTrackSectionsSorted`; new in #1304: the empty-shell reconcile and newest-wins clip) shifts later sections' indices while in-memory annotations survive (only `SmoothingPipeline.RemoveRecording` invalidates), so a spline computed for old index k can apply to a different section. Candidate fix: key annotations by section identity (startUT) or invalidate the recording's annotations whenever Ensure reports Changed. Not fixed in #1304 (scope).

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

**References:** full investigation + ranked options doc at the umbrella root `reaim-seam-investigation.md`; log snapshot `logs/2026-06-15_1906_duna-mission-investigation/`; engaged params `KSP.log:13201` (`ENGAGED re-aim Kerbin->Duna via Sun; D0=142619013 synodic=19653075 tof=6854613`).

**ATTEMPTED + REVERTED (2026-06-17) - option 3 (whole-chain synthesis). STILL OPEN; do not re-attempt without the preconditions below.** The deferred "option 3" was built behind a default-OFF flag (P0/P1 foundation #1169, P2-P4 chain synthesis #1170) and then REVERTED (#1171 reverts #1169; #1170 closed). Plan/design docs at the umbrella root: `reaim-fix-plan.md`. Why it was abandoned:
- **It regressed the flag-ON render and never solved the bug.** First playtest (Kerbal X #2): synth escape/capture legs were garbage hyperbolae (`[ReaimSeam] chain legs: escape ecc=12.9, capture ecc=7.77`; sane is ~1.05-1.3), so departure+arrival misaligned, Duna-SOI arrival render broke, icon teleported. Log `logs/2026-06-16_2351_reaim-chain-kerbalx2-regression/`.
- **Root cause (confirmed):** `ReaimTransferSynthesizer.BuildBodyRelativeLeg` paired the transfer POSITION at the SOI crossing with the Lambert ENDPOINT velocity v1/v2 (at the planet center), an inconsistent state vector. Fixed (use `transfer.getOrbitalVelocityAtUT(crossingUT)`) + added the plan's never-built periapsis/ecc fail-closed gate (`IsSaneLegConic`), so flag-ON fails closed to baseline instead of rendering garbage.
- **But it still could not be validated:** every available test mission is a fail-closed case. Duna One threads Ike (capture fails closed). Kerbal X #2 is a heliocentric-parking (two-burn) departure (escape fails closed) AND its re-aim render is independently broken by the #1166-engages-but-#1167-Increment-2-not-built span-greater-than-synodic gap, not by this work. Validating option 3 needs a CLEAN DIRECT Kerbin->Duna recording: no parking-orbit loiter, no moon (Ike) encounter. No such mission exists in the test save.
- **Structural doubt:** a center-to-center Lambert solve carries no information about the real ejection/capture periapsis, so the synth body-relative legs inherit whatever periapsis the SOI-crossing sample implies; the gate fails the bad ones closed, but a "consistent but wrong-altitude" leg can still pass. The premise may need rework (e.g. SOI-edge-to-SOI-edge solve, or seed the ejection from the recorded parking periapsis) rather than center-to-center.

**Do NOT re-attempt option 3 without (a) a clean direct no-park no-moon Kerbin->Duna looped recording to validate against, and (b) resolving the center-to-center-periapsis structural doubt.** Confirm a validatable test case exists BEFORE building (this arc was built end-to-end before that check, which was the core process mistake).

## BUG-C (2026-06-07 career playtest) - `R2-B2` tree instability + NaN debris -> stock exceptions

Source: `logs/2026-06-07_1638_career-playtest/` (KSP.log, `BUGS.md` BUG-C section). Build `Parsek V0.10.0` @ `07dea8fac`. The player used NO Parsek features this session (no rewind / re-fly / loop / playback); Parsek was only background-recording. Three log signatures, separable root causes. BUG-C is largely fallout of BUG-A (ledger recalc) and BUG-B (passive ghost / vessel auto-spawn), which are tracked separately.

### 1. NaN debris -> stock `FlightIntegrator.UpdateOcclusionSolar` throw - STOCK KSP, not Parsek data (no fix)

Two `ArgumentOutOfRangeException` throws in stock `FlightIntegrator.UpdateOcclusionSolar` (KSP.log lines 205423 @15:46:42, 218683 @15:50:46), each immediately around `R2-B2M-S6 Debris had a NaN Orbit and was removed` + an on-rails Kerbin->Sun SOI transition.

Origin is **pure stock physics**, confirmed:
- `R2-B2M-S6 Debris` (pids 1333358833 / 2800168062) is the player's **real staging debris**, created at 15:42:22 by a real decouple (`Decouple created vessel during recording ... rootPart=radialDecoupler`), with real drag cubes, terrain collision (`crashed through terrain on Kerbin`), and explosions. It is NOT a Parsek ghost/spawn: line 204856 `CleanupOrphanedSpawnedVessels: no match for 'R2-B2M-S6 Debris'` is Parsek explicitly disclaiming ownership.
- Parsek background-recorded the debris and then **finalized + deleted** those recordings as non-persistable (`canPersist=False`, `DeleteRecordingFiles`) at 15:42:39, ~4 minutes before the NaN. No Parsek recording carried or authored the NaN orbit.
- The throw is the well-known stock pattern: debris clips through terrain, is packed on rails with a degenerate velocity, the resulting hyperbolic orbit escapes Kerbin->Sun, and `UpdateOcclusionSolar` indexes a body list off a NaN-derived value and throws before stock's own NaN-orbit removal runs.

Decision: do **not** Harmony-patch stock `FlightIntegrator` to swallow this. It is not Parsek data, it reproduces without Parsek, and guarding a stock NaN path from a mod is high-risk for little gain (the proper home for a stock-bug shim is KSPCommunityFixes). Filed as known-stock, no Parsek code change.

### 2. Terminal-orbit ghost "permanently abandoned" 3x - BUG-B fallout + a real durability gap (FIXED here)

`[Policy] Spawn-death detected for terminal orbit and will not be retried: #32 "R2-B2-S5" ... reason=spawned-terminal-orbit-vessel-died` fires 3x (15:46:17, 15:56:43, 15:58:58), each with a fresh Parsek-spawn pid (3390689712 / 3495642311 / 3732877540) and `deathCount=1`.

Traced each cycle: `SpawnAtPosition: vessel spawned (ORBITING, body=Sun, alt~13.4 Gm)` -> Parsek's own `CleanupOrphanedSpawnedVessels: recovering 'R2-B2-S5' (matched by name)` immediately recovers it -> `RunSpawnDeathChecks` sees it gone -> `MarkCannotSpawnSafely`. The spawn itself is **BUG-B**: Parsek auto-materializes a committed terminal-orbit `vessel`-type recording during passive play, then orphan-recovers it.

The durability gap (the part fixed here): `RunSpawnDeathChecks` sets `Recording.TerminalSpawnCannotSpawnSafely = true` ("will not be retried"), and `VesselSpawner.SpawnOrRecoverIfTooClose` (`VesselSpawner.cs:1688`) honours that flag as a pre-spawn guard. But the flag was **transient** (`Recording.cs`, "do not serialize"), so every scene reload reset it to false and the vessel re-spawned. The first spawn each session happens before the flag is set, and `TryPassTerminalOrbitSpawnSafety`'s live orbit-geometry re-check passes (a 13.4 Gm heliocentric coast is geometrically "safe"), so only the recorded spawn-death can stop it - and that was being forgotten.

Fix: persist `TerminalSpawnCannotSpawnSafely` + `TerminalSpawnSafetyReasonCode` so the abandon survives a reload, on BOTH load paths. (1) Cold start (fresh game load, `RecordingStore.ClearCommittedInternal` then `LoadRecordingTrees`): the `RecordingTreeRecordCodec` save/load (`SaveMutablePlaybackState` / `LoadRecordingResourceAndState`) round-trips the keys when the committed trees are rebuilt from disk. (2) In-session load (scene change / quickload / revert, the `returned-scene-change` branch of `ParsekScenario.OnLoad`): that path reconciles the in-memory committed recordings instead of rebuilding them - it resets every recording's terminal spawn-safety via `TerminalOrbitSpawnSafety.Clear` (~line 2320) then restores only the saved subset, so the new `ParsekScenario.RestorePersistedTerminalAbandon` re-applies the flag from the saved RECORDING node (absent on a revert quicksave, so the abandon correctly does not carry across a revert). Either way the flag is true on the next scene, so the existing `VesselSpawner.cs:1688` pre-spawn guard blocks the re-spawn and the "will not be retried" log becomes truthful across reloads. The observed 3x repro went through path (2), so the codec change alone would not have fixed it. The soft altitude-deferred hold (`TerminalSpawnSafetyDeferred`) is deliberately left transient so it re-evaluates against the propagated orbit. Tests: `RecordingTreeTests.RecordingTree_TerminalSpawnCannotSpawnSafely_RoundTrips` / `RecordingTree_NoTerminalSpawnAbandon_StaysFalseOnLoad` (codec path) + `SpawnStateReconciliationTests.RestorePersistedTerminalAbandon_*` (in-session path) + `TerminalOrbitSpawnSafetyGeometryTests` (pins the geometry-vs-durability split itself: the 13.4 Gm heliocentric coast is admitted by the geometry re-check and stopped only by the persisted abandon, while the non-finite altitude / periapsis / apoapsis family is rejected outright). This is defense-in-depth; the upstream cure (don't auto-spawn during passive play at all) is BUG-B. Note: each orphan recovery runs a stock vessel-recovery (`Recovery processing captured ... recoveryFactor=...`), a candidate contributor to BUG-A's funds drift - flagged for the BUG-A session.

### 3. Active-tree save skipped - correct-by-design merge-consent guard, root is BUG-B-adjacent identity collision (no fix here)

`[Scenario] SaveActiveTreeIfAny: skipped active tree 'R2-B2-S5' because at least one recording could not be written with current v0 sidecars` + `skipped dirty sidecar save for committed-restore overlap recording 'bb53...'` (lines 165907-165925, 15:04).

This is the merge-consent guard in `SaveActiveTreeIfAny` (`ParsekScenario.cs:1380-1390`): a dirty recording that is an `IsCommittedTreeRestoreAttemptRecordingId` (and not a marker-owned switch segment) is skipped to avoid overwriting committed history before merge consent. **No committed sidecar is corrupted**, and in this log only one recording was dirty (the overlap clone `bb53...`, 1 buffered point), so there is no meaningful new-data loss - the guard behaved correctly.

The real defect is upstream and BUG-B-adjacent: the active tree was created by `TryRestoreCommittedTreeForSpawnedActiveVessel` treating the player's **fresh-rollout real vessel** `R2-B2-S5` (pid 590316933) as a committed-spawned-clone. This is the documented craft-baked-`persistentId` collision (a new launch of the same craft reuses the baked pid that prior committed recordings of that craft also carry). The fresh-rollout fast-path ("matches captured scene-entry pid") correctly skipped restore at 14:55, but after the 15:04 scene reload the captured scene-entry pid no longer matched and it fell through to committed-tree restore - routing a normal flight into the re-fly merge-consent path. The correct cure is launch-identity (`RecordedVesselGuid` / `VesselLaunchIdentity`) discrimination at the restore site, which belongs with BUG-B / the identity subsystem, not the save guard. No safe save-path change here.

~~Latent secondary (noted, not fixed): `SaveActiveTreeIfAny` early-returns and skips the WHOLE active-tree node when any one recording is a committed-restore overlap, even if a legitimately-new marker-owned switch-segment recording in the same tree had its sidecar written - that would orphan the sidecar (no tree node references it). Not triggered destructively in this log; flagged for the switch-segment owner.~~ **FIXED** (both-or-neither sidecar invariant).

**Fix:** `SaveActiveTreeIfAny` no longer writes sidecars while it is still deciding whether the tree node can be written. Both skip predicates (the committed-restore merge-consent guard, and the hydration-failed empty-sidecar-overwrite guard) are pure functions of recording state, so the whole active tree is now CLASSIFIED first by the new `ParsekScenario.PlanActiveTreeSidecarSaves` (`internal static`, returns an `ActiveTreeSidecarSavePlan` carrying the write candidates + the existing #280 counters + `AllRecordingsWritable`). When the plan reports a skip, `SaveActiveTreeIfAny` logs the grep-stable `outcome=both-or-neither deferredSidecarWrites=<n>` Warn and returns having written NOTHING; otherwise it runs the deferred write pass and then serializes the node. Either the node and its sidecars are both persisted, or neither is. The predicates, their per-recording Warn / Verbose lines, the marker-owned bypass and the counters are unchanged - only the ordering moved, so the merge-consent contract and the Phase-D switch-segment narrowing both still hold. Residual (documented in-code, irreducible): a genuine I/O failure inside the write pass is only knowable by attempting the write, so it can still leave earlier sidecars written with no tree node - but a recording whose sidecar write failed is already not-current on disk either way. Second surface, correct by design and noted for completeness: under a skip the OTHER recordings' fresher sidecars are deliberately not flushed either (without the tree node they would be unreferenced), so they stay `FilesDirty` and are written by the next save that clears the skip - the guard defers those writes, it does not drop the data. Because the whole `Scenario.OnSave` path is not drivable from xUnit, the classify pass is unit-tested directly (`SaveActiveTreeSidecarBothOrNeitherTests`, including the exact todo shape: one committed-restore overlap + one marker-owned switch-segment recording, asserting the segment is a write candidate while the plan is not writable, order-independently) and the classify-before-write ordering is pinned by a source gate in the same file.

---

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

## 640. Stock committed-future overlay v2 follow-ups

**Status:** TODO - future investigation / review item from PR #721.

PR #721 ships the v1 scope: stock R&D, Astronaut Complex, and Mission
Control committed-future overlays, plus click-blocks for duplicated tech,
contract accept, kerbal hire, and facility upgrade actions. The following
ideas are deliberately out of v1 scope and should be reviewed as separate
follow-ups after in-game verification:

- KSC facility-upgrade visual overlays in the top-down KSC view. The
  click-block already exists via `FacilityUpgradePatch`; v2 would add the
  visual badge and extend the overlay/click-block invariant to facilities.
- Future-completed / future-failed contract badges in Mission Control, not
  only future-accepted contract badges.
- Administration strategy activation overlays, paired with matching
  click-block behavior if the stock UI has a clickable affordance.
- Per-row claim / override UI for cases where the player intentionally wants
  to bypass a committed-future action, instead of using the global setting.
- Per-user dismissible badges for "hide this warning until next session" style
  workflows.
- Non-stock screen integrations, such as Contract Configurator's own Mission
  Control replacement or other mod-provided building screens.
- Modded flight-scene building overlays. The current v1 overlays are
  `SPACECENTER` scene-bound, while the lower-level click-blocks remain
  scene-agnostic.
- Tooltip styling polish using KSP's richer
  `KSP.UI.TooltipTypes.TooltipController_Text` path instead of the v1
  `GUI.skin.box` fallback.

**Review guidance:** keep the v1 invariant intact for every clickable action:
if a stock or modded UI exposes a clickable affordance, the overlay candidate
set and the click-block predicate must share the same `MilestoneStore` source
helper, with any UI-only suppression kept outside the click-block predicate.

---

## Phase 6 known gaps (deferred to later phases)

- ~~§7.7 BubbleEntry / BubbleExit candidates are not emitted by the Phase 6 builder.~~ Shipped: `AnchorCandidateBuilder.EmitBubbleEntryExitCandidates` walks adjacent `TrackSection` pairs and emits at every `Active|Background ↔ Checkpoint` source-class transition; `IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos` reads the LAST/FIRST physics-active sample as the high-fidelity world reference. Mainline shipped this at `AlgorithmStampVersion=5`; on the Phase 5 stack it lands inside the v8 alg-stamp window. Residual gap: RELATIVE-frame physics-active sections adjacent to a Checkpoint segment are deferred with a `bubble-entry-exit-relative-section-deferred` Verbose (uncommon in practice — vessel docked to its anchor while a Checkpoint splices in).
- ~~§7.8 CoBubblePeer anchors are reserved in the enum but emit no candidates.~~ Obsolete: the co-bubble subsystem was retired in PR #912 (v0.9.3). The enum slot 7 (formerly `CoBubblePeer`) is now `Reserved7`, kept only to preserve the persisted `.pann` `AnchorCandidatesList` byte layout; there is no co-bubble pipeline. Close-formation accuracy is delivered by the parent-anchored debris contract instead.
- The 2.5 km bubble-radius HR-9 Warn (`RenderSessionState.cs:836-848`) only fires from the LiveSeparation path inside `RebuildFromMarker`. Anchors written via `AnchorPropagator.TryWriteAnchor → PutAnchorWithPriority` (§7.4 / §7.5 / §7.6 / §7.7 / §7.10) skip the magnitude check, so a non-LiveSeparation ε of, say, 12 km lands silently. Lift the magnitude check into `PutAnchorWithPriority` (or the per-source dispatch) in a follow-up PR so all anchor types are uniformly guarded — pre-existing gap, not introduced by §7.7.
- §7.9 SurfaceContinuous emits a marker only with ε = 0; the per-frame terrain raycast that resolves ε is Phase 7 work. Phase 6 demoted the rank from 2 to 6 to prevent the zero stub from winning ties against real OrbitalCheckpoint ε; Phase 7 must promote back to rank 2 once the resolver ships and bump `AlgorithmStampVersion` so existing `.pann` re-resolve.
- The split anchor sources (Undock / EVA / JointBreak) currently share the `DockOrMerge` enum byte (priority rank 4 either way). Logs label them by `BranchPointType` rather than by enum value to preserve telemetry granularity. If a future phase needs to differentiate split priorities from dock priorities, expand the `AnchorSource` enum and bump `AlgorithmStampVersion`.

---

## 435. Multi-recording Gloops trees (main + debris + crew children, no vessel spawn)

**Source:** world-model conversation on #432 (2026-04-17). The aspirational design for Gloops: when the player records a Gloops flight that stages or EVAs, the capture produces a **tree of ghost-only recordings** — main + debris children + crew children — all flagged `IsGhostOnly`, all grouped under a per-flight Gloops parent in the Recordings Manager, and none of them spawning a real vessel at ghost-end. Structurally the same as the normal Parsek recording tree (decouple → debris background recording, EVA → linked crew child), with the ghost-only flag applied uniformly and the vessel-spawn-at-end path skipped.

**Guiding architectural principle:** per `docs/dev/gloops-recorder-design.md`, Gloops is on track to be extracted as a standalone mod on which Parsek will depend. Parsek's recorder and tree infrastructure will become the base that both Gloops and Parsek share — Gloops exposes the trajectory recorder + playback engine, Parsek layers the career-state / tree / DAG / world-presence envelope on top via the `IPlaybackTrajectory` boundary. Multi-recording Gloops must therefore **reuse Parsek's existing recorder, tree, and BackgroundRecorder infrastructure** rather than growing a parallel Gloops-flavored implementation. The ghost-only distinction is a per-recording flag on top of shared machinery, not a separate code path.

**2026-04-19 boundary note:** `GhostPlaybackEngine.ResolveGhostActivationStartUT` no longer casts back to `Recording`; the engine now resolves activation start from playable payload bounds through `PlaybackTrajectoryBoundsResolver` over `IPlaybackTrajectory`. #435 remains otherwise unchanged, but this leak is no longer part of the extraction risk surface.

**Current state (audited 2026-04-17):**

- `gloopsRecorder` is a **parallel** `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`) — a temporary workaround that the extraction direction wants to retire.
- `BackgroundRecorder` is never initialized in the Gloops path — only alongside `activeTree` for normal recordings. Staging during a Gloops flight does not produce a debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`), so EVA does not produce a linked crew child either.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, adds it to the flat `"Gloops - Ghosts Only"` group (`RecordingStore.cs:394-418`). No `CommitGloopsTree`, no nested group structure.
- No conditional `IsGloopsMode` branch inside `RecordingTree`, no half-finished Gloops tree scaffolding.

**Net: Gloops is strictly single-recording by design today**, implemented as a parallel workaround. Multi-recording Gloops is a separate, sizable feature that should also consolidate Gloops onto the shared Parsek recorder (retire the parallel `gloopsRecorder` path).

**Desired behavior:**

- Gloops uses Parsek's main `FlightRecorder` + `RecordingTree` + `BackgroundRecorder` path, with a tree-level `IsGhostOnly` flag propagated to every leaf at commit. No parallel `gloopsRecorder`.
- Starting a Gloops recording creates a `RecordingTree` with the ghost-only flag; normal recording continues alongside on the same machinery if already active, or the tree operates solo if not. How the two modes interleave in the UI (explicit toggle, implicit based on UI state, etc.) is for the implementing PR to decide — possibly in coordination with a UI gate preventing concurrent career + Gloops capture.
- Staging during a Gloops flight → debris gets its own ghost-only recording via the normal `BackgroundRecorder` split path, with `IsGhostOnly = true` inherited from the tree.
- EVA during a Gloops flight → linked child ghost-only recording via the normal EVA split path.
- Commit: the whole Gloops tree flushes as a nested group under `"Gloops - Ghosts Only"` — e.g. `"Gloops - Ghosts Only / Mk3 Airshow Flight"` with child debris / crew recordings under it. Every leaf is `IsGhostOnly`.
- No vessel-spawn-at-end for any recording in a Gloops tree. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` already gates on `!rec.IsGhostOnly` (see `GhostPlaybackLogic.cs:3001`); the tree case reuses this.
- Per-recording delete / regroup / rename in the Recordings Manager works the same as normal trees.
- Apply-side: #432's filter reads `rec.IsGhostOnly` per-recording, so every leaf in a Gloops tree is already excluded from the ledger with no extra work.

**Files likely to touch (sketch, not exhaustive):**

- `Source/Parsek/ParsekFlight.cs` — retire `gloopsRecorder` in favor of the main `recorder`/`activeTree` path; the "Start Gloops" action creates a tree flagged ghost-only. `CheckGloopsAutoStoppedByVesselSwitch` goes away or is folded into normal tree commit.
- `Source/Parsek/FlightRecorder.cs` — remove `IsGloopsMode` branches once the parallel recorder is retired; the recorder becomes agnostic to career semantics (aligning with the extraction boundary in `gloops-recorder-design.md`).
- `Source/Parsek/BackgroundRecorder.cs` — carry a tree-level ghost-only flag so debris children inherit it.
- `Source/Parsek/RecordingStore.cs` — collapse `CommitGloopsRecording` into the normal tree commit path; the ghost-only distinction is per-tree (or per-leaf, if partial-Gloops trees ever become a thing, which they shouldn't).
- `Source/Parsek/UI/GloopsRecorderUI.cs` — controls now drive the main recorder with a ghost-only flag rather than spinning up a parallel instance.
- `Source/Parsek.Tests/` — tree-structural tests for multi-recording Gloops capture and commit.

**Dependencies / sequencing:**

- Ships after #432 (which closes the existing single-recording leak and establishes the per-recording `IsGhostOnly` apply-side filter that multi-recording Gloops will rely on).
- Coordinates loosely with the Gloops extraction work (`docs/dev/gloops-recorder-design.md` Section 11 — the extraction sequence); ideally this consolidation happens before extraction so the extraction moves a single unified recorder, not two.
- Not tied to the deterministic-timeline correctness cluster — this is a feature extension, not a correctness bug.

**Out of scope:**

- Making Gloops spawn real vessels at ghost-end (explicitly not wanted — Gloops is visual-only).
- Turning the existing single-recording Gloops path into a tree retroactively for existing saves (beta, restart the save if you want the new behavior).
- Actually extracting Gloops into its own mod. That's covered by `docs/dev/gloops-recorder-design.md`'s extraction plan. #435 is a preparatory consolidation step on the Parsek side.

**Priority:** Medium. Feature extension + architectural cleanup. Worth scoping after #432 lands.

**Status:** TODO. Size: L. New feature — not a follow-up to anything shipped today.

---

## 430. "Why is this blocked?" explainer for the committed-action dialog

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Partial mitigation:** PR #721 adds stock R&D / Astronaut Complex / Mission Control row badges with tooltips for committed-future actions, including the event UT and source recording when available. This helps before the click, but does not replace the structured blocked-action dialog below: the dialog still needs conflict context, Timeline navigation, and the rewind shortcut.

**Desired behavior:**

- Replace the one-line reason with a structured block:
  - The action the player tried (e.g. "Research node: Heavier Rocketry").
  - The committed action that blocks it, including the source recording and its UT (e.g. "Already scheduled at UT 183420 in recording 'Mun Lander 3'").
  - A `Go to Timeline` button that opens the Timeline window and scrolls to the offending entry (reuses `TimelineWindowUI.ScrollToRecording`).
  - A `Revert to launch` shortcut if the player actually wants to undo it (routes to the existing rewind dialog pre-filled with the blocking recording).
- Keep the OK/close path unchanged so existing muscle memory still works.

**Why it matters:**

The mental model of "you can't do this because the timeline already did" is counter-intuitive for a first-time player. Showing the *which* and *when* turns a mysterious block into a debuggable constraint, reinforcing the ledger-as-truth principle every time a block fires.

**Files to touch:**

- `Source/Parsek/CommittedActionDialog.cs` — extend the dialog body; accept an optional `blockingRecordingId` + `blockingUT` + `blockingAction` tuple.
- `Source/Parsek/Patches/*Patch.cs` (where blocks are triggered for tech research / facility upgrade / part purchase) — pass the conflict context into the dialog instead of just the short reason string.
- `Source/Parsek/UI/TimelineWindowUI.cs` — already has `ScrollToRecording`; no changes beyond what's there.

**Out of scope for v1:**

- Auto-resolving the block by rewinding silently; this stays an informational dialog, not a one-click rewind.
- Collapsing multiple overlapping blocks into a summary (each block fires its own dialog as today).

**Status:** TODO. Size: S-M. Best quality-per-effort of the paradox-comms work.

---

## 428. Preview-rewind pane

**Source:** follow-up on the "cost-of-rewind is hard to intuit" thread. Rewind is the most consequential single action in Parsek — it moves the player back to a chosen launch point and replays forward with existing ghosts. But right now the rewind confirmation dialog shows a single summary line ("Rewind to 'Mun Lander 3' at Y1 D23?") and a raw count of "how many future recordings exist". A player can't tell before confirming: which exact recordings will be preserved, which will be replayed, which resources / contracts / milestones will be re-rolled, whether crew reservations will shift.

**Desired behavior:**

- Replace the existing one-line confirmation with a two-pane preview dialog anchored on the rewind button.
- Left pane: **"Before rewind point"** — committed recordings whose `EndUT <= rewindTargetUT` (stay intact on the ledger and their ledger effects remain applied); game-action milestones that already fired before the target; crew reservations that complete before the target.
- Right pane: **"Re-rolled forward"** — committed recordings whose `StartUT > rewindTargetUT` (they stay committed; their resource deltas + events re-apply from the target UT forward as the player plays); milestones pending at UT > target (they'll re-fire); crew reservations spanning the target (stand-in chain resets).
- Each pane shows a count + a preview list of the first ~5 items with `...and N more` if longer.
- Confirm / Cancel buttons unchanged.

**Why it matters:**

Rewind currently feels like a commitment to the unknown — the player isn't sure what they'll lose. Making the consequences legible before the dialog closes reduces regret and teaches the two buckets (before / re-rolled), which is the honest mental model: rewind is deterministic replay, nothing is thrown away.

**Files to touch:**

- `Source/Parsek/UI/RewindConfirmationUI.cs` (new or extension of the existing confirmation helper — current code is inlined in `RecordingsTableUI.ShowRewindConfirmation`).
- A `RewindPreview.Build(recordings, ledgerActions, milestones, rewindTargetUT, liveUT)` pure helper that classifies each item as "before rewind point" or "re-rolled forward". Lives next to `TimelineBuilder` since both walk similar data.
- Tests: classification helper fully covered (happy path + each bucket's edge cases + an item spanning the target UT).

**Out of scope for v1:**

- Previewing the new resource balance after rewind. Just show counts + first few items.
- Undo for rewind. One-way operation stays one-way.

**Status:** TODO. Size: M-L. Biggest UX win per dollar on the rewind mechanic.

---

## 427. Proactive paradox warnings surface

**Source:** follow-up on the conversation after shipping the Career State window. Today the mod prevents paradoxes mostly via blocks (action-blocked dialog) and a single red over-committed warning in the Timeline's resource footer. There's no centralized surface that says "your committed timeline has these N potential issues" — so a player can build up a career with, e.g., a contract that expires before its committed completion, or a facility upgrade requiring a level that won't be reached in time, and only discover the contradiction when it fires (or silently zeroes out).

**Desired behavior:**

- A **Warnings** badge on the main ParsekUI button row — hidden when count is 0, shown as `Warnings (N)` when any warning rules fire.
- Clicking opens a small scrollable window listing each warning as a row:
  - Category tag (`Contract`, `Facility`, `Strategy`, `Resource`, `Crew`).
  - One-line description (`Contract "Rescue Kerbal" deadline UT 240000 is before committed completion at UT 250000`).
  - `Go to ...` button linking to the relevant other window (Timeline scroll, Career State tab, etc.).
- Warnings are computed once per `OnTimelineDataChanged` fan-out (same cache-invalidation channel everything else uses).
- Starter rule set, each as a pure static helper in `WarningRules.cs`:
  - **ContractDeadlineMissed** — active contract's `DeadlineUT < terminal-UT of its committed completion recording`.
  - **FacilityLevelRequirement** — an action requires facility level N but the facility doesn't reach N until after that action's UT.
  - **StrategySlotOverflow** — projected active strategies > projected max slots (currently only warned in log, not UI).
  - **ContractSlotOverflow** — same for contracts.
  - **CrewDoubleBooking** — a stand-in appears in two chains at overlapping UT ranges.
  - **ResourceOverCommit** — already shown in Timeline budget footer, but also listed here for one-stop-shop.

**Why it matters:**

Action blocking catches paradoxes at the moment the player tries to violate them. Warnings catch *latent* contradictions that the ledger can detect but won't error on — the subtle ones where the ledger silently picks a resolution the player didn't intend (e.g. contract gets zeroed out because its deadline passed unexpectedly). Surfacing these early turns the mod's "structural paradox prevention" into a communicated design contract rather than a hidden invariant.

**Files to touch:**

- `Source/Parsek/UI/WarningsWindowUI.cs` — new scrollable list window.
- `Source/Parsek/WarningRules.cs` — new pure-static rule evaluators, one method per rule, each returning `List<Warning>` given `(ledger, recordings, modules)`. Heavy unit-test coverage.
- `Source/Parsek/ParsekUI.cs` — add the badge button + open toggle; integrate with `OnTimelineDataChanged` cache invalidation.
- `Source/Parsek.Tests/WarningRulesTests.cs` — one test per rule (happy + each flag condition).

**Out of scope for v1:**

- Auto-fix for any warning. Pure read-only surface.
- Severity levels / color-coding. All warnings are equal in v1; add severity in a follow-up if there are too many of one kind.
- Per-rule disable toggles. Playtesting can decide which rules feel noisy before we add knobs.

**Status:** TODO. Size: M. Complements the help popup (#426) — where help explains the system, warnings explain *your career's* specific issues. Together they turn the mod from "learn by experimenting" to "learn by seeing the model."

---

## 426. In-window help popups explaining each Parsek system

**Source:** follow-up conversation during the #416 UI polish pass. A player unfamiliar with the mod has to read `docs/user-guide.md` (out of the game) to understand what each window's sections and columns mean. The mechanics are specific enough (slots vs. stand-ins vs. reservations, per-recording fates, timeline tiers, resource budget semantics, etc.) that even tooltips-on-hover don't carry the full picture. An in-game help surface keeps the explanation next to the thing it explains.

**Desired behavior:**

- A small `?` icon button rendered in the title bar (or as the last button in the main toolbar row) of each Parsek window: Recordings, Timeline, Kerbals, Career State, Real Spawn Control, Gloops Flight Recorder, Settings.
- Clicking the `?` opens a small modal-ish popup window titled `Parsek - {Window} Help` anchored next to the parent window.
- The popup body is static help text tailored to that window. For tabbed windows (Kerbals, Career State), the help content should also cover each tab, either as one scrolling document or as a small tab-match sub-structure inside the popup. Keep each section brief (5-15 sentences) — the goal is orientation, not exhaustive docs.
- A "Close" button and `GUI.DragWindow()` so the popup can be moved.
- Help text can be hard-coded string constants in `Source/Parsek/UI/HelpContent/` (one file per window). No runtime load, no localization for v1.
- Suggested starter content:
  - **Recordings** — column-by-column walkthrough, L/R/FF/W/Hide button meanings, group vs chain vs ghost-only distinction.
  - **Timeline** — Overview vs Details tiers, Recordings/Actions/Events source toggles, time-range filter, resource-budget footer, GoTo cross-link.
  - **Kerbals** — slots vs stand-ins vs reservations (Roster State tab), chronological outcomes per kerbal (Mission Outcomes tab), outcome-click-scrolls-Timeline.
  - **Career State** — contracts / strategies / facilities / milestones tabs, current-vs-projected columns when the timeline holds pending recordings, Mission Control / Administration slot math.
  - **Real Spawn Control** — what it does (warp-to-vessel-spawn), State column, 500m proximity trigger.
  - **Gloops** — ghost-only manual recording, loop-by-default commit, X delete button in Recordings.
  - **Settings** — group-by-group overview (Recording, Looping, Ghosts, Diagnostics, Recorder Sample Density, Data Management); call out Auto-merge, Auto-launch, Camera cutoff, Show-ghosts-in-Tracking-Station.

**Out of scope for v1:**

- Inline tooltips on every sub-control (hover-tooltips already exist for a few buttons; expanding them is a separate follow-up).
- Localization / translation.
- Interactive tutorials.
- Search within help content.
- External hyperlinks (no browser launch from KSP IMGUI reliably).

**Files to touch:**

- New: `Source/Parsek/UI/HelpWindowUI.cs` (shared small popup window; takes a `windowKey` + body-text source).
- New: `Source/Parsek/UI/HelpContent/*.cs` (one static class per window, each exposes `public const string Body` or a `BuildBody()` method if dynamic content is needed later).
- Each existing window UI file (RecordingsTableUI, TimelineWindowUI, KerbalsWindowUI, CareerStateWindowUI, SpawnControlUI, GloopsRecorderUI, SettingsWindowUI): add a small `?` button and an `IsHelpOpen` toggle that feeds HelpWindowUI.
- `ParsekUI.cs`: add a single shared `HelpWindowUI` field + accessor so every window delegates to the same instance (only one popup open at a time).
- `CHANGELOG.md` entry under Unreleased.
- `docs/user-guide.md` can mention the new `?` buttons briefly but stays as the authoritative long-form reference.

**Status:** TODO. Size: M. Style it the same way as the rest of the mod (shared section headers, dark list box for paragraph groups, pressed toggle idiom if any sub-tabs appear).

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

2026-04-25 update: deferred spawn queue outside-physics-bubble waits are no longer
a spam source; the per-recording kept line and repeated warp-ended summary were
replaced with a rate-limited queue wait summary.

2026-04-25 update (UnfinishedFlights + missed-vessel-switch):
`logs/2026-04-25_1314_marker-validator-fix/KSP.log` was 96 MB / 540k lines, of
which ~511k (94%) were `[Parsek][VERBOSE][UnfinishedFlights]
IsUnfinishedFlight=…` decisions and ~1k were `[Parsek][WARN][Flight] Update:
recovering missed vessel switch` lines. Both fired from per-frame paths:
`EffectiveState.IsUnfinishedFlight` is invoked once per recording per frame from
`RecordingsTableUI` row drawing, `UnfinishedFlightsGroup` membership filtering,
and `TimelineBuilder`; the missed-vessel-switch warn fires in `ParsekFlight`
`Update()` until the recovery handler clears the predicate, which in this
playtest took dozens to hundreds of frames per vessel. Each of the 7 return
paths in `IsUnfinishedFlight` now uses `ParsekLog.VerboseRateLimited` keyed by
`{reason}-{recordingId}` so each (recording, reason) pair logs once per
rate-limit window. The missed-vessel-switch warn now uses
`ParsekLog.WarnRateLimited` keyed by `missed-vessel-switch-{activeVesselPid}`
so each vessel logs at most once per window. Regression
`EffectiveStateTests.IsUnfinishedFlight_RepeatedCallsSameRec_RateLimitedToOneLine`
calls the predicate 100x with the same recording and asserts a single emitted
line.

2026-04-25 update (post-#591 second-tier cleanup): the `2026-04-25_1933_refly-bugs`
KSP.log surfaced six more spam sources, addressed as numbered bugs #592-#596
(closed in this commit) plus #597 (open underlying-logic concern). #592 covers
the ~3300 `Time warp rate changed` / `CheckpointAllVessels` / `Active vessel
orbit segments handled` lines from KSP's chatty `onTimeWarpRateChanged`
GameEvent. #593 covers ~1190 lines from repeatable record milestones
(`Records*` IDs) re-emitting the same `Milestone funds` / `stays effective` /
`Milestone rep at UT` line on every recalc walk. #594 covers 221 KspStatePatcher
bare-Id fallback lines. #595 widens the OrbitalCheckpoint playback and Recorder
sample-skipped rate-limit windows from 1-2s to the default 5s. #596 gates the
PatchFacilities INFO summary on having actual work. #597 later closed the
underlying duplicate checkpoint work with a same-tree/same-rate/same-UT guard
plus recorder-level duplicate-boundary idempotence.

2026-04-26 update (observability Phase 1 current spam hygiene): the newest
retained package `2026-04-26_0118_refly-postfix-still-broken` surfaced a
different top-repeat set: finalizer-cache periodic summaries, repeated
patched-snapshot missing-body/captured pairs, repeated extrapolator seeded
orbital-frame-rotation lines, and small GhostMap cleanup/window repeaters. This
branch keys finalizer summaries by owner/recording/terminal state, removes the
no-delta Info backstop, keeps only the first unique classification at Info,
gates patched-snapshot and OFR-seeding details with `VerboseOnChange`, and
rate-limits empty GhostMap cleanup plus diagnostics missing-sidecar warnings.
The follow-up also gates repeated all-zero ledger summaries and sandbox/no-target
KSP patch skips with `VerboseOnChange`. Focused xUnit log assertions pin each
gate. Remaining broader audit work stays tracked by the Observability Audit
section above.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

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

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

Latest investigation: a second procedural-truss attempt was tested against fresh collected logs in `logs/2026-04-13_1529_fairing-truss-artifact`. The run correctly detected `FairingJettisoned` and rebuilt the ghost with `showMesh=True`, but the generated truss still looked bad in game: visible dark bars with transparent gaps following the fairing outline from base to tip. This confirms the simplified procedural replacement is still not shippable.

Important constraint: the current ghost snapshot is just a normal `ProtoVessel`/`ConfigNode` capture (`BackupVessel` output copied into `GhostVisualSnapshot`). That preserves fairing state such as `fsm`, `ModuleStructuralNodeToggle.showMesh`, and `XSECTION`, but it does not preserve the live runtime-generated stock Cap/Truss mesh deformation/material state from `ModuleProceduralFairing`. So the ghost cannot reproduce the exact stock truss visual from snapshot data alone.

To implement properly: prefer a stock-authoritative approach instead of another simplified procedural mesh. Most likely options are either capturing the live stock fairing truss render/mesh state at record time, or spawning/regenerating a hidden stock fairing from the snapshot and cloning the resulting stock truss renderers for the ghost. Only fall back to custom geometry if it can genuinely match stock quality.

**Status:** Open — do not revive the current simplified procedural-strip truss

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort
