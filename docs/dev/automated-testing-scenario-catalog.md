# Parsek Automated Testing - Scenario Catalog

Companion to `automated-testing-plan.md` (v2). This file is the coverage
backbone: the dimension registry, the atomic mission blocks, the tiered
scenario ladder, the regression replay list, and the fixture sources.
Sources: repo design docs + bug archives (mined 2026-07-11), community
mission resources (web survey 2026-07-11). Plain ASCII.

---

## 1. Coverage-dimension registry (D1-D17)

Every scenario is a point in this dimension space. A scenario spec declares
which dimension values it covers; the coverage report is computed against
this registry. Full per-value source-file citations live in the mining
report; headline values only here.

- **D1 Recording lifecycle**: auto-record on launch / on EVA / on
  first-modification-after-switch (all shipped defaults, ParsekSettings.cs),
  manual Gloops ghost-only, stop-on-switch (no Stop decision), commit via
  revert-merge / scene exit / abort, discard (career rollback), auto-merge,
  sub-2-point drop, switch-segment (Fly / Switch-To) + no-op auto-discard,
  scene-exit finalization, ballistic extrapolation, finalization cache.
- **D2 Sampling**: density presets, proximity cadence (BG), structural-event
  snapshots, threshold+debounce recording.
- **D3 Reference frames** (per TrackSection): absolute, surface/body-fixed,
  orbital checkpoint, relative-anchored non-loop (recorded anchor), relative
  loop (live PID), parent-anchored debris, boundary seam sections.
- **D4 Environment classification / optimizer**: Atmospheric / ExoPropulsive /
  ExoBallistic / SurfaceMobile / SurfaceStationary + hysteresis, env/body
  splits, surface graze suppression, cohesive cross-body coast, tail trim,
  seed-event split, SplitAtUT.
- **D5 Tree / chain topology**: single-node tree, undock split, staging debris
  (TTL, promotion), EVA branch, controlled-decoupled child, dock merge
  (same tree), cross-tree foreign dock, chain continuation across switch,
  crash coalescing, BG recording, BG on-rails (NO TrackSections - modeled).
- **D6 Playback / ghost engine**: basic playback, loop (period modes),
  self-overlap, overlap expiry / soft caps, zone transitions
  (2.3km/50km/120km), watch mode + retarget + explosion hold, spawn-at-end
  (PID dedup), ghost map presence (TS icons, orbit lines, targeting),
  non-orbital polyline, reentry FX, attitude preservation, SOI-crossing
  playback, time-jump, CommNet relay, ghost audio.
- **D7 Part events (28 types)**: decouple/stage/destroy, engine FX
  (legacy + EFFECTS + Waterfall fallback), RCS, chute 2-phase/cut, shroud,
  fairing, panels/antennas/radiators, gear, bays, lights, dock/undock,
  inventory place/remove, flag plant.
- **D8 Ledger / economy (9 modules + infra)**: Science, Funds, Reputation,
  Milestones, Kerbals, Facilities, Contracts, Strategies, Route; recalc
  engine, orchestrator, KspStatePatcher, tombstones, ERS/ELS routing,
  ground-truth harness (CareerSaveParser + LedgerGroundTruthDiff), action
  blocking, recalc-from-ut0, epoch isolation after revert.
- **D9 Rewind / re-fly**: Rewind-to-Launch, Fast-Forward, Rewind-to-
  Separation, re-fly gate (5 preconditions), Unfinished Flights / STASH,
  Seal / Stash / Fly, supersede relation, tombstones, merge journal (crash
  recovery), HEAD/TIP origin split, terminal-kind classify, load-time sweep,
  RP disk usage + reaper, revert-during-re-fly dialog, read-back guard,
  reconciliation bundle.
- **D10 Logistics / routes**: candidate detection, KSC origin (funds +
  recovery credit), docked-depot origin, dock / claw producers, delivery /
  pickup / mixed direction, resource cargo, inventory cargo (multi-module,
  stack, volume/mass admission), harvest provenance, multi-stop,
  multi-origin escrow, round-trip pair, inter-body Nth-window, dispatch
  cadence, hold reasons, destination-full gate, route map lines,
  route x rewind interaction.
- **D11 Missions / periodicity / re-aim**: default mission per tree, leg
  trim, whole-mission loop on span clock, self-overlap, clone, phase-lock
  (pad-align, Mun/Minmus window), zero-drift schedule, re-aim Lambert per
  window, loiter compression, arrival hold, eccentric/inclined targets,
  heliocentric-parking departure, station phase-lock, multi-moon config
  hold, land+dock dual constraint, partner journey, S4 arrival re-stitch,
  fail-closed-to-faithful.
- **D12 Crew / kerbals**: reservation + auto-hire, stand-ins, seat matching,
  rescue marker, dead-crew strip, tombstone + rep penalty, missed-EndUT
  auto-free, crew swap, hire/dismiss patches.
- **D13 Spawn safety**: proximity offset, bbox block, trajectory walkback,
  terrain correction, KSC exclusion, situation correction, surface orbit
  reseed, PID dedup, 3-cycle abandon, terminal-orbit safety, Real Spawn
  Control.
- **D14 Environment axes** (multipliers): body (Kerbin..Eeloo, Jool moons),
  atmosphere, situation, SOI count, warp rate (1x / phys / rails / high),
  scene (Flight / Map / TS / KSC / Editor), game mode (Career / Science /
  Sandbox), cold-load UT=0 hazard.
- **D15 UI surfaces**: OUT OF SCOPE for automation (no layer can observe
  IMGUI; manual checklists per test-coverage-matrix remain the owner).
  Exception, the one honest automated cell: TimelineBuilder is a pure
  headless projection of ERS/ELS (timeline design 3.4) - assert every ELS
  action maps to exactly one entry, UT-sorted, ineffective rows demoted.
- **D16 Storage / format**: sidecar set (.prec binary v3 / .craft / .pcrf /
  .pann annotations - stale-annotation-vs-prec consistency is an analyzer
  invariant, rendering design 17.3.1), RP quicksaves, Deflate snapshots,
  .txt mirrors, alias mode, .sfs scenario node, schema gate (format 1 /
  generation 4), path validation, safe-write, pre-Parsek backup.
- **D17 Mod compatibility**: Waterfall + SWE pristine fallback, ReStock/+,
  PersistentRotation, BetterTimeWarp, RemoteTech/CommNet, Making History.
  Runs ONLY on the modded-compat instance profile (plan section 10).
- **D18 Ghost chains / paradox prevention** (flight-recorder design 12-14;
  the mod's headline promise, previously uncovered): committed-interaction
  vessel claiming, ghost conversion of quicksave vessels after rewind,
  intermediate spawn suppression along a chain, chain-tip spawn preserving
  the ORIGINAL vessel PID, cross-tree chain linking, ghost extension past
  EndUT while blocked, background-event claims (12.9.6), chain terminated
  by destruction/recovery (12.9.1/12.9.8), chain state re-derived (never
  persisted - determinism across consecutive loads, 20.2), relative-state
  time jump observables (14.5-14.7: positions/velocities/attitudes
  unchanged, orbital epochs shifted by exactly the delta, earlier-tip
  auto-spawn, jump-then-rewind leaves no persistent state), rewind strip ->
  ReconcileSpawnStateAfterStrip -> re-spawn cycle (13.11), loop
  first-run-is-real (exactly one real vessel across N loop cycles and
  rewind re-crossings, 2.2 principle 9 / 12.7).

The existing 68 in-game test categories are the canary layer over these
dimensions; the full list is in the mining report and in
`Source/Parsek/InGameTests/`.

## 2. Atomic mission blocks (B1-B10)

Smallest flights that together touch the most dimensions. Each block lists
its fixture shortcut (synthetic recording or stock Scenario save).

| Block | Mission | Dimensions | Fixture shortcut |
|---|---|---|---|
| B1 | Pad hop: launch, hop, land 2km, revert-merge | D1,D2,D4(atmo),D6,D7(SRB+chute),D13 | Flea Flight synthetic #3 |
| B2 | Ascent to LKO, hold attitude, commit | D3(orbital),D6,D13,D14 | Orbit-1 synthetic #6 |
| B3 | EVA branch: pad craft, EVA, walk, board | D1(EVA),D5(EVA),D12 | PadWalk synthetic #1; stock "EVA in Kerbin Orbit" for orbital variant |
| B4 | Undock split in orbit, both children live | D5(undock,BG),D6(tree),D9(RP) | Undock tree synthetic #9 |
| B5 | Stage AB stack: B to orbit, A debris | D5(staging,TTL),D9(RP multi) | - |
| B6 | Rendezvous + dock + transfer + undock | D5(dock),D7(dock),D10(candidate),D8(manifest) | stock "Space Station 1" scenario save (target pre-exists) |
| B7 | Mun transfer, land, return | D3,D4(SOI),D14(Mun) | stock "Mun Orbit" + "Powered Landing" saves skip legs |
| B8 | Loop B7 tree as mission, phase-locked | D6(loop),D11(span,phase) | inject committed B7 tree |
| B9 | Crash A from B5, rewind, re-fly, merge | D9(full),D8(recalc) | inject tree with Crashed sibling + RP |
| B10 | Career passive safety: stock actions only, warp, scene change, cold load | D8(all),D14(career),D16 | fresh career save |

B10 requires almost no flying and covers the historically most destructive
regressions (R1/R2/R4-R7 below).

B8 additional assertion (loop first-run-is-real, D18): after 3 loop cycles
plus one rewind re-cross of the spawn window, exactly ONE real vessel with
the recording's spawn PID exists (save parse).

Investment note (from design-doc re-review): the test-coverage matrix
already rates recording/boundary detection Strong across all modes; the
destructive bug mass sits in career economy, save integrity, routes x
rewind, and map render. Marginal flying hours beyond B1-B7 go to B10, the
L-track, playback sessions, and the F-series - not to more recorder
breadth.

## 3. Scenario ladder (repo design-doc scenarios x community archetypes)

Tiers 0-5. Repo scenarios (S*) from design docs; community archetypes (A*)
from the mission-ladder survey with automation-readiness:
TRIVIAL = one MechJeb/kRPC.MechJeb call; SCRIPTED = public script exists;
HARD = hand-build; IMPRACTICAL = human needed.

### Tier 0 - pad / trivial (TRIVIAL)
- S0.1 pad hop + end-spawn ghost (B1); S0.2 pad-walk EVA ghost; S0.3
  suborbital arc + chute; S0.4 destroyed-near-KSC fallback sphere.
- A1 hover/hop, A2 suborbital (kRPC tutorial script + craft).

### Tier 1 - orbit, one vessel (TRIVIAL/SCRIPTED)
- S1.1 ascent to orbit + orbital-attitude spawn; S1.2 island cruise landed
  spawn; S1.3 close-spawn 250m offset; S1.4 Gloops airshow loop.
- S1.5 rewind loop (core loop, D18): fly B1, commit, warp past EndUT
  (vessel spawns), quicksave, rewind via seam, assert vessel stripped,
  ghost replays, vessel re-spawns at EndUT situation-corrected; crew
  re-reservation + resource reset against the ledger oracle.
- A3 LKO (MechJeb ascent), A5 exact-orbit satellite deploy (contract
  params), A4 orbital EVA (HARD - jetpack control).

### Tier 2 - multi-vessel split, one mission (SCRIPTED)
- S2.1 undock split two children; S2.2 mid-flight EVA branch; S2.3 staging
  with recoverable booster; S2.4 destruction-test tree (one child destroyed).
- A6 rendezvous (MechJeb), A7 docking (TRIVIAL small craft; HARD large),
  A14 rescue/tourist chain (ElWanderer kOS scripts).

### Tier 3 - loop / mission / single-body SOI (SCRIPTED)
- S3.1 Mun mission whole-tree loop phase-locked; S3.2 pad-aligned
  launch+orbit loop; S3.3 Kerbin-Duna re-aim loop (synodic); S3.4 drop-pod
  fork loop with camera handoff; S3.5 eccentric target re-aim (Eeloo/Moho).
  NOTE: S3.3/S3.5 (and R10-R12) target the re-aim subsystem which is
  actively in flux (launch-escape seam failed playtest, cross-SOI kink
  open, Lambert-reliability branch in progress); they enter the coverage
  ledger under the expected-fail/quarantined-by-known-bug state keyed to
  todo-doc bug IDs, so they cannot poison nightly triage.
- A8 Mun flyby free-return, A9 Mun landing, A10 Minmus landing,
  A16 Duna round trip.

### Tier 4 - rewind / re-fly / cross-tree / station (mixed)
- S4.1 re-fly crashed booster (supersede + tombstone); S4.2 re-fly stranded
  EVA kerbal; S4.3 4-probe constellation deploy + later flies; S4.4 station
  rendezvous phase-locked loop; S4.5 cross-tree partner-journey loop;
  S4.6 land+dock dual constraint.
- S4.7 chain rewind (D18, the paradox-prevention scenario): commit B6
  (docked to pre-existing station), rewind before its StartUT, assert
  (i) station despawned + ghosted (log contract + kRPC vessel list),
  (ii) docking physically impossible during ghost window (no dock events),
  (iii) at tip UT combined vessel spawns with the ORIGINAL station PID
  (save parse), (iv) with a second committed docking, intermediate spawn
  at link 1 suppressed and ghost mesh transitions (12.9.5).
- S4.8 time jump (D18): 80m rendezvous approach to a ghost (injected chain
  fixture), TimeJump via seam, assert relative position delta within
  tolerance, SMA/ecc/inc unchanged, MNA-at-epoch shifted exactly by delta,
  earlier tips auto-spawned (14.6), jump-then-rewind leaves no state.
- A13 station assembly (multi-launch dock), A15 asteroid claw (HARD),
  Apollo-style (compact 2-dock + Mun SOI - best mid-tier torture test).

### Tier 5 - career + routes + layered rewinds (the end goal)
- S5.1 KSC-station supply route full cycle; S5.2 hub-and-spoke chained
  routes; S5.3 inter-body Nth-window route; S5.4 multi-stop + round-trip
  pair; S5.5 Jool-5 multi-moon loop; S5.6 route x rewind interaction;
  S5.7 grand career oracle run (see L-track in the plan).
- A19 Jool-5 single leg (flagship many-SOI test), Kessler swarm (N
  satellites for simultaneous-ghost stress). A18 Eve ascent, A11 rover,
  A12 SSTO, A17 ISRU: HARD, defer; A20 grand tour: IMPRACTICAL, segment.

## 4. Regression replay list (R1-R26)

Historically bug-producing situations, highest value first. Full citations
in the mining report; bug sources are todo-and-known-bugs.md + v7 archive.

| # | Scenario | Systems |
|---|---|---|
| R1 | Pure-stock career play: science/funds must not drift (BUG-A/B) | D8,D14 |
| R2 | Cold-load established career: no economy wipe at UT=0 (BUG-F) | D8,D16 |
| R3 | Stock Revert must not delete unrelated live craft (BUG-H) | D9,D13 |
| R4 | First atmosphere crossing: no world-record recalc freeze | D8,D4 |
| R5 | Discard recording: contract/science/milestone earned live survives | D1,D8 |
| R6 | Scene change: no silent facility-upgrade refund | D8 |
| R7 | Stock contract re-fire: no N-fold reward bake | D8 |
| R8 | Loop Mun leg, warp through orbit-raise gap + SOI (warp-reseed-lag) | D6,D14 |
| R9 | Quickload mid-recording then commit (Limbo tree serialization) | D1,D16,D9 |
| R10 | Re-aimed heliocentric transfer renders in-plane | D11 |
| R11 | Re-aim transfer line reaches destination (no arc-clip truncation) | D11 |
| R12 | Span>synodic re-aim: correct transfer at EVERY relaunch | D11 |
| R13 | TS Fly loads right vessel with ghost trajectories | D6,D15 |
| R14 | Route ghost docks recorded station, not 20km copy; no dup ghost | D10,D6 |
| R15 | Route rewind: funds spent must re-deliver goods | D10,D9 |
| R16 | Route to full destination holds, no debit-then-drop | D10 |
| R17 | Multi-module inventory delivery fills beyond first container | D10 |
| R18 | EVA spawn walkback clears overlapping endpoint | D13,D1 |
| R19 | Dock/undock: no triple identical-UT structural snapshots | D5,D2 |
| R20 | Stand-in kerbal not duplicated across live vessels | D12 |
| R21 | Static GameEvent OnLoad NRE must not wipe recording index | D16 |
| R22 | OnSave must not hollow a save whose load faulted | D16 |
| R23 | Committed-restore-clone discard reverts cleanly | D1 |
| R24 | TS ghost icon must not circle underground on clipped orbit | D6 |
| R25 | Save-load-into-flight with BTW/Waterfall: no NRE flood | D14,D17 |
| R26 | Waterfall SWE !EFFECTS{} does not kill ghost plumes | D17,D7 |

The done/ archives hold 300+ resolved bugs; this list is the rotation seed,
extend as tiers come online.

R13 caveat: kRPC's `active_vessel` setter does NOT pass through the patched
stock handlers (MapContextMenuOptions.FocusObject.OnSelect /
SpaceTracking.FlyVessel), so R13 certifies the wrong code path unless the
command seam's SimulateStockSwitchClick command (arms the real
StockActionIntentMarker) is used. Do not claim switch-segment coverage
without it.

## 4b. Fault-injection family (F-series, never-corrupt contract)

Cheapest severe-bug coverage in the catalog: boot cycles only, no flying.
Sources: flight-recorder design 19.2-19.7, storage safe-write,
PreParsekBackup, merge journal.

| # | Fault | Assert |
|---|---|---|
| F1 | Truncated .prec | recording shown "damaged", no partial parse, zero loss elsewhere, log contract line |
| F2 | Deleted _vessel.craft | "missing data" state, tree intact |
| F3 | Wrong-generation sidecar | reject with reason generation-older/newer, rest of save loads |
| F4 | Cycle-injected tree topology | rejected, no chain-walker hang, no ghosting, no loss |
| F5 | Kill during safe-write | .tmp recovery on next load, no hollowed file |
| F6 | First cold-load backup | pre-Parsek sibling save exists and is pristine |
| F7 | Crash after merge-journal phase X (test command CrashAfterJournalPhase) | RunFinisher rolls back or completes per phase, for every X |
| F8 | Stale .pann vs regenerated .prec | annotation consistency invariant flags it |

## 5. Fixture sources

### 5.1 Stock Scenario saves (ship with KSP, version-matched, zero flying)
Space Station 1 (docking target pre-built), Mun Orbit, Mun Rover, Powered
Landing, Impending Impact, ARM_Asteroid1/2 (claw), Refuel at Minmus,
Prospecting Eeloo, Exploring Gilly, Jool Aerobrake, EVA in Kerbin Orbit,
EVA on Duna, Dynawing Re-entry / Final Approach, Transmissions.
Enumerate live from `KSP/Scenarios/*.sfs`. Highest value: Space Station 1,
Mun Orbit, Powered Landing, ARM_Asteroid1, Dynawing Final Approach - each
isolates one recorder-critical maneuver with no ramp-up flight.

### 5.2 Craft files (pin exact files in repo)
- kRPC tutorial craft (co-designed with their scripts): suborbital +
  LaunchIntoOrbit.craft - default for Tier 0/1.
- Bundled stock craft: Kerbal X (LKO/Mun), Kerbal 1/2 (suborbital),
  Dumpling (satellite), Aeris 4A (SSTO, defer), Acapello + Dynawing
  (Making History; Apollo-style and shuttle profiles).
- KerbalX only as last resort; verify stock-parts-only.

### 5.3 Synthetic recordings + injected saves
The existing `docs/dev/synthetic-recordings.md` library (pad walk, Flea
Flight, suborbital, destroyed-vessel, Orbit-1, close-spawn, island cruise,
undock trees #9-11) plus `InjectAllRecordings` remain the zero-flight
fixture path for playback-facing tests and D9 base states (inject a tree
with a Crashed sibling + RP for re-fly scenarios).

### 5.4 Autopilot scripts (external, per archetype)
- kRPC.MechJeb: ascent, maneuver, rendezvous, docking, landing (one call
  each) - primary for Tiers 0-3.
- Art Whaley kRPC demos (rendezvous.py, docking), kRPC official tutorials.
- kOS: RAMP (xeger/kos-ramp, near-turnkey launch/node/rendezvous/dock/land),
  ElWanderer kOS_scripts (satellite/tourist/rescue boot scripts), KSLib
  (MIT building blocks).

### 5.5 Known autopilot failure modes (build detection into the harness)
- MechJeb ascent: warp stutter during coast (noisy velocity), weak on
  low-TWR/wobbly stacks.
- MechJeb landing: arms only below altitude threshold, can silently abort
  or brake late (overshoot); worse with atmosphere.
- MechJeb docking: reliable only for small RCS-balanced craft - keep test
  craft small or budget retries.
- Timewarp entry can shift orbit params in edge cases; guard recorder
  assertions around warp transitions.

## 6. What simple missions CANNOT reach (needs multi-session orchestration)

- Re-fly merge / supersede / tombstone / journal crash recovery (2+ sessions
  + quicksave reload, or injected base state).
- Cross-tree foreign dock / partner journey (two independent launches).
- Supply routes end-to-end (committed dock run first, then N loop cycles
  across warp and save/load to observe dispatch, escrow, recovery credit).
- Multi-stop / multi-origin / round-trip / inter-body routes (multi-window,
  game-days of UT).
- Re-aim per-window correctness over many synodic windows.
- Multi-moon config hold (needs a Jool-5-shaped tree fixture).
- Loop self-overlap caps (several relaunch cycles under warp).
- Ledger recalc-from-UT=0 with many actions at distinct UTs.
- Quickload/rewind against in-progress recording (save-state manipulation).
- Warp-reseed-lag (scripted high-warp crossing of specific gaps).
- Cold-load / OnSave fault injection (process restarts).

Harness implication: three fixture tiers - (a) autopilot-flown atomic
blocks, (b) injected synthetic base states, (c) multi-session orchestration
scripts (fly-commit-restart-observe).
