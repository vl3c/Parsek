# Coverage extension plan: quickload, mining, resource conservation, gameplay x timeline ops (2026-10-06)

Status: RESEARCH + PLAN. Four read-only audits of `main` at `eafdd64e` (quicksave / quickload;
ore mining and ISRU; resource conservation across timeline operations; a gameplay-feature x
timeline-operation matrix over all test layers), each suspected defect then put through an
adversarial code-read pass that tried to refute it. Nothing was flown or reproduced; every
CONFIRMED below means "traced end to end, no guard found". Companion docs:
`integration-coverage-gaps-2026-10-06.md` (supply routes x rewind x ledger, the IR / IC lanes)
and `feature-op-coverage-matrix-2026-10-06.md` (the full matrix this plan summarizes).

## 1. The short version

- **Nothing tests going BACK in time with F9.** All 17 green in-place reload lanes reload the
  instant they saved; the one backward quickload is an in-game cell that goes 2 s back inside one
  recording (H65). There is no stock-Revert seam verb, so no lane drives a revert either. And the
  code read found that an in-session load (F9, stock Revert, Discard Re-fly) keeps recordings and
  the ledger in memory while rebuilding the Re-Fly bookkeeping from the save: the two halves of
  the timeline can disagree after any back-in-time load (section 3.1).
- **Mining and ISRU are untested end to end.** No fixture contains a drill, an ISRU, an ore tank
  or Ore; converters are exercised only through a FuelCell. The good news from the code read:
  after a rewind a ghost miner produces nothing, and the vessel spawned at its recording's end
  carries the recorded ore exactly once (ore amount and converter timestamp come from one
  `BackupVessel` call). The defect found: the two Parsek time-jump paths treat running
  converters differently (section 3.2).
- **Recorded resource transfers do not multiply over repeated rewinds** (the quicksave and the
  snapshot never change, so N replays give the same result), but four shapes lose or duplicate
  resources: route cargo baked into a chain-tip snapshot, chain tips spawned at the KSC or in the
  TS, dock dominance picking the wrong chain, and EVA inventory moved across vessels
  (section 3.3).
- **Coverage is deep on single features and on the older pairs** (Re-Fly x crew / staging /
  docking / EVA / map, Rewind-to-Launch x ghosts / spawn / EVA, loops x warp / TS), and **thin
  wherever the timeline is rewound in a career**: every career rewind check is log-token level
  because the ledger oracle refuses rewind lanes, career lanes check no playback at all, and
  contracts x Re-Fly never actually executes on any host (section 2).

## 2. Where the gaps are (from the matrix)

Full per-cell evidence: `feature-op-coverage-matrix-2026-10-06.md`. Green lanes per operation:
record+commit about 119, merge / discard 7, Re-Fly 33, Rewind-to-Launch 25, in-place reload 17
(back-in-time 0), stock revert 0, `WarpToUT` 13, `TimeJump` 91, scene change 51, career 51,
repeated rewind 8.

Operations with no live coverage at all, or nearly none:

| Operation | Not covered live |
|---|---|
| Stock revert | everything (only in-game cells H64 RevertFlow, LT-1 RevertVesselStrip, R7a ReFlyRevertDialog) |
| Back-in-time F9 | everything |
| Merge vs discard | crew, funds, spawn, loops, docking, EVA |
| Re-Fly | science (RB-1 / RB-2 expected-fail), contracts, strategies, tech, facilities, loops, CommNet, Real Spawn Control |
| Rewind-to-Launch | contracts, strategies, tech, facilities, docking (no docking fixture has a launch quicksave), orbital endings, CommNet, Real Spawn Control |
| Real warp | science, contracts, strategies, funds, EVA, KSC / TS |
| Repeated rewind | spawn, loops, crew, every economy feature |
| Career | ghosts, loops, map, CommNet, Real Spawn Control, watch, docking, staging, orbital endings, spawn |

Structural limits that every new lane runs into: one `RunTests` step per lane; `WarpToUT` works
in FLIGHT only; `RunInvariantReport` and `CrashAfterJournalPhase` are reserved verbs, not
implemented; four in-game categories are never run by any spec (DeployedScienceGhost,
ChainTipBlockedGhost, GhostReapplyFrame, GuiMock); `ContractTombstonesAcrossSupersede` skips on
every host it runs on.

Thin tests behind large code: the scenario / scene-control layer (`ParsekScenario` and the
OnLoad branch matrix, about 51k lines behind 220 named xUnit facts; `ParsekKSC` and
`ParsekTrackingStation` have no test file of their own); `GhostCommNetManager` (no xUnit file);
watch mode (5.9k lines, one test file, two in-game tests); and no crew, vessel-identity or
resource-conservation rule among the analyzer's INV1-INV12.

## 3. Defects (adversarially verified)

### 3.1 Quickload family

| Id | Verdict | What happens | Cheapest red test |
|---|---|---|---|
| QL-D1 | CONFIRMED | Every load rebuilds rewind points, supersede rows, retirements, tombstones, the Re-Fly marker and the merge journal from the save (`LoadRewindStagingState`, `ParsekScenario.cs:2783`, called at `:3609`); the carry that protects them runs only on a plain rewind (`:3617`, `:3624`). Recordings and the ledger stay in memory. F9 to a save from before a Re-Fly merge leaves the original and the re-flown flight both visible and the tombstoned crew deaths and reputation penalties counting again. Discard Re-fly with the editor target hits the same path | `RewindStagedListsCarryTests`-style xUnit: commit A and A', keep the supersede row only in memory, non-rewind load of a node without it, assert A invisible |
| QL-D2 | CONFIRMED | Tagged game-state events from the abandoned future are never purged on F9 (the only after-UT purge has one caller, the Re-Fly discard); once the resumed flight runs past them they are booked at commit. A contract completion or milestone reward from the timeline F9 abandoned is credited | `QuickloadResumeTests`: tagged ContractCompleted at 300, trim to 200, commit 100-350, assert no ContractComplete row |
| QL-D3 | CONFIRMED (when the detach runs) | A committed tree detached on F9 keeps its ledger rows; recovery funds or contract rewards from the abandoned future survive a different outcome | extend `TryRestoreActiveTreeNode_SkipsCommittedTreeStashesActiveTree` with a funds row at 300 and a loaded UT of 200 |
| QL-D4 | PARTLY CONFIRMED | Future terminal state and crew end states of OTHER tree members leak into the resumed tree through the splice / refresh and the stale-epoch path; the trim never clears them. A booster that survives the replayed flight still ghosts as exploding and is no longer recorded. (The active recording itself is guarded by `ClearStaleDestroyedTerminalForResume`.) | `QuickloadResumeTests`: splice + trim on a Destroyed / Dead booster at 300, assert terminal cleared and back in the background map |
| QL-D5 | PARTLY CONFIRMED (narrow) | An origin rewind point created inside an earlier, merged Re-Fly session is purged with its quicksave file when Discard Re-fly (editor target) loads that quicksave. Deciding fact: whether the editor scene re-reads persistent.sfs | xUnit over `LoadTimeSweep.Run` with that point and the merged session's marker |
| QL-R1 | RULED 2026-10-06: record again | Discard deletes the recording files, so a later F9 back into that flight resumes it unrecorded. Owner ruling: the resumed flight records again (QUICKLOAD-INTO-DISCARDED-FLIGHT-RECORDS-AGAIN) | lane QL-6 |
| QL-R2 | DESIGNED, doc stale | KSC purchases, upgrades and hires made after the quicksave stay as committed future (owner ruling D2: committed is permanent, rewind frees nothing); game-actions design 15.5 still says a load prunes future spendings | doc fix |
| - | REFUTED / RULED | F9 at the KSC or TS is not misclassified (both consumers also require FLIGHT->FLIGHT). The TS's exclusion from the current-UT cutoff was pinned but unexplained, and a cold TS load prunes future spendings while an in-session one does not; owner ruling 2026-10-06: align the TS with FLIGHT / SPACECENTER (TRACKING-STATION-LEDGER-CUTOFF-ALIGN) | update the pin |

The route member of this family is already filed: ROUTE-STATE-NOT-RECONCILED-ON-F9-REVERT-DISCARD.

### 3.2 Mining and ISRU

| Id | Verdict | What happens | Cheapest red test |
|---|---|---|---|
| MINE-D1 | CONFIRMED | `TimeJumpManager.ExecuteJump` (Real Spawn Control Warp to Spawn and Warp to Departure, the `TimeJump` seam verb) never resets converter `lastUpdateTime`, while `ExecuteForwardJump` does for loaded vessels. A loaded miner or ISRU gains (or burns) resources over one jump kind and not over the other, contradicting logistics design's "a jump is not production time"; a burst after `ExecuteJump` can also land inside an open harvest window and be credited as harvested | in-game on the H38 / `logi-cargo-pad` host (its FuelCell is a converter): same delta through both jumps, compare consumption; a pure jump-policy decision pinned in xUnit |
| MINE-D2 | VERIFIED (doc) | Logistics design :1658 and :1742 overstate the code (jump policy above; origin tanks do refill between cycles when the base loads, through stock catch-up) | doc fix with the D1 decision |
| MINE-D3 | GAP | `FixResourceConverterTimestamps` has no test at any layer; catch-up on a Parsek-spawned miner is unmeasured (could be capped by power or ground contact, or large) | in-game `SpawnedConverterCatchUp_StaleTimestamp_Measured` |
| MINE-Q1 | RULED 2026-10-06: cap it | A harvest-origin route recorded right after a catch-up burst credits the whole burst every cycle; owner ruling: bound it by drill rate x duration (HARVEST-ROUTE-PLAUSIBILITY-CAP) | xUnit on `RouteHarvestCapture` |
| MINE-Q2 | RULED 2026-10-06: ledger them | Resource-scan unlocks are neither recorded nor ledgered; after a Rewind-to-Launch a committed future scan is not re-applied; owner ruling: record and ledger them (RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED) | xUnit + mining lane |

Answer to "how does ore mining work after a rewind when the miner is a ghost": between the rewind
point and the recording's end the ghost plays the drill / converter loop and nothing produces ore;
at the end the real vessel spawns with the recorded end ore, drills still active, `lastUpdateTime`
at about the end UT; the next time it loads, stock catch-up continues production from there,
which is what the original timeline would have done too. Recorded ore arrives exactly once.

### 3.3 Resource conservation

Parsek changes a vessel's resources only through the route writers; everything else moves
resources by replacing one source wholesale with another (the stock save or a rewind quicksave,
a recording's vessel snapshot copied verbatim at every spawn including chain-tip respawns, live
route writes). Worked example from the committed `bdock-recorded` fixture: the station holds LF
720 / MP 230 before the dock; its chain tip (`37d0dc07`, same part uids) holds LF 704.23 / MP 230;
after a Re-Fly at either rewind point and a replay forward the station is despawned from the
rewind UT and respawns from the tip snapshot at 8951.548 with LF 704.23, the same on every
replay.

| Id | Verdict | What happens | Cheapest red test |
|---|---|---|---|
| RC-D1 | CONFIRMED (narrow trigger) | A route already delivering into a station BEFORE a later committed mission docks to it: the station half's snapshot (taken at commit) holds those deliveries; after a rewind to before the dock, the route rows after the cutoff are retired and every crossing while the station is despawned is BLOCKED with no catch-up. The station returns holding cargo nobody paid for again; the origin keeps its cargo and KSC funds are refunded | a `RouteLoopDeliveryFireTests` cell: endpoint unresolved until X then resolved, no cycle before X delivered; full effect needs a new fixture (lane RC-2) |
| RC-D2 | CONFIRMED | Only the flight scene ghosts a claimed vessel. After a Rewind-to-Launch (lands at the Space Center), a chain tip whose SpawnUT passes at the KSC (surface tip, `ParsekKSC.cs:1999`) or in the TS (`GhostMapPresence.cs:7543-7569`) ADOPTS the live pre-transfer vessel and marks itself spawned; the launch-guid adoption guard passes because the station half keeps the station's Vessel.id. The recorded transfer silently vanishes; the station keeps its pre-transfer tanks while the transport half spawns post-transfer | a TS / KSC spawn cell: tip's claimed pid live and pre-claim, assert no adoption |
| RC-D3 | CONFIRMED | `GhostChainWalker.WalkToLeaf` follows the same-pid child, i.e. the DOMINANT half of the dock merge; KSP gives the departing half a new pid on undock. When the transport is dominant (higher vessel type, or heavier) and later ends Destroyed or Recovered, the chain counts as terminated, the station is never ghosted, and the station half spawns as a second station (same part flightIDs, resources, crew seats) | a pure `GhostChainWalkerTests` cell with a transport-pid merged child, a Destroyed transport half and a new-pid station half |
| RC-D4 | CONFIRMED | No inventory or EVA-construction event is recorded, so moving a part between a tree vessel and a FOREIGN vessel creates no claim: after a rewind the part exists twice or not at all | lane RC-6 with `EvaGroundScience action=take` against a foreign container (new fixture) |
| RC-D5 | MECHANISM CONFIRMED; RULED 2026-10-06: keep resource-changing tails | Tail trim moves EndUT / SpawnUT earlier but keeps the commit-time snapshot, so resources from the trimmed tail arrive early; with RC-D1, crossings between the trimmed SpawnUT and commit are delivered twice | `RecordingOptimizer` tail-trim cell |
| RC-D6 | CONFIRMED (fixture) | `bdock-recorded` recorded its transfer INVERTED: the spec asks 40 LF transport -> station and 15 MP station -> transport; the window shows the transport gaining 15.77 LF and the station losing it (a full inversion clamped by tank room), part-set labels correct. Likely cause: the runner's side partition (its own `TODO(flight-14)`). No player impact; every lane on this fixture inherits a pickup-shaped transfer, and the `Route window delta:` gate checks only presence | offline cell over the committed fixture asserting the endpoint's LF delta is positive |
| RC-D7 | CONFIRMED (UI) | The loop-route status text says "Use Re-scan to find it" for a lost endpoint, but Re-scan clears a field the loop path never reads; only Pause then Activate recovers the route | a presentation / window test |
| - | UNCERTAIN | A retired Re-Fly fork that docked to a pre-existing vessel could still claim it (the walker applies no retirement filter, the tip spawn reads the effective set), despawning a real vessel that then never returns; reachability unproven | a `GhostChainWalkerTests` cell pinning the behaviour |

### 3.4 Not defects, but recorded

- Repeated rewinds to the same rewind point give identical resources (the quicksave and the
  snapshot are immutable); recorded transfers do not multiply.
- A vessel that later docked is not re-flyable (`UnfinishedFlightClassifier` downstream branch
  point), so a Re-Fly cannot re-run a recorded transfer.
- `EndResources` is not refreshed when the commit re-snapshots a vessel, and background leaves
  have none: any resource oracle must read the snapshot sidecars (format `PSN0` + 25-byte header +
  raw deflate), not the recording metadata.

### 3.5 Gameplay cases nobody tests or lists (candidates, not verified)

A check of 22 candidate cases against every spec, test and doc found these neither tested nor
listed anywhere (ranked likelihood x damage, each 1-5). They are CANDIDATES: none was traced end to
end. Already covered or already listed, for the record: reserved-kerbal EVA (KB-5 live), ghosts as
rendezvous targets (H28, by design not dockable), two vessels in range with switching (S0.8, CI-1,
GS-3 ...), claw capture (H41 / H42), Making History launch sites (MC-4), overheat / structural
failure with Re-Fly (RF-9, CL-3), KSC building collapse (todo), crash mid-merge (ST-4 and the
in-game MergeInterruptionRecovery), revert to VAB (GP-2), fairings / clamps / debris with fuel
(GS-6, B18 / B19, GS-10 / GS-11), orbital EVA with Re-Fly (EVA-2, RF-20).

| Id | Case | Rank |
|---|---|---|
| GC-1 | Two different saves loaded in one KSP session: static Parsek state leaking between careers (`ParsekScenario.DetectSaveFolderChange` resets some of it; nothing exercises it) | 4x4 |
| GC-2 | Recovering or terminating, from the Tracking Station or the KSC, a vessel that a COMMITTED FUTURE mission docks to (the Ghost Chain Rule only ghosts in FLIGHT; `OnVesselTerminated` / `OnVesselRecovered` have no claim check): the chain tip later brings it back - a duplicate or recovery funds paid twice | 3x5 |
| GC-3 | Loading a save whose recordings are an older schema generation: they are skipped with only a log warning (`RecordingTree` skip), no player notice, and the first save may make the loss permanent | 3x4 |
| GC-4 | A contract parameter or milestone completed by a vessel Parsek spawned at a recording's end, or by a ghost map ProtoVessel (no patch guards them; L5 covers live vessels only) | 3x4 |
| GC-5 | A floating EVA kerbal as a recording's terminal spawn after a rewind or warp (H20 covers a landed kerbal only) | 3x3 |
| GC-6 | A held (reserved) kerbal dying aboard a live vessel that is not his reserved flight | 2x4 |
| GC-7 | Renaming a vessel mid-mission (no recorder hook): ghost label vs spawned name; a rewind across a recorded rename; a dock merge keeping the dominant vessel's name | 4x2 |
| GC-8 | The Mobile Processing Lab (data processing over time) across a rewind and a spawn | 3x2 |
| GC-9 | A runway landing and a spawn on the runway | 3x2 |
| GC-10 | Stock asteroid / comet lifetime: an untracked SpaceObject claimed by a committed claw tree expiring or being replaced after a rewind | 2x3 |
| GC-11 | External command seats: a kerbal taking a rover seat may be recorded as a dock merge rather than a board (unverified whether stock fires the couple event) | 2x3 |
| GC-12 | Tourists in a spawn-at-end or chain-tip snapshot after a tourism contract removed them | 2x3 |
| GC-13 | The stock Alarm Clock: alarms bound to vessels that a Re-Fly supersedes or the chain rule despawns, and warp-to-alarm across spawns | 2x2 |
| GC-14 | CommNet 'require signal for control' with a ghost relay that drops out (loop end, Re-Fly suppression, chain-spawn handoff); the settings research row 'ghosts are CommNet-inert' is stale since ghost relays shipped | 2x3 |
| GC-15 | A restored backup or a copied save folder: an older persistent.sfs against newer sidecars, or two saves sharing recording ids | 2x3 |
| GC-16 | Action groups / abort sequences on ghosts (outcomes are recorded as part events; the GS-6 sweep does not cover action-group-bound families); engine plates | 3x1 |

## 4. Strategy: what goes in which layer

| Layer | What belongs there |
|---|---|
| xUnit | the pure decisions (load-path x reconciler wiring, jump policy, prune / purge decisions, chain-walker shapes) and SEQUENCE properties: seeded fuzzers in the `LedgerStateFuzzerTests` mould that apply random operation sequences and assert invariants |
| In-game tests | anything that needs live stock singletons (converter catch-up, chain-tip resource fidelity, stock UI), plus read-only invariant categories that hold in ANY state so many lanes can run them |
| Offline analyzer / oracles | the biggest multiplier: a new rule runs over the produced save of every green lane at once |
| Harness lanes | two-feature lanes, one new axis at a time |
| Campaign / fuzzer lanes | long mixed sequences on rich fixtures, invariants after every step |

### 4.1 Capabilities that multiply coverage

Verbs:
- **`Quickload`** (or `LoadGame allowLiveRecorder=quickload`): F9 while recording; LoadGame refuses
  a live recorder today except for Re-Fly.
- **`Revert target=launch|vab`**: stock revert; none exists.
- **`ReFlyRevert choice=... target=launch|prelaunch`**: the Esc > Revert dialog during a Re-Fly.
- **`RunInvariantReport`**: implement the reserved verb as a per-step, non-batch read, which lifts
  the one-`RunTests`-per-lane limit for invariant checks.
- **`ReadVesselResources pid=|name= [expect=...]`**: per-vessel resource totals and a part-uid
  digest, in FLIGHT, KSC and TS.
- **Scene-agnostic warp**: `WarpToUT` at the KSC and in the TS.
- **Mission action to deploy drills and start converters** (`set_converters` only sets `.active`;
  `set_deployables` skips harvesters; kRPC 0.5.4 exposes both).

Oracles and analyzer rules:
- **Rewind-aware ledger oracle (L4)**: pools = seed + rows at or before the cutoff, tombstones
  applied; route action types (see HARNESS-LEDGER-ORACLE-ROUTES-AND-REWIND).
- **Resource oracle** (`harness/lib/resourceq.py`, an `[expectations.world.vessels]` block): V1 no
  part uid on two live vessels; V2 a chain-tip vessel equals its snapshot plus the route rows
  addressed to it after the spawn; V3 dock-window conservation with a declared direction; V4
  per-vessel windows.
- **Analyzer INV13 crew conservation and INV14 vessel identity** (no duplicate or superseded
  spawn), baselined over every fixture first, then gated.

Fixtures:
- `minmus-miner-landed` (drill, MiniISRU, radial ore tank, engineer; reuses B14's landing; part
  definitions harvested from a live VAB session).
- A career save with a rewind point and a launch quicksave; a crewed Mun landing with a rewind
  point; a docking fixture with a launch quicksave; the science-bg-pad builder.
- A Probe-typed station docked by a heavier transport that later ends Destroyed (dominance
  shape); EVA cargo moved from a foreign container.
- `bdock-recorded` re-harvested with its transfer in the declared direction (see 3.3).

### 4.2 Generic test machinery

- **Load-path x reconciler wiring gate (xUnit)**: a table of load paths (cold, stock revert, F9 in
  FLIGHT / KSC / TS, scene change, Rewind-to-Launch, Re-Fly, Discard Re-fly launch / prelaunch,
  crash reconcile) against the state each must reconcile (ledger cutoff, events, Re-Fly lists,
  route and loop cursors, crew, spawn state), asserted from the code so a new load path or a new
  state category cannot be added without a decision. QL-D1..D3 and the route reconcile defect are
  all this class.
- **Headless timeline fuzzers**: extend `LedgerStateFuzzerTests` / `EffectiveStateGraphFuzzerTests`
  with commit, supersede, cutoff, F9-backward, revert-prune and discard; add
  `RouteTimelineConservationFuzzerTests` (fake runtime with in-memory tanks: Tick, WarpJump,
  Rewind, F9, HideEndpoint) asserting debits + funds = credits, one row per (route, cycle, stop),
  rewind-and-re-tick equals never-rewinding, every tank change has exactly one row.
- **Seeded timeline-op fuzzer lane**: a generator writes a declarative step list from an
  operation alphabet (save, load back in time, revert, TimeJump, WarpToUT, Re-Fly,
  Rewind-to-Launch, merge / discard, scene change, RealSpawn, Recover, KscAction, EVA) obeying an
  OnFlightReady guard grammar, runs `RunInvariantReport` after each step and the analyzer,
  saveParse, the resource oracle and the L4 oracle at the end; each run prints its seed and step
  index so a failing prefix replays exactly. Nightly, one seed per night, on the rich fixtures.
- **Campaign lanes**: one career and one sandbox "everything" save, 30-60 mixed steps each; the
  integration doc's IC-3 is the route slice.

## 5. The register (lane and test ids)

Detailed rows, hosts and dependencies are registered in `docs/dev/autotest-roadmap.md` ("The
timeline-operation coverage program"). Summary:

- **QL-1..QL-6** quickload lanes: career rows across a back-in-time F9 at the KSC (QL-1) and in
  flight (QL-2); Re-Fly bookkeeping across F9 (QL-3); future crash state after F9 (QL-4); Discard
  Re-fly with a real load (QL-5); Discard then F9 (QL-6, after the ruling).
- **MINE-0..MINE-5** mining lanes: converter events on the drill / ISRU showcases (MINE-0); the
  Minmus miner fixture and its record / mine / ISRU / commit / Rewind-to-Launch / spawn lane
  (MINE-1, MINE-2); the jump-policy lane (MINE-3); a mining-base docked-origin route across a
  rewind (MINE-4); a live-drill harvest-origin route (MINE-5); plus GS-6 revision 3 (an ISRU on
  the sweep craft).
- **RC-1..RC-6** resource-conservation lanes: three rewind / replay cycles on `bdock-recorded`
  reading the station each time (RC-1); route into a station a committed mission later docks
  (RC-2, expected-fail on RC-D1); chain tip crossed at the KSC (RC-3, expected-fail on RC-D2);
  warp across a spawn and three crossings (RC-4); dominant-transport dock (RC-5); foreign EVA
  cargo (RC-6).
- **GP-1..GP-20** gameplay stories (matrix section 3), ranked; the first five: career F5 in orbit,
  transmit, complete a contract, crash, F9, redo, with LedgerGroundTruth after the F9 (GP-1);
  Revert to Launch in a career with committed history (GP-2); Re-Fly a booster in a career where
  the superseded flight completed a contract and recovered science (GP-3); Rewind-to-Launch after
  KSC actions, then the stock screens (GP-4); crewed spawn-at-end in a career after warp (GP-5).
- **GC-1..GC-16** the uncovered gameplay cases of section 3.5, each to be traced, then pinned or
  filed.
- **FZ-1** the seeded timeline-op fuzzer lane; **FZ-H** the headless fuzzers.

## 6. Order of work

1. Red xUnit tests for every CONFIRMED defect above, each landed with its fix (or with the
   ruling that makes it intended); the load-path x reconciler wiring gate.
2. The headless timeline fuzzers.
3. Verbs: `Quickload`, `Revert`, `ReadVesselResources`, `RunInvariantReport`, scene-agnostic warp,
   the drill / converter mission action.
4. The resource oracle and analyzer INV13 / INV14, baselined over every fixture, then gated.
5. Fixtures: the Minmus miner, the career-with-rewind-handles saves, the dominance and foreign-cargo
   shapes, the re-harvested bdock.
6. Lanes: QL-1..QL-4 and GP-1..GP-5 first (back-in-time F9 and revert are the largest blind
   spot), then MINE-0..MINE-3, RC-1..RC-4, then the rest.
7. The L4 ledger oracle, so those lanes gate on pools rather than log tokens.
8. FZ-1 nightly, then the campaign lanes.

## 7. Bookkeeping

Filed in `docs/dev/todo-and-known-bugs.md`: the program pointer TIMELINE-OP-COVERAGE-PROGRAM and SCRIPTED-CAMPAIGN-TO-THE-MUN; the
quickload defects (QUICKLOAD-REFLY-LISTS-REVERT-WHILE-RECORDINGS-STAY,
QUICKLOAD-ABANDONED-FUTURE-EVENTS-BOOKED-AT-COMMIT, QUICKLOAD-DETACHED-TREE-KEEPS-LEDGER-ROWS,
QUICKLOAD-FUTURE-TERMINAL-LEAKS-INTO-RESUMED-TREE, DISCARD-REFLY-PRELAUNCH-PURGES-NESTED-ORIGIN-RP);
TIMEJUMP-CONVERTER-POLICY-DIFFERS-BY-JUMP-KIND and MINING-ISRU-UNTESTED-END-TO-END; the
resource defects (CHAIN-TIP-SNAPSHOT-CARRIES-UNPAID-ROUTE-CARGO,
CHAIN-TIP-ADOPTS-STALE-VESSEL-OUTSIDE-FLIGHT, CHAIN-WALK-FOLLOWS-DOMINANT-DOCK-PARTNER,
EVA-INVENTORY-MOVE-TO-FOREIGN-VESSEL-UNCLAIMED, TAIL-TRIM-KEEPS-COMMIT-SNAPSHOT,
BDOCK-FIXTURE-TRANSFER-INVERTED, LOOP-ROUTE-RESCAN-DOES-NOTHING); the capabilities
(HARNESS-VERBS-FOR-TIMELINE-OPS, HARNESS-RESOURCE-ORACLE-AND-INVARIANT-RULES,
HARNESS-TIMELINE-FUZZERS); and RULINGS-NEEDED-TIMELINE-OPS-2026-10-06, all five ruled in the owner
interview of 2026-10-06 with a work item each (QUICKLOAD-INTO-DISCARDED-FLIGHT-RECORDS-AGAIN,
HARVEST-ROUTE-PLAUSIBILITY-CAP, RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED,
TRACKING-STATION-LEDGER-CUTOFF-ALIGN; tail trim keeps resource-changing tails). ROUTE-INTERACTION-SEAMS-TO-VERIFY
items 1 and 9 are ticked (verified as two of the defects above). The register is in
`docs/dev/autotest-roadmap.md` ("The timeline-operation coverage program"). The game-actions
design's 15.5 now records how a load actually treats future ledger rows (QL-R2).
