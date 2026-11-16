# Race test and harness machinery - implementation plan

Status: PLAN (2026-11-14), not started. Design sections covered: 11 (in full), the gates of
6.4, 10 (log tags as test anchors), 15.8, 15.10 (harness-made opponents, the seed only),
15.14. Phases: R0b (the day-one tasks), then alongside R1 to R6; one optional task (T-34)
on the parallel runner, which is on main.

Reworked 2026-11-15 for the ground-up milestones (README section "Milestones", task-index
section F): the owner asked to build from the ground up and test in parallel, to reuse the
scripted career campaign as player A and a twin of it as player B, and ruled that games the
test tools create score as labelled test games (design 7.12). T-37 to T-59 are added; T-21,
T-24, T-28, T-29, T-31, T-33 and T-34 are split, moved or made smaller.

## 1. Goal and scope

Build the shared test machinery that lets one developer on one PC prove the space race, and
the tests that no single feature slice owns. Concretely:

- A headless two-machine simulator: a fake synced store with fake clocks, bound to the 15.3
  ports through R3E-04's `SyncPorts`, so arrival order, an offline folder, partial sync,
  conflict copies and placeholder files are unit tests, not play tests. R3E-19's loopback
  tests run on it too (OV-19).
- The one generator for synthetic shared folders (`RaceFolderBuilder`, OV-17) next to
  `RecordingBuilder`, `VesselSnapshotBuilder` and `ScenarioWriter`, and committed
  shared-folder fixtures written by it.
- The harness side: shared-folder staging in `run.py`, the seam verb actions and tables R1
  does not add (R1-08, R1-10 and R1-11 land the first actions, OV-01), a pure race evaluator
  (`harness/lib/raceparse.py`) with a gated leak scan, a save-equivalence diff, and the RACE
  lane family with tiers.
- The cross-cutting headless tests: the 15.8 single-player inertness test, "same files, same
  board", and the changed-file two-machine scenario. The 6.4 byte-identity isolation test is
  R3W-29's (OV-20).
- No grep-audit gate of its own: the 6.4 and 15.4 race gates are R1-06's (on the shared
  scanner, OV-10, OV-11, OV-12) and R3W-27's (the rival-store gate, OV-13).
- The hosting lanes for the race in-game test categories, and a context helper those tests
  share.

### How a race is tested without a second human or PC

| Way | What it proves | What it cannot prove |
|---|---|---|
| A. Headless simulator over the ports (T-02, T-18) | Every rule of sync, import and the board under any arrival order, delay, outage, partial file, conflict copy or edit; two or more machines; seeded and repeatable | Anything live: scenes, ghosts, stock state, real file systems |
| B. Generator-built rival folders, committed as fixtures and staged by `run.py` (T-16, T-17, T-03) | Import, the rival store, rival ghosts, flags, map objects and the board in the real game against known rival data, including tampered, incomplete, newer-format and conflict-copy cases | That a real second build exports what the generator writes (the codec round trip covers the bytes, not the exporter) |
| C. Two race saves in one install, played in turn (the SWS-1 / `NewGame as=` precedent) (`RaceSetup` from R1-10 / R1-11, RACE-1, RACE-6; the two scripted players, section 4b) | The real exporter feeding the real importer through a real folder; join, slots and looks; a quickload that drops a shared flight; in a test game (7.12, R1-23) holder changes, retakes and no retakes on real flights | Concurrency; the scoring of a normal game, where every harness-flown flight is tool-marked (the owner's one normal-game flight, section 1, owner list) |
| D. A harness campaign that flies a race save and harvests its player folder (the scripted players T-49, T-50, T-53, T-57; harvest T-59; T-33; 15.10) | A real, flown rival folder as a fixture; the seed of R8; a defect finder, as the career campaign was for single player | Nothing new about sync; it is a fixture source |
| E. Two game copies on one PC sharing one folder (T-34, on T-54's paired lanes and the parallel runner's slot copies) | True concurrency of two live writers on one folder (`automation/rs/<run>/<group>/`, inside the harness boundary) | Real sync clients, real clock skew |

Recommendation: **A is primary for the rules, B is primary for everything live**, C is the
end-to-end proof of the real exporter, D feeds B with real flights and is the integration
suite, E is optional. Because the harness creates test games (7.12), way C and the campaign
also prove scoring live: the first change of hands (T-53), a retake and no retakes (T-56)
and two careers to the Mun under the board oracle (T-57).

### What only the owner proves (the one owner list)

This is the single list of what only the owner's eyes or two PCs prove; README points here,
and no other file keeps a copy. Every other proof is a headless test, an in-game test or a
lane (README rule 11).

| What | Why one PC or the harness cannot | Where it is asked for |
|---|---|---|
| Real sync clients: placeholder files, their conflict-copy naming, partial upload | the fake store (T-02) models them; only a real client shows them | spike S8 (S8a, S8b), R3E-23's rerun |
| Clock skew between machines | one PC has one clock | design 6.3, 11 |
| Two operating systems reading one folder | the id and path-length rules are unit-tested, not exercised | R1-08, P7 |
| A race on two real PCs | way E is two copies on one PC | the owner's play test |
| UI feel: the main-menu entry and the setup pages by keyboard, look and label legibility, the plaque, the camera of the board's watch cell | T-43, the census and `op=state` prove that the controls draw and act, not how they feel | R1-14, R1-15, R1-17, R1-18, R3W-09, R3W-16, R3W-25, R4-09, R4-10, R4-11 |
| Frame cost with real sync clients running | the diagnostics budget on a lane (R6-10, R3W-32) runs with no sync client | R6-10, S8b |
| One real normal-game scoring flight | harness games are test games; a normal game's flights are tool-marked when the harness flies them | design 7.12 (owner ruling 2026-11-15) |

Moved off the list because one PC reaches them: a no-DLC install (a profile without the
expansions, for R5-09), the older-build check of edge 22 (an older DLL through `provision.py
--parsek-dll`, T-42's optional cell), and two installs (the parallel runner's slot copies).

### Not in scope

- The feature itself and its own unit tests (each feature slice owns the tests of its code,
  design 11's first list).
- The in-game tests of each category (the slices write them; this plan writes the host lanes
  and the shared helper).
- The GUI gates of the Space Race window (P11, the GUI slice).
- Any co-op feature, release, version bump or tag.

## 2. Starts after

- Version 1.0.2 is released and the owner says go.
- **Day-one tasks (no feature code present):** T-01, T-02, T-03, T-04, T-05, T-07, T-45,
  T-49; T-06, T-38, T-54 and T-59 follow on T-03 and T-05. They touch only the test project,
  the harness and the `TestCommands` tooling module.
- After R1-04 (`RaceGate`): T-10 (with T-01); with R1-06, T-44 (the single-player negative
  contract).
- After R1-08 (`RaceCommand action=status`, `local.cfg`): T-12. After R1-10 (`RaceSetup
  action=create`, on P10-01's core), R1-11 (`RaceSetup action=join`) and R1-23 (the test game
  key): the lane T-13. After T-03, R1-10 and R1-11: T-29 (the helper and RACE-9, on a race the
  lane creates and joins itself), then T-35 (RACE-10) and T-36 (RACE-11); each rival category
  moves its host onto a staged race in its own pull request (R3W-09, R3W-20). After R1-11 and
  R1-12: T-48 (the race overlay at staging).
- After R1-10 and R3E-04 (the manifest write codecs, the standing codec and
  `RaceSync.Run(SyncPorts, SyncInputs)`): T-16, then T-17 and T-47. After R3E-04 and R3E-16:
  T-18 (its self-heal case with R3E-20). After R3E-17 and R3E-18: T-19, and T-39 (RACE-6's
  first form). After R3E-04 and T-38: T-21 (R3E-19 adds its witness-log facet). After R3W-05
  and R3W-09 (the rival store and the rival ghost lane): T-20, then the lanes T-24 to T-28
  (T-25 also after R3E-20; R3W-20 adds RACE-2's Tracking Station step). T-33 after R1-10,
  R3E-16, T-21 and T-59.
- After R4-04 (the board): T-30, T-31, T-52 (also R3E-11), T-32 (also R4-07), T-53 (the first
  change of hands, also T-48, T-59, T-19). After T-21, T-30 and R4-02: T-51 (the board oracle).
  After T-30 and T-48: T-50 (the campaign spec generator), then T-55 (the integration
  variants). T-37 after T-31, R3E-19, R3E-20 and R6-03.
- After R2-01: T-46 (the sites tolerance comparator). After T-13, R2-02 and R1-20: T-42.
  After R1-20 and R1-22: T-41. After R1-15, R1-17 and R1-18: T-43. After R6-02, R6-03 and
  R6-12: T-40. After T-53, R6-02 and R4-07: T-56; after T-56 and R2-12: T-57.
- T-34: after T-54, T-21 and T-30. The parallel-flights machinery it builds on (slot copies,
  each with its own kRPC ports, and `harness/tools/parallel_runner.py`, pull request #2592) is
  on main; T-54 adds the pairing.
- Dropped here and owned elsewhere: T-08, T-09, T-15 (R1-06), T-11 (R1-10, R1-11), T-14
  (R2-13), T-22 (R3W-27), T-23 (R3W-29).
- Spikes: none blocks a task here. S8 (sync clients) feeds the fake store's file behaviours
  (T-02 proceeds without S8, which runs last; S8's findings become new cases then).

## 3. What the code looks like today

### Headless save writing

- `Source/Parsek.Tests/FormatGoldenV1GeneratorTests.cs:283` `BuildState()` builds a populated
  `ParsekScenario` headlessly; `:189` `RunSaveWriters(scenario)` runs the production save
  writers in OnSave's phase order and returns the PARSEK node; `:87` `ResetAllStores()`.
  The writer is skipped unless `PARSEK_WRITE_FORMAT_GOLDEN=v1` (`:20`, `:117`). This is the
  base of the byte-identity tests; today it is private to one test class.
- `Source/Parsek/Controllers/ParsekScenario.cs:484` is `OnSave`. Its token stream is pinned by
  `ParsekScenarioOnSaveCharacterizationTests` against
  `Source/Parsek.Tests/Fixtures/SourceCharacterization/ParsekScenario.OnSave.tokens.txt`; the
  OnLoad twin is `ParsekScenarioOnLoadCharacterizationTests`. Since pull request #2598
  `ParsekScenario.OnLoad` is a skeleton over `OnLoadFlowPrologue`, one flow per branch
  (`PlainRewind`, `InSession`, `Cold`) and the tail parts, threading an `OnLoadContext`;
  `ParsekScenarioOnLoadStepOrderTests` runs the real OnLoad headless per load kind and pins
  the executed step table `Fixtures/SourceCharacterization/ParsekScenario.OnLoad.steps.txt`
  (every call into Parsek code from an `OnLoad*` method is a step). The R1 hooks (R1-03,
  R1-04, R1-08) live in `ParsekRaceScenario` / `ParsekRaceBoot`, not in
  `ParsekScenario.OnLoad`, so no current task moves the step table; a task that adds a call
  inside an `OnLoad*` method does.
- `Source/Parsek.Tests/SaveFormatGoldenGateTests.cs:43-73` `FindCoverageProblems`: every
  registered format version from baseline to current must be carried by a golden folder's
  `formats.txt`; `SaveFormatGoldenGateTests.cs:133` every golden folder needs a manifest and a
  `persistent.sfs`; `:149` committed golden files are frozen by checksum.

### Generators

- `Source/Parsek.Tests/Generators/ScenarioWriter.cs:81` `AddRecordingAsTree`, `:575`
  `InjectIntoSaveFile`, `:585` and `:679` call `WriteSidecarFiles(saveDir)`, which writes
  `Parsek/Recordings/<id>.prec`, `_vessel.craft`, `_ghost.craft`.
- `Source/Parsek/Recording/SidecarVersions.cs:26-30` `SidecarContentHash.Compute(byte[])`: the hash
  the payload file names of 6.1 carry.
- `Source/Parsek/Recording/RecordingPaths.cs:302` validates ids with
  `Path.GetInvalidFileNameChars()`, which differs per OS (P7 moves it).

### The command seam

- `Source/Parsek/TestCommands/TestCommandVerbs.cs:56` the `ImplementedVerbs` set, mirrored by
  hlib and pinned by `test_the_implemented_verb_tuple_mirrors_the_c_sharp_initializer`.
- `Source/Parsek/TestCommands/ParsekTestCommandAddon.NewGame.cs:253`
  `CreateAndStartNewGame`, the create core P10 lifts. `NewGame ... as=<suffix>` creates a
  second save folder in the same instance; `harness/scenarios/SWS-1-save-switch-through-
  main-menu.toml` flies two saves in one process (p50 73 s in the run index).
- `Source/Parsek/TestCommands/TestCommandListHandles.cs:203-206` the kind tokens (`routes`,
  `timeline`, ...), `:280` `ParseKind`: read kinds are added here, not as verbs.
- `Source/Parsek/TestCommands/TestCommandRouteCommand.cs:122` `KnownActions`: the pattern for
  one verb with an `action=` table.
- `docs/dev/design-autotest-command-seam.md:346` the known-verb table; `:677-682` the NewGame
  update note; `:997-1025` `LoadGame scene=spacecenter` and known-gate 6 (the FLIGHT route of
  the seam does not run scenario-module setup the way the SPACECENTER route does).
- `harness/lib/hlib.py` keeps a per-verb row in several tables, for NewGame at `:887`
  (implemented), `:1286` (budget 300.0), `:1544` (tail role), `:1782` (post-mission role),
  `:4571` (`NEWGAME_VERB`); `harness/lib/lanecatalog.py:75` maps the verb to a timeline
  operation; `harness/lib/test_hlib.py:18826` `NewGameSourceSyncTests`.

### The harness

- `harness/run.py:1869` `stage_fixture`: template copy, shared ships, injection, liveState
  patches; `:1978-1979` "The seam's LoadGame bypasses the Main Menu"; `:615` `run_inject`
  runs `inject-recordings.ps1` offline before boot; `run.py:197` `INJECTION_PRESETS`.
- `harness/README.md:110-111`: no new reads or writes outside `harness/`, `automation/`, the
  target instance, the read-only dev install. A shared folder may therefore sit inside the
  target instance or under `automation/` (a paired lane's, T-54).
- `harness/README.md:1419-1430`: the produced-save snapshot copies one save directory into
  `results/<runId>_save/`; nothing else.
- `harness/README.md:366-414`: "Authoring a fast lane" (reuse a host's flight, explicit-UT
  `WarpToUT`, minimal dwells, fail fast, tight windows, tier by measured p50,
  `flight_efficiency.py` after the first green flight).
- `harness/README.md:1737-1743`: tiers daily / nightly / operator / longhaul / full.
- `harness/lib/saveparse.py:165` `parse_sfs`, `:419-534` the row types: the model for a
  pure evaluator. Its gating is armed per spec through `ARMED_ALLOWLIST`
  (`harness/lib/test_hlib.py:12303`).
- `harness/lib/test_hlib.py:3902` `CommittedBatchTallySourceSyncTests` (a spec's pinned batch
  tally must equal the C# attributes), `:4958` `INTERIM_PIN_IDS` (never-flown specs),
  `:7807` the multi-category `GROUP`.
- `harness/lib/test_saveparse.py:4188` `test_fixture_set_is_exactly_the_committed_set` pins
  the directories under `harness/fixtures/saves/`.
- `harness/lib/test_lanecatalog.py:217` `test_the_committed_catalog_is_current`: a new spec
  regenerates `docs/dev/flight-profiles.md` in the same commit.
- `harness/coverage/registry.toml`: dimensions D1 to D18; a new feature adds its values in the
  same PR.
- `harness/fixtures/saves/fresh-career` is the boot save `CS-0-new-career-game` uses for
  `NewGame` (`harness/scenarios/CS-0-new-career-game.toml:41`).
- No spec id starts with `RACE`, `MP` or `ISO` on `main` or on the 15 remote branches read
  with `git ls-tree -r --name-only <branch> -- harness/scenarios`.
- Runs: one harness owner holds the machine lock (`run.py`, `provision.py` or
  `harness/tools/parallel_runner.py`); the parallel runner flies two or three slot copies
  at once (pull request #2592; `harness/README.md`, "Parallel runs").
  (`docs/dev/autotest-roadmap.md:6685` still says runs are serial; that line is stale.)

### Grep-audit gates

- `Source/Parsek.Tests/GrepAuditTests.cs:234` `RunGrepAuditScript(scriptFileName,
  managedFallback)` probes for pwsh and falls back to a managed scan. The gallery write-set
  gate is the model for an allowlist-of-types gate: `:616` `GuiMockAllowedTypes`, `:153` the
  gate, `:159` anti-vacuity, `:179` the cases a denylist missed, `:212` mirror equality with
  the script. `:71` `ParsekFileDeletes_PlantedDeleteUnderRecordings_IsRed` is the
  planted-red pattern.

### Architecture

- `scripts/arch/modules.toml:162` `[layers]`, `:182` `[headless.modules]`, `:213-214`
  `[forbidden]` edges.
- `scripts/arch/test_archview.py:4127` the knot canary `[437, 9, 4, 3, 2]` on main
  ab1cc9478, re-derived by hand.
- `scripts/arch/test_semantic.py:623` the semantic residue ratchet (a hand-set bound).
- `Source/Parsek/Controllers/LoadReconcilePolicy.cs:242` `KnownLoadInitiators`.
- `Source/Parsek.Tests/SaveScopedSessionResetTests.cs:25` `InventoryTypes`.

### Things that read fixture folders by glob

- `Source/Parsek.Tests/OptimizerTransferCohesionTests.cs:521-530` globs
  `harness/fixtures/saves/**/*.prec.txt` recursively; rival payload under `fixtures/saves/`
  would be read as an own recording.
- `Source/Parsek.Tests/Analyzer/SaveDirectoryLoader.cs:62` reads only `Parsek/Recordings`;
  the analyzer never sees a rival cache.

### Measured lane cost (run index, `origin/metrics:flights/index.jsonl`, 4387 rows)

| Tier | Lanes with a PASS | Sum of p50 wall |
|---|---|---|
| daily | 28 | 0.42 h |
| nightly | 289 | 12.5 h |
| operator | 258 | 14.4 h |
| longhaul | 5 | 3.4 h |

The table counts lanes with a PASS in the index, under the tier they had then. The specs on
main ab1cc9478 (`docs/dev/flight-profiles.md`): 595 lanes, daily 29 (S0.14 joined on
2026-11-14), nightly 284, operator 271, longhaul 5, pending-fixture 6; a full run flies 589
(`tier_runner.py` leaves out pending-fixture). The daily 0.42 h is the sum over the 28 daily
lanes with a PASS.

Reference p50s: SWS-1 73 s; CS-0 new career 48 s; PPB lanes 45 to 51 s (the boot floor of a
seam-only lane); R7c 52 s; RW-1 146 s; B2-lko-ascent 272 s; B1-pad-hop 389 s.

## 4. Tasks

The install of the race lanes (owner rulings of 2026-11-15, design 7.2): a race checks the
game's tech tree and part values against the race content table at create, join and load
(R1-20). `harness/provision/profiles/stock-minimal.toml` installs CommunityTechTree and
ProbesBeforeCrew, so on that install the tree differs and every race save reaches the
warning. Every race lane that creates, joins or loads a race save therefore runs on the
`stock-tree` profile (`stock-minimal` without those two mods; the RACE lanes of this plan,
RACE-9 to RACE-11 included, and SITES-1 and SITES-3), where the check passes with no
difference. One lane stays on `stock-minimal` on purpose and proves the warning path: SITES-2
(R2-13). A lane answers a warning only through the seam arg `unscoredConfirm` (R1-20),
whose default `cancel` makes an unexpected warning fail the step at once. Three more lanes stay
off `stock-tree` on purpose: RACE-15 (T-41, `stock-minimal`, the launch warning), RACE-16
(T-42, `modded-compat`, the refused load) and RACE-17 (T-42, `stock-minimal`, the refused load
with play without scoring off). RACE-0 (T-07, a stock baseline) and RACE-20 (T-44, a
single-player save) load no race save and stay on `stock-minimal`.

Tier and tag (2026-11-15): every race lane (RACE, SITES, RCA, RCB, RCI) is `tier =
"operator"`, as every `stock-tree` lane is today (40 of 40 specs), whatever its profile, and
carries the tag `race`; nothing joins the nightly tier. The race lanes are flown on request
with `parallel_runner.py run --tag race` (T-45) when a milestone's exit proof is due (README
rule 11). A lane other than RACE-1 may start in a race save through the race overlay (T-48)
instead of a join round trip across the main menu; RACE-1 keeps the real create and join.

Test games (design 7.12, owner ruling 2026-11-15): only the test tools make a test game
(`testGame = True` in the manifest, R1-23), and two can: the seam's create step in a running
game, and T-48's fixture writer, which stages a race save before the game starts through the
same manifest writer. The setup dialog cannot, and no player-reachable path sets the key. A test
game is shown as "Test game" (after the race name) to every player, and in it the test-tools
mark does not stop a flight from scoring; every other mark applies. Every race the
harness creates or overlays is a test game, so lanes assert real holders and points. A lane
that must see a normal game's tool mark builds that rival with the generator (T-16), which
writes `testGame = False`.

### T-01 Headless save-output driver and its determinism pin

- **Change:** move `BuildState`, `RunSaveWriters` and `ResetAllStores` from
  `FormatGoldenV1GeneratorTests` into `Source/Parsek.Tests/Generators/SaveWriterDriver.cs`
  (test project), bodies verbatim; the golden generator calls the driver. Add
  `SaveWriterDriver.WriteToText(ConfigNode)` (invariant text form) and
  `SaveWriterDriver.Run(Action<ParsekScenario> between)` that runs the writers twice with a
  hook between them and returns both texts. R1-12's golden `v2` generator and R3E-04's
  golden `v3` generator both run through this driver (OV-15).
- **Proof:** `SaveWriterDriverDeterminismTests`: two runs with an empty hook are
  byte-identical; the same under `de-DE` and `ro-RO` thread cultures; the v1 golden writer
  still produces its checksums when armed (env-gated, run by hand once).
- **Registers:** none (test-only).
- **Size:** S. **Depends on:** nothing. **PC:** no.
- **Risk:** low; a non-deterministic writer (dictionary order) would show here first, which
  is the point.

### T-02 Fake synced store and fake clocks

- **Change:** new test-only folder `Source/Parsek.Tests/Race/Sim/`:
  - `FakeSyncedStore`: one authoritative "cloud" plus one view per machine; a write lands in
    the writer's view at once and reaches other views through a delivery queue.
  - Per-delivery controls: delay, reorder, drop until reconnect (offline machine), partial
    content (a truncated file visible before the rest arrives), placeholder files (name
    present and marked as a placeholder until the file is opened; the open delivers the
    content, as an on-demand sync client does), conflict copies (two writers of one path become
    `name (conflict <machine> <n>).ext`, the naming S8 will confirm), foreign edits and
    renames, deletes.
  - `FakeClock` per machine (real time and game UT independently settable, skew allowed).
    It is the one clock fake: once R3E declares `IRaceClock`, `FakeClock` implements it
    (the binding lands with T-18), and no second clock fake is written (OV-17).
  - A seeded `SimSchedule` that records every delivery so a failing seed replays.
  - R3E-16's tests add `FakeRaceTransport` as a thin layer over this store (OV-17).
- **Proof:** `FakeSyncedStoreTests` (each control, determinism by seed, a replayed schedule
  equals the original).
- **Registers:** none.
- **Size:** M. **Depends on:** nothing. **PC:** no.
- **Risk:** low; the store models what sync clients do, and S8 may add a behaviour, which is
  one more control.

### T-03 Shared-folder staging in run.py

- **Change:**
  - Spec surface, validated in `hlib.validate_spec`: `[fixture.race] sharedFolder =
    "empty" | "fixtures/race/<name>"`. The placeholder `${raceShared}` resolves to
    `<instance>/rs/<n>`, a short path (6.3's path-length validator) inside the target
    instance (README:110). The validator refuses any other value for a folder argument of a
    race verb. Every race lane uses this path, R1's and R2's included; the
    `<instance>/race-shared/` form of R1-13 is retired (OV-02).
  - `run.py::stage_fixture`: create or wipe `<instance>/rs/`, copy the fixture folder in
    (containment assert like the save template), log one stage line.
  - Post-run: copy `${raceShared}` to `results/<runId>_race/` next to `_save/`, and archive
    a listing (names, sizes, hashes; not the payload) with the run's small artifacts.
  - Pure helpers in hlib: `race_shared_dir(instance, n)`, `is_race_fixture_path`.
  - As built (2026-11-16, corrected here): `<n>` is always `"0"` today
    (`hlib.RACE_SHARED_DEFAULT_LEAF`); each install slot is its own instance, so the folder
    is per slot without a slot number, and stage wipes the whole `<instance>/rs/` for every
    race lane. `${raceShared}` is a WHOLE-value token (like `${runSave}`), substituted in
    seam step args only (a `missionParams` value carrying it is refused). The race verbs'
    folder args are named in `hlib.RACE_FOLDER_ARG_BY_VERB` (`RaceSetup folder`,
    `RaceCommand path`) ahead of the verbs (T-07, T-08). `validate_spec` takes the
    committed fixture names as `race_fixtures=` (run.py passes `list_race_fixtures()`,
    the folders under `harness/fixtures/race/`). The copy to `results/<runId>_race/` is
    taken at KSP exit in the main thread (before the next lane can stage and wipe), not in
    the post-quit verify worker; the listing is `results/<runId>_race.listing.txt` (an
    optional entry of the results archive) and the result JSON carries `raceSnapshot`.
    No harness path-length validator exists yet; the short leaf is the only mitigation
    (section 8, path length). T-54 adds `sharedFolder = "pair"` by extending
    `hlib.validate_race_fixture` and `run.stage_race_shared`; T-06 and T-48 resolve their
    targets through `hlib.race_shared_dir`.
- **Proof:** `test_hlib` cells for the validator (accepts, refuses outside paths, refuses an
  unknown fixture), `test_run_smoke` with `_fake_ksp` (staged, snapshotted, wiped before the
  next lane). As built: `RaceSharedFolderSpecTests`, `RaceSharedFolderStagingSmokeTests`.
- **Registers:** `harness/README.md` (fixtures section, snapshot section);
  `docs/dev/autotest-status.md` row for the harness capability.
- **Size:** M. **Depends on:** nothing. **PC:** no.
- **Risk:** low; inert for every spec without `[fixture.race]`.

### T-04 Snapshot every save a lane creates

- **Change:** `[fixture] snapshotSaves = ["${runSave}-b", ...]`: `run.py` copies each named
  save folder of the instance into `results/<runId>_save-<suffix>/` the same way it copies
  the run save. The `as=` saves a lane creates are named in advance, so the list is static.
  - As built (2026-11-16): an entry is `${runSave}-<suffix>` or the literal
    `<runSaveName>-<suffix>`, suffix `[A-Za-z0-9_]+` (the `NewGame as=` rule); the
    validator (`hlib.validate_snapshot_saves`) refuses the run save itself, a duplicate,
    any other name, and a suffix whose dir name would end in a name another results pass
    keys on (`hlib.RESULTS_NAME_SUFFIXES`), and warns when no step arg names the save.
    Staging removes each named save first (fail closed), the pipelined post-quit copy
    holds it, the copy runs in the post-quit step right after the run save's (size and
    disk refusals, tmp-and-rename), and retention keeps the newest 3 per scenario and
    suffix. A declared save absent after a valid driver stage reds
    `PARSEK-FAIL(snapshot-save-missing)`, the last red. The result JSON carries
    `snapshotSaves`. The run save's `_save` name and the passes keyed on it are
    unchanged, so a reader that requires a name ending in `_save` (T-49) never sees a
    named-save copy.
- **Proof:** `test_hlib` validator cells; `test_run_smoke` cell that two saves are snapshotted.
  As built: `SnapshotSavesSpecTests`, `SnapshotSavesSmokeTests`.
- **Registers:** `harness/README.md` snapshot section.
- **Size:** S. **Depends on:** nothing. **PC:** no.
- **Risk:** low.

### T-05 Pure race evaluator, part 1: leak scan and save equivalence

- **Change:** new `harness/lib/raceparse.py` (stdlib only):
  - `collect_rival_ids(folder_listing, own_player_dir)`: player ids from player subfolder
    names, recording ids from payload names `<recordingId>.<hash><suffix>` (6.1).
  - `leak_scan(save_dir, rival_ids)`: every file of the produced save except
    `Parsek/Race/` is searched for every rival id; any hit is a finding with file and line.
  - `save_equivalence(save_a, save_b, mask)`: a canonical diff of two produced saves
    (`persistent.sfs` through `saveparse.parse_sfs`, plus every `Parsek/` file except
    `Parsek/Race/`), with a mask of keys whose values are compared within a tolerance
    instead of exactly (UT and UT-derived values). The mask is a module constant pinned by
    a test, never a spec key.
- **Proof:** `harness/lib/test_raceparse.py`: a planted id in a sidecar reds; an id under
  `Parsek/Race/` does not; two copies of one committed fixture are equivalent; a one-byte
  change outside the mask reds; a UT change inside tolerance passes and outside reds.
- **Registers:** `harness/README.md` module list.
- **Size:** M. **Depends on:** nothing. **PC:** no.
- **Risk:** low; the file name shape of 6.1 is the only assumption, and T-21 re-checks it
  against the codec.

### T-06 Harness-side folder step

- **Change:** a seam step kind that `run.py` executes itself, between two seam steps, never
  sent to the game: `{ harness = "raceFolder", args = { op = "replace|edit|delete", from =
  "fixtures/race/<name>", path = "<relative>" } }`. Every target resolves under
  `${raceShared}`; anything else is refused at validation. Logged on the harness log.
- **Proof:** `test_hlib` validator cells; `test_run_smoke` cell that the step runs in order
  and touches nothing outside the folder.
- **Registers:** `design-autotest-harness-core.md` (step kinds); `harness/README.md`.
- **Size:** S. **Depends on:** T-03. **PC:** no.
- **Risk:** medium; it is the first non-seam step, so the step grammar, the lane catalog and
  the timeline tooling must accept it (each has a cell).

### T-07 Seam read kind `ListHandles kind=sites`

- **Change:** `TestCommandListHandles.cs`: kind `sites`. One line per fact, invariant
  formatting: the working space center's body position (lat, lon, alt), each stock launch
  site's name and position, the recovery reference point, the terrain height at a fixed
  list of sample points (the four patches of 7.3 once the site table (R2-01) exists; today the KSC
  pad and runway ends). Works in any save. Applier partial in
  `ParsekTestCommandAddon.ListHandles*.cs`.
- **Proof:** unit tests of the formatter and the sample-point table; hlib's
  `LISTHANDLES_KIND_VALUES` and `ListHandlesSourceSyncTests` updated; a new operator lane
  **RACE-0-stock-sites-baseline** (tag `race`, `stock-minimal`, fresh-career, `ListHandles
  kind=sites`, about 45 s)
  pins the stock values SITES-2 (R2-13) compares against (OV-04). SITES-1 to SITES-3 read
  their per-site values with this kind, not with extra batches (OV-03).
- **Registers:** `design-autotest-command-seam.md` ListHandles grammar; hlib tables; lane
  catalog regeneration; `registry.toml` a new `race` dimension at the next free id when this
  task lands (expected today: D19, written D19 below; README rules 5 and 10: the branch
  renumbers if main adds a dimension first) with value `stock-sites`;
  `autotest-status.md` row; `INTERIM_PIN_IDS` not needed (no batch).
- **Size:** M. **Depends on:** nothing. **PC:** yes (one flight of RACE-0).
- **Risk:** low; read-only.

### T-08 Race grep gates: shared scanner and "no store in the rules"

Dropped: replaced by R1-06 (OV-10).

### T-09 Race grep gate: Recording types only in the loader

Dropped: replaced by R1-06 (OV-12).

### T-10 Single-player inertness test (15.8)

- **Change:** `Source/Parsek.Tests/Race/SinglePlayerInertnessTests.cs` on the T-01 driver: a
  single-player state's writer output is byte-identical before and after every race entry
  point is invoked with `RaceGate.IsRaceSave == false`; no `ParsekRaceScenario` node and no
  `Parsek/Race/` directory are written; a source gate that every public entry of the three
  race modules reads `RaceGate` first (no production attribute, OV-14). The entry list is
  derived from source, not kept by hand: every `public` or `internal` member of a type in
  the three race module folders that the arch module map marks as an entry (a Harmony patch,
  a `GameEvents` handler, a `ScenarioModule` or `KSPAddon` callback, a seam applier) is
  listed by a parse of the folders, and the test reds when the derived list and the gate
  scan disagree.
- **Proof:** the tests themselves; log capture asserts the one `[Race]` "inert" line; a
  planted-red cell (a synthetic entry file without the `RaceGate` read, parsed from a string)
  must red the gate, and an anti-vacuity count that the derived list is not empty.
- **Registers:** none.
- **Size:** S. **Depends on:** T-01, R1-04. **PC:** no.
- **Risk:** low.

### T-11 Seam verb `RaceSetup action=create|join`

Dropped: replaced by R1-10 and R1-11 (OV-01).

### T-12 Seam action `RaceCommand action=set-folder` and the seam tables R1 leaves

- **Change:** R1-08 lands the verb `RaceCommand` with its `KnownActions` table (the
  RouteCommand shape), the `KnownActions` source-sync cell, its hlib rows and its seam
  contract, with `action=status` (no arguments; it reports the current session's gate,
  race mark, slot, look, folder and last sync). R1-10 lands `RaceSetup action=create`
  with the `RaceSetupSourceSyncTests` cell, the hlib rows at the NewGame rows, the
  `lanecatalog.py` operation map and the `#### RaceSetup` contract; R1-11 adds
  `action=join` (OV-01). This task adds:
  - `action=set-folder path=${raceShared}` to `KnownActions`, through the same validator as
    Settings. Single-phase.
  - Its rows in the hlib tables and the `#### RaceCommand` contract of
    `design-autotest-command-seam.md`, and any per-verb row R1-08, R1-10 and R1-11 left out.
  - The argument names those contracts carry (this plan's, OV-01): `RaceSetup` takes
    `action`, `race`, `mode`, `preset`, `folder=${raceShared}`, `name`, `look`,
    `sites=on|off`, `endDate`, `noRetakes`, `as=<suffix>`, and for `action=join` also
    `startOverPlayerId` (start over in that seat); two-phase like NewGame, refusal reasons
    the setup dialog's (7.2) verbatim, budget 300 s. `RaceCommand action=status` takes no
    arguments. No verb marks the save itself: R3E-08 marks it as tool-used at the seam's
    arming, at the in-game test runner and at the autorun hook (OV-33).
- **Proof:** parse tests of `set-folder`; its hlib rows; R1-08's `KnownActions` source-sync
  cell grows.
- **Registers:** `design-autotest-command-seam.md`; hlib tables; `scripts/arch/modules.toml`
  placement is automatic (the `TestCommands` folder rule).
- **Size:** S. **Depends on:** R1-08, R1-11 (the verb file's order: R1-08, R1-10, R1-11,
  then this task). **PC:** through T-13.
- **Risk:** low.

### T-13 Lane RACE-1-setup-create-join

- **Change:** `harness/scenarios/RACE-1-setup-create-join.toml`, tier operator, tag `race`, profile
  `stock-tree` (the stock tree, so R1-20's check passes; section 4 head), boot `fresh-career`,
  `[fixture.race] sharedFolder = "empty"`, `snapshotSaves` for the joiners. Flow: create as A; join
  as B, C, D (`as=` saves); a fifth join refused `race-full`; a name already taken refused; Create
  on a folder with a race refused; `RaceCommand status` after each; slots 0 to 3 and four distinct
  looks pinned through `[Race]` lines; `ListHandles kind=committed` count 0 in each. It takes
  R1-13's assertions (OV-02): from MP-1, `Gate: race save` in the Space Center of the new save,
  `Gate: cleared` at the main menu, the boot single-player save loaded with `LoadGame
  scene=spacecenter` reads `Gate: not a race save reason=no-mark` and gets no `SCENARIO` node from
  R1, and the race save loaded again turns the gate on with the same `raceId`; from MP-2, B's join
  into a folder that already holds A's race takes slot 1 and the first free look (`[Race] Join: ...
  slot=1 look=..`). Last, B starts over in its seat (`RaceSetup action=join startOverPlayerId=<B>`,
  R1-11): same slot and look, a fresh save. Added 2026-11-15: `RaceCommand status` reports
  `testGame=True` for the created race and every join (R1-23), and, from R2-01 on (soft: added in
  the later pull request of the two), the `[RaceSites] Reconcile:` line on each scene request of a
  race save and at the main menu (each negative assertion of the lane, such as "no `Sites apply
  failed`", gets a source-sync cell for its literal, as `WarpRunTokenSourceSyncTests` does).
- **Proof:** the lane; T-38's `raceParse` leak scan gated from the first flight; structure
  report-only until T-21 arms it.
- **Registers:** lane catalog; `registry.toml` D19 values `create`, `join`, `refusal`;
  `autotest-status.md`; tags.
- **Size:** M. **Depends on:** T-03, T-04, R1-10, R1-11, R1-23; soft: R2-01. **PC:** yes.
- **Risk:** medium; four main-menu round trips put it near 150 s; if it is slower, split the
  refusals into their own lane. It is the only proof that the mark reaches the scene on a
  real create, so a red here blocks R1-14 (as R1-13's lanes did).

### T-14 Lane RACE-5-stock-kerbin-after-race

Dropped: replaced by R2-13 (OV-04).

### T-15 Race grep gate: no writer of the own model

Dropped: replaced by R1-06 (OV-11).

### T-16 Generator `RaceFolderBuilder`

- **Change:** `Source/Parsek.Tests/Generators/RaceFolderBuilder.cs`: fluent builder of a
  whole shared folder: `race.cfg` (manifest), N players with `player.cfg`, a standing
  history per player (seq, chain, dates, tool marks, `since`, receipts), the manifest's
  `noRetakes`, end date and `testGame` (R1-23; default `False`, so a generator rival in a
  test of the tool mark is a normal game's), payload made with
  `RecordingBuilder` / `VesselSnapshotBuilder` and written through `ScenarioWriter`'s
  sidecar writer, then renamed `<recordingId>.<hash><suffix>` by
  `SidecarContentHash.Compute`. Writers are the production codecs (R1-10's manifest
  writers, R3E-04's `StandingCodec.Write`), never a copy.
  Faults on request: incomplete newest standing (half-synced), seq gap, broken chain,
  pruned middle, deleted latest, renumbered or edited standing (edited after seen), newer
  `raceFormat`, conflict copy, placeholder, missing payload, unsafe id, two machines with
  one seq. This is the one shared-folder builder of the race work: R3E-05's
  `SharedFolderBuilder` is dropped and its shapes are listed here, R3E's tests use this
  builder with R1-08's in-memory `IRaceFolder`, and R3W-05's `RivalCacheBuilder` builds
  its content through it (OV-17). The R6-11 folders (no retakes, end date) are built with
  it too (OV-08). It adds no production member: it calls R1-10's manifest writers (first
  production caller `ParsekRaceSetup.Create`) and R3E-04's `StandingCodec.Write` (first
  production caller the export), so the uncalled-method ratchet does not move (A1).
- **Proof:** `RaceFolderBuilderTests`: every built folder reads back through the production
  reader; each fault is refused or flagged with the reason 7.15 names; a fault-kind
  completeness cell lists every refusal reason the import declares (the reason enum or table
  of R3E-18, read by reflection) and reds when one has no builder fault that produces it.
- **Registers:** none.
- **Size:** M. **Depends on:** R3E-04, R1-10. **PC:** no.
- **Risk:** medium; it must follow the codec exactly, so it writes only through it.

### T-17 Committed shared-folder fixtures

- **Change:** `harness/fixtures/race/<name>/`, written by an env-gated xUnit writer
  (`PARSEK_WRITE_RACE_FIXTURES=1`) from T-16: `three-rivals` (complete), `three-rivals-faults`
  (incomplete newest, conflict copy, tampered standing, newer format), `rival-station`
  (an orbiting station, a lander with a leftover, a planted flag, an SOI crossing), and
  `holder-gen1` / `holder-gen2` (one race at two moments at `Kerbin/Orbit`; gen2 adds an
  earlier date by a second rival). This writer also writes the folders R6-11's headless
  tests read (OV-08): `noretakes-seen-first` (`noRetakes = true`, A's `Kerbin/Orbit` in A3,
  B4 listing `Seen` A3 and an earlier date) and `enddate-between` (an end date between two
  of B's standings), so they are committed and kept current like the others. A
  `README.md` lists each folder and its builder call.
  `RaceFixtureFoldersAreCurrentTests` rebuilds each into a temp folder and compares bytes
  (like the lane catalog cell); a `frozen/` subfolder is never rebuilt and serves format
  compatibility after a `raceFormat` bump.
- **Proof:** the currency test; `test_hlib` cell that `harness/fixtures/race/` holds exactly
  the folders the README lists.
- **Registers:** `harness/fixtures/race/README.md`, `harness/README.md` fixtures section.
  Not under `fixtures/saves/`, so neither `test_fixture_set_is_exactly_the_committed_set`
  nor the E4 glob (`OptimizerTransferCohesionTests.cs:521-530`) reads them.
- **Size:** S. **Depends on:** T-16. **PC:** no.
- **Risk:** low.

### T-18 Two-machine simulator bound to the ports

- **Change:** `Source/Parsek.Tests/Race/Sim/RaceSimMachine.cs`: one machine = its own
  in-memory save state, R1-08's in-memory `IRaceFolder` for its local race directory,
  R3E-16's `FakeRaceTransport` over its `FakeSyncedStore` view, its `FakeClock` as the
  `IRaceClock`, an `IPublicStateSource` fed by test rows. Each machine builds R3E-04's
  `SyncPorts` from these (OV-18). `RaceSim` steps N machines and calls
  `RaceSync.Run(SyncPorts, SyncInputs)` on each in a seeded order, collecting every
  `SyncResult`. It is the one machine fake: R3E-19's `RaceSyncLoopbackTests` and T-31 run
  on it (OV-19). It adds no production member: `RaceSync.Run` and the port interfaces have
  R3E-04's driver as their production caller (A1).
  `RaceSyncSimulatorTests`: arrival order permutations; one machine offline for days then
  back (seq continuity, own mirror copy-out); partial files never imported; a placeholder
  imported only after it was opened, one per poll;
  conflict copies ignored with a note; a foreign edit to the own subfolder healed
  (R3E-20); a late joiner; export-before-import in one tick.
- **Proof:** the tests; each seed printed on failure. Anti-vacuity: over the committed seed
  set, a count per fake-store control (delay, reorder, drop, partial, placeholder, conflict
  copy, foreign edit, delete) that it fired at least once, red when one never fires. A
  planted-bug cell runs the simulator with a test-only port wrapper that drops every second
  import and must red the suite's board-equality check.
- **Registers:** none.
- **Size:** M. **Depends on:** T-02, R3E-04, R3E-16; R3E-20 for the self-heal case (it
  joins the suite in R3E-20's pull request if T-18 lands first). **PC:** no.
- **Risk:** medium; if `SyncPorts` or `SyncInputs` holds a live type, the binding cannot be
  built, which is a 15.3 contract break to report, not to work around.

### T-19 `RaceCommand action=sync`

- **Change:** add `sync` to `KnownActions`: runs one copy step and one import on the main
  thread and prints the `SyncResult` counts (imported, refused with reasons, healed,
  exported). Also `action=offline|online`, which makes the folder port report unreachable
  for the session (no file is touched), so a lane can drive the offline path.
- **Proof:** parse tests, hlib rows, R1-08's `KnownActions` source-sync cell grows.
- **Registers:** seam doc, hlib.
- **Size:** S. **Depends on:** T-12, R3E-17, R3E-18. **PC:** through T-24.
- **Risk:** low.

### T-20 `ListHandles kind=rivals`

- **Change:** one line per rival: player id prefix, slot, look, latest complete seq, recording
  count, and per rival ghost in the scene: recording id, body, situation, whether it is a
  vessel in `FlightGlobals.Vessels` (must be no, or a map object guarded by
  `IsGhostMapVessel`), collider count, CommNet node (must be none).
- **Proof:** formatter tests, hlib kind values.
- **Registers:** seam doc, hlib.
- **Size:** S. **Depends on:** R3W-05, R3W-09 (the kind reads the rival store R3W-05 adds
  and the rival ghosts R3W-09's engine draws; R3W-05's own in-game test cell is the store's
  first caller, so this kind adds no first caller). **PC:** through T-26.
- **Risk:** low.

### T-21 Race evaluator, part 2: structure facets and the verifier row

Split on 2026-11-15: the verifier row and the gated leak scan land early as T-38; this task
keeps the structure facets.

- **Change:** `raceparse.py` gains structure facets over `results/<runId>_race/` and the
  produced save's `Parsek/Race/`: per player chain intact, latest complete seq, seq gaps,
  completeness of payload, own mirror equals own subfolder; R3E-19's pull request adds the
  witness-log facet. They join T-38's `raceParse` verifier row under the spec block
  `[expectations.race]`. Structure windows are report-only until armed per spec
  (`PARSEK-FAIL(race-structure)`), armed through the same allowlist discipline as
  `saveParse`; the leak scan stays gated (T-38).
- **Proof:** `test_raceparse.py` over folders the T-16 writer produced (copied in as text
  test data); a currency cell that the copied test data equals what T-16's writer produces
  today (the T-17 pattern: rebuilt into a temp folder by the env-gated writer and compared,
  so a format change in R3 reds here, not in a lane); a smoke run with `_fake_ksp`; a cell
  that every verdict subkind is in hlib's verdict table.
- **Registers:** `harness/README.md` verifier list; `design-autotest-harness-core.md`
  verdict subkinds; `ARMED_ALLOWLIST` grows a race block entry when one is armed.
- **Size:** M. **Depends on:** T-05, T-03, R3E-04, T-38; soft: R3E-19 (the witness-log
  facet). **PC:** no.
- **Risk:** medium; a format change in R3 moves these facets, so the test data is
  regenerated, never hand-edited.

### T-22 Race grep gate: rival store names only in allowlisted files

Dropped: replaced by R3W-27 (OV-13).

### T-23 Isolation byte-identity test (6.4)

Dropped: replaced by R3W-29 (OV-20).

### T-24 Lane RACE-2-import-generator-folder

- **Change:** operator, seam-only. Start in the race staged from `three-rivals-faults` (a
  folder that already holds a race is joined, never created into; through T-48's overlay
  once it lands, else a join); `sync`; assert each refusal by its reason, the conflict copy
  ignored, the incomplete newest standing skipped, the newer-format standing refused whole,
  the last good state kept; `ListHandles kind=rivals`. R3W-20's pull request adds the
  Tracking Station step (`LoadGame scene=trackstation` with the rival map objects present
  and guarded), so the refusals fly three waves earlier.
- **Proof:** lane; `raceParse` leak scan gated.
- **Registers:** catalog, D19 `import`, `refusal`, status.
- **Size:** S. **Depends on:** T-17, T-19, T-20, T-21; soft: R3W-20 (the Tracking Station
  step). **PC:** yes.
- **Risk:** low.

### T-25 Lane RACE-8-export-self-heal-offline

- **Change:** operator, seam-only. Create; `RaceCommand offline`; drive one export while
  offline (R3E-20 names the seam-reachable trigger; a commit is the fallback), and
  assert it lands in the own mirror only; `online`; `sync` and assert the copy-out with
  continuous seq; a second `sync` with nothing changed exports nothing (11); a
  harness-side edit of the own subfolder (T-06's step) followed by `sync` asserts the
  heal.
- **Proof:** lane; `raceParse` chain and seq facets.
- **Registers:** catalog, D19 `export`, status.
- **Size:** S. **Depends on:** T-19, T-21, T-06, R3E-20. **PC:** yes.
- **Risk:** low.

### T-26 Lane RACE-3-rival-ghosts-flight

- **Change:** operator. Start in the staged `rival-station` race (overlay, T-48); `sync`;
  launch the stock craft from the VAB (`GoToEditor` / `LaunchFromEditor`), `WarpToUT` with
  explicit UTs across the rival SOI crossing, the rival landing and the rival flag plant;
  `ListHandles kind=rivals` after each (no vessel, no collider, no CommNet node). Then
  `InvokeRewindToLaunch` and `ListHandles kind=rivals` again: the rival ghosts are all still
  there (design edge 8, a rewind keeps rival ghosts). It keeps only these seam
  assertions and runs no batch: the in-game isolation cells (progress and ledger unchanged
  across the SOI crossing and the landing) are R3W-30's `RaceRivalIsolation`, hosted by
  RACE-10 and RACE-11 (OV-21).
- **Proof:** lane; leak scan.
- **Registers:** catalog, D19 `rival-ghosts`, status.
- **Size:** M. **Depends on:** T-17, T-19, T-20. **PC:** yes.
- **Risk:** medium; the warp lands near, not on, each UT, so the waits target after-event
  UTs with margin.

### T-27 Lane RACE-4-isolation-whole-save

- **Change:** operator; the one whole-save isolation lane of the race work (OV-05; R3W's
  acceptance names RACE-4). Two race saves made by the lane (`as=` suffixes `-e` and
  `-f`), identical setup, the first with an empty folder, the second joined to a copy of the same
  race with `rival-station` rivals synced. The same scripted session in each: Space Center,
  Tracking Station, one FLIGHT visit with the vessel on the pad, explicit-UT warps across
  every rival event, `SaveGame`. `raceParse.save_equivalence` on the two snapshots with the
  pinned mask, gated (`PARSEK-FAIL(race-isolation)`), plus the leak scan. The mask holds
  T-05's UT keys and the keys that differ by nature (real-time stamps, session ids), each
  with its written reason.
- **Proof:** lane; T-05's equivalence cells pin the mask.
- **Registers:** catalog, D19 `isolation`, status.
- **Size:** M. **Depends on:** T-04, T-05, T-17, T-19; the rival content it crosses (map
  objects, flags) lands with R3W-18 and R3W-24 (soft: the lane flies before them on the
  cache alone). **PC:** yes.
- **Risk:** high; anything nondeterministic in a stock save (a vessel that moves under
  physics, a contract offer drawn at random) shows as a diff that is not a leak. The first
  flights are reading runs; each new mask entry needs a written reason.

### T-28 Lane RACE-6-two-saves-in-turn

Split on 2026-11-15: T-39 lands the same spec's first form (exchange through the seam only,
no ghost, no board line) three waves earlier; this task completes it.

- **Change:** operator, tag `race`. A creates (a test game, R1-23), quicksaves, flies the
  campaign's first flight shape: the stock Jumping Flea launched from the VAB with
  `GoToEditor` / `LaunchFromEditor` and the `science_bench_recover` mission (CS-1 / CS-3; the
  Flea resolves from the KSP root `Ships/`, `TestCommandEditorRoute.cs:214-238`, so a save
  created by `RaceSetup` needs no pad craft, which the B1 pad hop would); commits, exports. B
  joins (`as=`), syncs: A's ghost launches from site 0 (`ListHandles kind=rivals`), A's
  board row is present and A holds it (a test game, so A's flight scores; `ListHandles
  kind=board`), B's ledger untouched (`RunTests LedgerGroundTruth`). Then A loads the
  pre-flight quicksave and exports: R3E-22's message "1 shared flight left the race" is
  logged once on A's side (`[RaceSync] Message posted:`); B syncs: the ghost and the row are
  gone. Design 11's first and third lanes in one spec, per
  "reuse a host's flight". From R4-04 on, its `logContract` pins `[RaceBoard] Board computed`
  after each of B's syncs (R4-04's line, OV-07). Each "logged once" or "not logged" check has
  a source-sync cell for its literal.
- **Proof:** lane; `raceParse` gated leak scan, armed structure after two reading runs.
- **Registers:** catalog, D19 `two-saves`, `quickload-drop`, status.
- **Size:** M. **Depends on:** R1-10, R1-11, T-19, T-20, T-21, R3E-16, T-39; soft: R4-04 (the
  row assertion and the board line are added when it lands; the lane flies before it
  without them). **PC:** yes.
- **Risk:** medium; about 700 s, one of the longest RACE lanes; a quickload across a shared
  flight is exactly the case most likely to find a real defect.

### T-29 In-game race context helper and the Space Center host lane RACE-9

Split on 2026-11-15 (both reviews): T-29 keeps the helper and the first host; the FLIGHT host
RACE-10 is T-35 and the TRACKSTATION host RACE-11 is T-36, so each host flies once its scene
has race categories instead of all three waiting for the last rival task. Moved earlier the
same day (ground-up rework): each host creates its own race and joins it at slot 1, as RACE-1
does, instead of joining a staged rival folder, so RACE-9 flies at wave 8 instead of 14 and
RACE-10 and RACE-11 at wave 9 instead of 15; the rival categories move their host onto a
staged race in their own pull requests (R3W-09, R3W-20).

- **Change:** `Source/Parsek/InGameTests/RaceTestContext.cs` (tooling module): `RequireRace
  (out RaceGate)` which `InGameAssert.Skip`s with the required context named, accessors for
  the rival store and the applied site. One host lane per scene, each one `RunTests` with a
  comma list of the categories of the table in section 6 (A2; one scene per category,
  OV-09). This task adds the first:
  - **RACE-9-sc-batch**, SPACECENTER, 5 categories:
    `RaceMode,RaceSites,RaceExchange,SpaceRace,RaceLooks`.
  The other two hosts, for the record of all three:
  - **RACE-10-flight-batch** (T-35), FLIGHT, 6 categories:
    `RaceSitesFlight,RaceFacts,RaceRivalLane,RaceRivalIsolation,SpaceRaceFlight,RaceLooksFlight`.
  - **RACE-11-ts-batch** (T-36), TRACKSTATION, 4 categories: `RaceSitesTrackingStation`,
    `RaceFactsTrackingStation`, `RaceRivalLaneTrackingStation`,
    `RaceRivalIsolationTrackingStation`.
  Each host creates a race (`RaceSetup action=create`, a test game) and joins it as a second
  player (`action=join as=`), so it runs in a race save at slot 1 (the moved space center,
  the patches and the copies exist there once R2 lands), and the race categories run with
  `passed` above zero (a single-player batch would skip them all); RACE-10 enters FLIGHT with
  a vessel on the home pad. When a category needs rivals (R3W-09's `RaceRivalLane`, R3W-20's
  Tracking Station cells), that task's pull request moves its host onto a race staged from
  T-17's fixtures (T-48's overlay); R3W-05's pull request adds the rival-store accessor to
  `RaceTestContext`. RACE-9's `logContract` pins R1-07's `[TestBatch] kept Parsek/Race/` line
  (the batch revert keeps the race directory). These three are the hosts in a RACE save.
  RACE-20 (T-44) hosts `RaceLooks`, `RaceFacts` and `RaceFactsTrackingStation` in a
  SINGLE-PLAYER save, one `RunTests` step per category and scene (the cells that need a race
  save skip there, the single-player halves run), so it pins those three too and is
  re-pinned with their race host. No other spec runs a batch of these categories: SITES-1
  runs no batch (OV-03), R5-13 is dropped (OV-06), and RACE-3 and the other RACE lanes run
  none. `RaceSitesStock` (SITES-2) and
  `RaceSitesEditor` (SITES-3) are R2's lanes' own categories, pinned by those lanes.
- Each host lists every category of its scene whose first test has landed when the host
  lands; a category whose first test lands later joins its host's comma list in that pull
  request, and after a host lands every new test in a category it hosts re-pins that host
  in its own pull request (section 6).
- **Proof:** the lane; pinned through `INTERIM_PIN_IDS` until its first flight, then whole.
- **Registers:** `MultiCategoryBatchWiringGroupTests.GROUP` (test_hlib.py:7807), catalog,
  autotest-status, D19 `ingame`.
- **Size:** M. **Depends on:** T-03, R1-10, R1-11; soft: R1-07 (the `logContract`), T-17,
  T-19, R2-03, R2-05, R5-11, R3W-05 (the rival-store accessor) and the first test of each
  other Space Center category (R1-03, R2-11, R3E-01, R3E-24, R4-02 (the ladder-against-stock
  cell of `SpaceRace`), R4-13: each joins RACE-9 when it lands). **PC:** yes.
- **Risk:** medium; every later test in a pinned category reds the pin until the spec is
  re-pinned in the same PR (cost in section 6).

### T-30 `ListHandles kind=board`

- **Change:** one line per milestone row: ladder id, holder(s), date, points, marks
  (unverified, tool-marked, blocked by no retakes), and a summary line with totals and the
  race's `testGame` flag (R1-23). T-40 extends the marks to every mark the board can carry.
- **Proof:** formatter tests, hlib.
- **Registers:** seam doc, hlib.
- **Size:** S. **Depends on:** R4-04, R1-23. **PC:** through T-32.
- **Risk:** low.

### T-31 "Same files, same board" (early half)

Split on 2026-11-15: this task keeps the base property on {T-16, T-18, R4-04}, so the
headless skeleton (MS-1c) closes at wave 14; the changed-file scenario and the keep rule
after the end date are T-37 (wave 18).

- **Change:** `Source/Parsek.Tests/Race/SameFilesSameBoardPropertyTests.cs`: a seeded
  hand-rolled generator (no new NuGet package) builds about 200 races with T-16 and runs
  them through T-18 with random arrival orders, calendar settings and installed-body sets;
  every machine's board is byte-equal. Each scoring task that lands after it (R4-05, R4-06,
  R6-01, R6-02, R6-03, R6-12) extends this generator with its own rule's inputs in its own
  pull request, so the property covers the whole board as it grows.
- **Proof:** the tests; a failing seed prints its schedule. Anti-vacuity: over the seed set,
  a count of boards with more than one rival and more than zero marks, red when below a
  pinned floor. A mutation cell: a test-only board builder that depends on arrival order
  (it keeps the first standing seen for a row) must red the property.
- **Registers:** none.
- **Size:** M. **Depends on:** T-16, T-18, R4-04. **PC:** no.
- **Risk:** low.

### T-32 Lane RACE-7-holder-change-folder-update

- **Change:** operator, seam-only. Start in a race staged from `holder-gen1`; `sync`; board shows
  rival P1 as holder (rival standings carry no local tool mark, so they score); the
  `raceFolder replace from=holder-gen2` step; `sync`; board shows P2 holder and the
  holder-change message line is logged once. Its `logContract` takes the board lines
  (OV-07): `[RaceBoard] Board computed` after each sync (R4-04), `Holder changed:
  milestone=Kerbin/Orbit` once (R4-07), and `[RaceSync] Message posted:` once (R3E-22's
  driver site posts it from `SyncResult.Messages` and logs that line, OV-38).
- **Proof:** lane; `ListHandles kind=board`.
- **Registers:** catalog, D19 `holder-change`, status.
- **Size:** S. **Depends on:** T-17, T-19, T-30, T-06, R4-07. **PC:** yes.
- **Risk:** low.

### T-33 Lane RACE-12-flown-rival-campaign (harvest by T-59)

Made smaller on 2026-11-15: the harvest tool is T-59's flag on the existing
`harvest_bdock_station.py`, not a new tool, and the flight is a measured campaign stage, not
the B2 shape (B2 has 1 PASS in 11 index rows and needs a pad craft a created race save lacks).

- **Change:** operator lane: a race overlay (T-48) on `cs-career-mc1` at slot 0; the CS-4
  orbit-and-return stage (`b4_reentry_career`, 3 of 3 PASS, p50 860 s) on day 3; commit,
  export. The player folder is harvested with T-59's `--race-folder` flag into
  `harness/fixtures/race/flown-<name>/`. In a test game its flights score, so the fixture
  serves import, ghosts and the board. When T-53 has landed, its RCA-1 is this flight and
  this lane is a spec on the same generator output.
- **Proof:** the lane; T-59's cells.
- **Registers:** catalog, fixtures README, status.
- **Size:** S. **Depends on:** R1-10, T-21, R3E-16, T-59. **PC:** yes.
- **Risk:** low.

### T-34 Lane RACE-13-paired-copies (optional; a spec on T-54)

Made smaller on 2026-11-15: the pairing machinery is T-54's; this task is its first spec.

- **Change:** operator lane, a `[pair]` of two specs (roles A and B) on two `stock-tree` slot
  copies sharing `automation/rs/<run>/<group>/` (T-54): both start in race overlays of one
  race (T-48), both export and sync for a few cycles with a `barrier` step before each sync;
  after a final barrier and sync, assert both boards equal (`ListHandles kind=board`) and the
  folder well-formed (T-21's facets). Order of arrival is free; only invariants are asserted.
- **Proof:** the lane.
- **Registers:** as any lane.
- **Size:** S. **Depends on:** T-54, T-21, T-30. Optional (question 3, assumed (a)).
  **PC:** yes (two slots).
- **Risk:** medium; it is the only proof of two live writers short of two PCs.

### T-35 Lane RACE-10-flight-batch (the FLIGHT host)

- **Change:** the FLIGHT host of T-29's table: one spec, RACE-10-flight-batch, that creates
  and joins its own race at slot 1 as RACE-9 does (R3W-09's pull request moves it onto a race
  staged from T-17's fixtures), enters FLIGHT with a
  vessel on the home pad and runs one `RunTests` over the FLIGHT race categories whose first
  test has landed (of `RaceSitesFlight`, `RaceFacts`, `RaceRivalLane`, `RaceRivalIsolation`,
  `SpaceRaceFlight`, `RaceLooksFlight`); a later category joins the comma list in the pull
  request of its first test.
- **Proof:** the lane; pinned through `INTERIM_PIN_IDS` until its first flight, then whole.
- **Registers:** `MultiCategoryBatchWiringGroupTests.GROUP`, catalog, autotest-status,
  D19 `ingame` (shared with RACE-9).
- **Size:** S. **Depends on:** T-29; soft: R3W-09 (moves it onto the staged race; its
  `RaceRivalLane` cells join then), R3W-30 (`RaceRivalIsolation` joins when it lands) and the
  first test of each other FLIGHT category (R2-09, R3E-08, R3E-13, R4-13, R5-09). **PC:** yes.
- **Risk:** medium, as T-29.

### T-36 Lane RACE-11-ts-batch (the TRACKSTATION host)

- **Change:** the TRACKSTATION host of T-29's table: one spec, RACE-11-ts-batch, that creates
  and joins its own race at slot 1 as RACE-9 does (R3W-20's pull request moves it onto a
  staged race), enters the Tracking
  Station and runs one `RunTests` over the Tracking Station race categories whose first test
  has landed (`RaceSitesTrackingStation` from R2 and `RaceFactsTrackingStation` from R3E-13
  exist before it; `RaceRivalLaneTrackingStation` and `RaceRivalIsolationTrackingStation`
  join in the pull requests of their first tests).
- **Proof:** the lane; pinned through `INTERIM_PIN_IDS` until its first flight, then whole.
- **Registers:** `MultiCategoryBatchWiringGroupTests.GROUP`, catalog, autotest-status,
  D19 `ingame` (shared with RACE-9).
- **Size:** S. **Depends on:** T-29; soft: R2-11 and R3E-13 (their Tracking Station cells
  join in their own pull requests), R3W-20 (moves it onto the staged race) and R3W-30 (their
  categories join when they land). **PC:** yes.
- **Risk:** low; a short batch in one scene.

### T-37 The changed-file scenario and the keep rule after the end date (late half of T-31)

- **Change:** `Source/Parsek.Tests/Race/TwoMachineChangedFileTests.cs` on T-18's simulator
  and T-31's generator: the 11 scenario (machine 1 saw the real standing and keeps it;
  machine 2 applies the changed one and shows the receipt disagreement; after the writer's
  heal (R3E-20) both boards agree), plus the keep rule after the end date (R3E-21, R6-03).
- **Proof:** the tests; a failing seed prints its schedule; a mutation cell (a test-only
  witness that forgets the first receipt) must red the disagreement cell.
- **Registers:** none.
- **Size:** S. **Depends on:** T-31, R3E-19, R3E-20, R6-03. **PC:** no.
- **Risk:** low.

### T-38 `raceParse` verifier row with the gated leak scan (early half of T-21)

- **Change:** the `raceParse` verifier row in `run.py` with spec block `[expectations.race]`,
  running T-05's leak scan over every race lane's produced save, GATED from the first lane
  (`PARSEK-FAIL(race-isolation)`); the structure facets join it later (T-21). A
  `mutation_check.py` mutator, `race-leak`, plants a rival id in a sidecar of an archived race
  run's save (through `mutsave`) and must turn the verdict red.
- **Proof:** a `_fake_ksp` smoke cell with a planted rival id in the produced save (red) and
  one without (green); the mutator's own cell; a cell that `race-isolation` is in hlib's
  verdict table.
- **Registers:** `harness/README.md` verifier list; `design-autotest-harness-core.md`
  verdict subkinds; `mutation_check.py`'s mutator list.
- **Size:** S. **Depends on:** T-03, T-05. **PC:** no.
- **Risk:** low; a scan that never reds is the risk, and the planted cell and the mutator
  are its proof.

### T-39 Lane RACE-6, first form: two saves exchange through the seam only

- **Change:** the first form of `RACE-6-two-saves-in-turn` (T-28 completes the same spec):
  save A is created (`RaceSetup action=create`, a test game), launches the Flea from the VAB
  and recovers it through the seam, commits and exports (R3E-04's and R3E-16's export lines,
  `RaceCommand status` shows the last export); A quicksaves before the flight. Save B joins
  (`as=`) and imports at Space Center entry (R3E-17's tick, R3E-18's import line;
  `RaceCommand status` shows A's latest seq). A loads the quicksave and exports: R3E-22's
  "1 shared flight left the race" is logged once (`[RaceSync] Message posted:`); B syncs and
  A's recording is gone from B's import. No ghost and no board line: those join with T-28.
- **Proof:** the lane; T-38's leak scan gated; a source-sync cell for each log literal the
  lane pins.
- **Registers:** catalog, D19 `two-saves`, status, tag `race`.
- **Size:** M. **Depends on:** R3E-17, R3E-18, T-03, T-04, T-38, R1-11. **PC:** yes.
- **Risk:** medium; it is the first lane in which a real exporter feeds a real importer, so
  it is where the exchange's first defects show.

### T-40 Lane RACE-14-scoring-marks; `kind=board` and `RaceCommand status` show every mark

- **Change:**
  - `ListHandles kind=board` (T-30) prints every mark the board can carry: uncounted parts
    and changed part values (R6-12), save marks (R3E-09), not scoring (R1-20), seen first
    (R6-02), history incomplete, after the end and arrived late (R6-03), cannot be watched,
    not checked here, tool-marked and debug-completed (R6-01). `RaceCommand status` adds the
    content-check state, the not-scoring state and the save marks.
  - T-17's writer gains the folders `rival-uncounted-part`, `rival-save-marked`,
    `rival-not-scoring` and `rival-tool-marked-normal-game` (a normal game, `testGame =
    False`, so its tool mark zeroes the row); `noretakes-seen-first` and `enddate-between`
    exist already.
  - Lane `RACE-14-scoring-marks`, operator, seam-only: start in each folder's race (overlay,
    T-48), `sync`, assert each row's mark and points with `kind=board`, and R6-07's header
    marks through `UiAction op=state` on the Space Race window.
- **Proof:** formatter tests of every mark; the lane.
- **Registers:** seam doc, hlib, catalog, D19 `scoring-marks`, status, fixtures README.
- **Size:** M. **Depends on:** T-17, T-19, T-30, R6-12, R6-02, R6-03. **PC:** yes.
- **Risk:** low.

### T-41 Lane RACE-15-content-check-launch-warning (stock-minimal)

- **Change:** lane `RACE-15-content-check-launch-warning` on `stock-minimal` (the tree
  differs, so R1-20's check warns): create with `unscoredConfirm=continue`, then build and
  launch a craft that carries a part outside the counted sets (a MechJeb2 part, which every
  profile installs; the exact part is checked against the pinned build when the spec is
  written): R1-22's launch warning shows (`UiAction op=dialog` names it, and its log line is
  pinned); answer Continue, fly, commit, `sync`; `RaceCommand status` reports the content
  differences and the flight's uncounted part.
- **Proof:** the lane; a source-sync cell for the warning's log literal.
- **Registers:** catalog, D19 `content-check`, status, tag `race`.
- **Size:** S. **Depends on:** R1-20, R1-22, R1-11, T-03, T-04. **PC:** yes.
- **Risk:** low; the one assumption is that a counted-set miss can be built from the
  profile's own parts.

### T-42 Committed race-save fixtures and the refused-load lanes RACE-16, RACE-17

- **Change:**
  - Two race saves made by RACE-1 (T-13; snapshotted through T-04) are harvested with
    `harvest_bdock_station.py --keep-parsek` into `harness/fixtures/saves/race-save-slot0`
    and `race-save-slot1`, with their shared folder under `harness/fixtures/race/`; the
    committed-set cell (`test_fixture_set_is_exactly_the_committed_set`) grows by two.
  - Lane `RACE-16-refused-load-modded-kerbin` on `modded-compat` (it loads Kopernicus): load
    the slot 1 save; R2-02's refusal (`kerbin-modified`) is logged, the race scenario stays
    inert and the save is not changed (design edge 24).
  - Lane `RACE-17-unscored-off-at-load` on `stock-minimal`: load a race save whose creator
    turned "allow play without scoring" off; the load is refused with R1-20's reason (edge
    32).
  - Optional cell `RACE-18-older-build-keeps-mark`: after `provision.py --parsek-dll` with
    an older build, the race save loads inert and keeps its mark (edge 22). It is flown only
    by hand, because a lane cannot provision.
- **Proof:** the lanes; the fixture cells.
- **Registers:** fixtures README, catalog, D19 `refused-load`, status.
- **Size:** M. **Depends on:** T-13, R2-02, R1-20. **PC:** yes.
- **Risk:** low; a fixture made on `stock-tree` and loaded on another profile is the point.

### T-43 Lane RACE-19-setup-screens: setup screens through the seam

- **Change:** UiAction coverage of R1's screens: the main-menu Multiplayer entry
  (`UiAction op=raise` and `op=state` from a MAINMENU seam step), the setup dialog's Create
  and Join pages and the content warning (`op=dialog` and `op=state` per page, field values
  read back), the Settings Multiplayer section (`op=state` keys); gallery mock states for
  the setup dialog (`UI/Gallery/` builders through the real `RaceSetupForm`). Lane
  `RACE-19-setup-screens`, operator, seam-only, walks them and creates one race through the
  dialog path, which must not carry `testGame` (R1-23).
- **Proof:** `TestCommandUiAction` token cells; `GuiCensusSeamVerbTests` mirror; the lane.
  The keyboard feel stays on the owner list.
- **Registers:** seam doc, hlib UiAction tables, `design-gui-state-gallery.md`, catalog, D19
  `setup-ui`, status.
- **Size:** M. **Depends on:** R1-15, R1-17, R1-18. **PC:** yes.
- **Risk:** medium; a stock `PopupDialog` at the main menu is new to the seam.

### T-44 Single-player negative contract and lane RACE-20-single-player-hosts

- **Change:**
  - A lane-health rule: no line with a race log tag (`[Race`, read from
    `scripts/arch/log_tags.json` by module) appears in any lane without the tag `race`; a
    planted-red cell (a fake run log with one race line under a non-race lane) must red it.
  - Lane `RACE-20-single-player-hosts`, operator, on a single-player save: hosts the race
    categories that have single-player halves, one `RunTests` step per category in that
    category's scene (`batchVacuityOptOut = true` with its reason): `RaceLooks` in
    SPACECENTER (R5-14's `SuitChoiceFreeInSinglePlayer`), `RaceFacts` in FLIGHT with a
    vessel on the home pad, as RACE-10 (R3E-08's and R3E-13's single-player cells, which
    start their own recording), `RaceFactsTrackingStation` in TRACKSTATION (R3E-13's).
    The cells that need a race save skip there with their stated reason; the single-player
    halves run. R5-11's log contract (no `[RaceLooks]` line) is the lane's `logContract`.
    Each step joins the lane in the pull request of its category's first test. RACE-20 pins
    those three categories beside RACE-9, RACE-10 and RACE-11, so a cell added to one of
    them re-pins RACE-20's tally with its race host's.
- **Proof:** the rule's cells; the lane.
- **Registers:** `harness/README.md` lane health, catalog, status.
- **Size:** S. **Depends on:** R1-04, R1-06. **PC:** yes.
- **Risk:** low.

### T-45 Race lane plumbing: `race` tag, `parallel_runner --tag`, CI push on `multiplayer`

- **Change:** the tag convention (every race spec carries `race`; an hlib cell reds a spec
  whose id prefix, the part before the first `-`, is `RACE`, `SITES`, `RCA`, `RCB` or `RCI`
  without it, and a non-race spec with it; the whole prefix, so a `SITESCAN-1` is not a race
  lane: `hlib.race_tag_problem`, `test_hlib.RaceTagConventionTests`); `parallel_runner.py
  run --tag <t>` (the `run` subparser, `parallel_runner.py:1231-1247`, had no tag filter;
  `run.py --tag`, `run.py:6377`, matches through `hlib.select_scenarios`, which
  `parallel.resolve_selection` reuses; a tag no spec carries refuses like an unknown tier);
  the `push:` trigger for the `multiplayer` branch in `.github/workflows/tests.yml` (README
  rule 10; the metrics step and job keep their own `refs/heads/main` conditions).
- **Proof:** hlib and `parallel` cells; the CI run on the first push to the branch.
- **Registers:** `harness/README.md` parallel runs.
- **Size:** S. **Depends on:** the owner's go. **PC:** no.
- **Risk:** low.

### T-46 Sites tolerance comparator (`ListHandles kind=sites ... tolerance=`)

- **Change:** `ListHandles kind=sites expectSite=<slot> tolerance=<m>`: compares the live
  values with the site table row (R2-01) within the tolerance and prints one `ok` or
  `mismatch` per fact, so SITES-1 and SITES-2 compare with tolerances rather than by digest
  (`ListHandles` has only `expectDigest` today, `hlib.py:3029-3035`).
- **Proof:** formatter and boundary cells (at, just inside and just outside the tolerance);
  `ListHandlesSourceSyncTests` grows.
- **Registers:** seam doc, hlib.
- **Size:** S. **Depends on:** T-07, R2-01. **PC:** no.
- **Risk:** low.

### T-47 Rival fixture content for hosted cells

- **Change:** T-17's writer gains rival folders the hosted in-game cells need: a vintage EVA
  rival (R5-09), an unknown-part rival (R3W-21), a landed vessel and an orbit leftover, a
  look change at a later standing (edge 29), and a scale folder of 3 rivals x 200 flights
  (R3W-32). Each is written through T-16 and kept current by T-17's test.
- **Proof:** T-17's currency test; each folder reads back through the production reader.
- **Registers:** fixtures README.
- **Size:** M. **Depends on:** T-16. **PC:** no.
- **Risk:** low; the scale folder is the largest committed fixture of the race work, so its
  size is checked when it is written.

### T-48 Race overlay at staging (`[fixture.raceOverlay]`)

- **Change:** `[fixture.raceOverlay] from = "fixtures/race-overlay/<name>" slot = N`.
  `run.py::stage_fixture` splices the overlay's `ParsekRaceScenario` node into the staged
  `persistent.sfs` and its `Parsek/Race/` files into the staged save, resolving the folder
  path to `${raceShared}`; the committed fixture is never touched (the `savepatch` rule:
  staged copy only). Overlays are written by an env-gated xUnit writer through the
  production writers (R1-03's `RaceLinkCodec.Write`, R1-10's manifest writers, R1-11's join
  plan) and are byte-gated like T-17. This writer is the second of the two test tools that
  may make a test game (R1-23): it sets `RaceCreateRequest.TestGame` through its `internal`
  setter (the test project is a friend assembly) and calls the same manifest write codec,
  so every overlay carries `testGame = True`; no player-reachable path writes one. Player A takes
  slot 0, the stock KSC where the campaign fixtures were flown, so no campaign pin moves.
- **Proof:** hlib validator cells; `test_run_smoke` (spliced, snapshotted, the committed
  fixture byte-equal after the run); the overlay currency test.
- **Registers:** `harness/README.md` fixtures section; `fixtures/race-overlay/README.md`.
- **Size:** M. **Depends on:** T-03, R1-11, R1-12, R1-23 (the key and the writer parameter).
  **PC:** no (rides T-53).
- **Risk:** medium; it is a new patch class beside `savepatch`'s FLIGHTSTATE-only one, so
  its boundary (only the race node and `Parsek/Race/`) is argued and pinned by a cell.

### T-49 Campaign chain runner

- **Change:** `harness/tools/campaign_runner.py` over a pure `harness/lib/campaign.py`: flies
  a declared ladder of lanes in order; each lane's template is the previous lane's snapshot
  (a new `run.py --stage-from <dir>` flag, accepted only from the runner), harvested into an
  uncommitted chain under `results/campaign/<name>/`; it resumes from the last green stage and
  pipelines two chains on two slots through the parallel runner's dispatcher. It is also the
  single-player roadmap's CS-ALL runner and its stage-to-fixture promotion tool
  (`autotest-roadmap.md:6555`, `:6560-6562`): one task serves both programs. It needs no
  race code.
- **Proof:** `test_campaign.py` (ladder parse, resume point, the two-chain pipeline schedule,
  refusal of `--stage-from` outside the runner); one replay of the CS chain CS-3 to CS-4b
  (about 1413 s of p50) on the owner's PC.
- **Registers:** `harness/README.md` (a "Campaign chains" section), status.
- **Size:** M. **Depends on:** the owner's go. **PC:** lanes (one CS chain replay).
- **Risk:** medium; it chains stages through snapshots, so a stage that leaves a bad save
  fails the next one; the resume point keeps a rerun short.

### T-50 Race campaign spec generator (RCA-k / RCB-k)

- **Change:** `harness/tools/build_race_campaign.py`: writes the `RCA-k` and `RCB-k` specs
  from the CS career specs and a schedule table (player, slot, `WarpToUT` before each launch,
  the race steps `RaceCommand action=sync` after load and after each commit, `ListHandles
  kind=rivals` and `kind=board`, the `[fixture.race]` and `[fixture.raceOverlay]` blocks),
  and later the RCI variants of T-55. The schedule table also names, per lane, the last
  step whose race assertions gate (T-53 gates `RCA-2` only to its sync head until T-56 and
  T-57 arm the rest). The output is regenerated and byte-gated
  (`test_race_campaign.py`) and the lane catalog regenerated with it. The schedule row that
  moves player B's overlay to slot 2 is added by whichever of R2-12 and this task lands
  second (normally this task; section 4b, stage S3).
- **Proof:** `test_race_campaign.py` (each generated spec validates and equals the committed
  copy; a CS spec change reds until regenerated).
- **Registers:** `harness/README.md`, catalog.
- **Size:** M. **Depends on:** T-03, T-12, T-19, T-30, T-48. **PC:** no.
- **Risk:** low; specs are generated, never hand-copied.

### T-51 Board oracle and its mutation checks

- **Change:** `raceparse.board_oracle`: the expected holder per ladder row from each player's
  captured stock award lines (`hlib.MILESTONE_AWARD_LINE_RE`, `hlib.py:11289`) and a ladder
  mirror kept equal to R4-02's table by a source-sync cell, diffed against the `ListHandles
  kind=board` lines. It never reads a Parsek number (the ledger oracle's rule, `oracle.py`).
  Verifier row `raceBoardOracle`, report-only until armed after two reading runs; a
  `mutation_check.py` mutator plants a wrong holder and must red it.
- **Proof:** `test_raceparse.py` oracle cells; the mutator cell.
- **Registers:** `harness/README.md` verifier list; harness-core verdict subkinds.
- **Size:** M. **Depends on:** T-21, T-30, R4-02. **PC:** no.
- **Risk:** low; holders are exact only when rival dates for one row are a Kerbin day apart,
  which the schedule guarantees (section 4b).

### T-52 Real-ledger board, headless

- **Change:** `Source/Parsek.Tests/Race/RealLedgerBoardTests.cs`: reads the campaign
  fixtures' ledgers (`cs-career-mun1`, `cs-career-mun4`, `C2CareerPostFix`; the
  `AnchorEpsilonRebuildClockTests.cs:124` precedent for reading `harness/fixtures/saves`),
  builds R3E-11's `MilestoneTable` from each, writes standings through T-16 and computes the
  board with R4-04; a pure UT shift of one copy makes firsts trade. Pins the holder per row
  and the points. R3E-11's and R4-04's proofs name these cells (real milestone tables, not
  only synthetic rows).
- **Proof:** the tests.
- **Registers:** none.
- **Size:** S. **Depends on:** R3E-11, R4-04, T-16. **PC:** no.
- **Risk:** low.

### T-53 First change of hands: lanes RCA-1, RCB-1, RCA-2 (stages S1, S2)

- **Change:** the first flight of three lanes T-50 generates (no spec is hand-written here),
  on twin overlays of `cs-career-mc1` (A at slot 0, B at slot 1, T-48), flown in turn on
  one install: `RCA-1` (A flies CS-4 on day 3 and exports), `RCB-1` (B syncs, sees A's rows
  on the board, flies CS-4 on day 2, exports, and holds ReachedSpace, Kerbin/Orbit and
  Kerbin/ReturnFromOrbit), `RCA-2`. `RCA-2` has one definition, T-57's (sync, then CS-4b
  and CS-5 on day 5): this task asserts on its sync head, R4-07's `Holder changed:` line
  once per row, and may end the lane's gating there; the rest of the lane flies, and its
  race assertions are armed when T-56 (MS-8) and T-57 (MS-10) need them, through T-50's
  schedule table (the column naming each lane's last gated step), so arming is a table edit
  and a regeneration, never a second spec. `RunTests LedgerGroundTruth` in each. From T-20
  on, `ListHandles kind=rivals` in flight shows the other's ghost (stage S2); before it the
  lanes fly without that step. The folder passes from lane to lane through T-59's harvest.
- **Proof:** the lanes; T-38's leak scan gated; the board rows exact (section 4b) up to
  RCA-2's sync head.
- **Registers:** catalog, D19 `campaign`, status, tag `race`.
- **Size:** M. **Depends on:** T-48, T-59, T-19, T-30, R4-04, T-50 (generates the three
  specs); soft: T-20 (`kind=rivals` steps), R4-07 (the holder-change line). **PC:** yes
  (three lanes from the CS p50s: 860 + 860 + 1195 = 2915 s).
- **Risk:** medium; CS-4 is 3 of 3 PASS, but its funds and science margins were measured at
  site 0 and B flies first.

### T-54 Paired lanes in the parallel runner

- **Change:** a `[pair] group = "<id>" role = "A" | "B"` spec block; `parallel.decide_next`
  dispatches both or neither (two free slots of the profile and the memory floor for two
  copies); a shared folder `<umbrella>/automation/rs/<run>/<group>/` (inside the harness
  boundary, `harness/README.md:110-111`), wiped at dispatch under the runner's machine lock
  and passed to each child as a `run.py` argument, accepted by T-03's validator as
  `sharedFolder = "pair"`; a harness-side `{ harness = "barrier", args = { name, timeout } }`
  step on T-06's grammar, its files in `results/parallel/<run>/pairs/<group>/`, never in the
  shared folder; a partner that ends or times out reads `INVALID(partner-lost)` on the
  survivor, flake-exempt and listed apart from the machine refusals. No new lock.
- **Proof:** `parallel` and hlib cells (both-or-neither, barrier order, partner lost); one
  paired flight on the owner's PC (T-34 is its first spec).
- **Registers:** `harness/README.md` parallel runs; harness-core verdict subkinds.
- **Size:** M. **Depends on:** T-03, T-06. **PC:** lanes (one pair).
- **Risk:** medium; pairing is new to the runner, and the second `stock-tree` copy needs
  `provision.py --profile stock-tree --copy 2` once.

### T-55 Race integration variants (lanes RCI-1 to RCI-9)

- **Change:** generated race variants (T-50) of single-player lanes that stress what
  multiplayer could disturb, each on a slot-0 overlay of its own host with a rival folder
  staged (B's harvested folder, or T-17's `rival-station`), the same pins, plus a gated leak
  scan, a `sync` after load and after each commit, and one extra race assertion (section
  4b). T-05's `save_equivalence` compares each variant's produced save with its
  single-player lane's snapshot under the pinned mask.
- **Proof:** the nine lanes (about 0.63 h: the ten-lane estimate of 2520 s at its 252 s
  average per lane, nine lanes = 2268 s; each first flight replaces it).
- **Registers:** catalog, D19 `integration`, status, tag `race`.
- **Size:** M. **Depends on:** T-48, T-50, T-21, T-19; soft: T-20. **PC:** yes.
- **Risk:** medium; the RW lanes are the most defect-dense single-player lanes, so a red here
  may be a single-player defect first; it is filed as such.

### T-56 Retake, no retakes, Re-Fly against a rival (stage S4)

- **Change:** hand-written lanes on twin overlays of `cs-career-mun1`: `RCA-3r` / `RCB-3r` (A
  takes Mun/Orbit on day 9; B on day 10 rewinds to a day-8 launch with
  `InvokeRewindToLaunch` and re-flies to orbit: B holds by the earlier date); `RCA-3n` /
  `RCB-3n` (the same moves in a `noRetakes = on` race: A holds, B's row is marked "seen
  first"); `RCB-3f` / `RCA-3f` (B opens a Re-Fly of the return stage after A's sync, `sync`
  during the session exports nothing (`exported=0`), merge, A sees the replacement).
- **Proof:** the lanes; the board rows exact; T-51's oracle report-only.
- **Registers:** catalog, D19 `retake`, `no-retakes`, `refly`, status, tag `race`.
- **Size:** M. **Depends on:** T-53, R6-02, R4-07, T-20. **PC:** yes.
- **Risk:** medium; the rewind and the Re-Fly are the paths most likely to find a defect.

### T-57 Both careers to the Mun (stage S5)

- **Change:** the full generated ladders (T-50) flown by T-49's runner on two slots, in
  turn and pipelined (A to CS-7, B to CS-6c; section 4b): every stage syncs, every B stage
  runs T-51's board oracle (armed after two reading runs). From R6-03 on (soft), the race is
  set with an end date just after B's last stage and the last lane checks the final board.
- **Proof:** the campaign run; the oracle armed.
- **Registers:** status; `harness/README.md` campaign chains.
- **Size:** S (the specs are generated). **Depends on:** T-49, T-50, T-51, T-56, R2-12;
  soft: R6-03 (the end-date step). **PC:** flight (about 2.24 h on two slots to RCA-5's end,
  then RCB-5; section 4b, "How the two run").
- **Risk:** medium; CS-7's landing is the flakiest stage (3 PASS, 35 INVALID in the index);
  only A flies it, with `[retry] once`.

### T-58 True new race from the seam and an accept-by-type selector (optional)

- **Change:** a stage 0 that creates a race with `RaceSetup action=create` (a test game) and
  harvests it like CS-0, plus a `KscAction accept-contract contract=offered:<type>` selector
  (owed by the single-player roadmap, row CS-3) and GUID-free pins in T-50's output, so a
  campaign can start from a race created by the seam rather than an overlay.
- **Proof:** the stage-0 lanes (`RCA-0`, `RCB-0`); selector cells.
- **Registers:** seam doc, hlib, catalog, status.
- **Size:** S. **Depends on:** T-50, R1-10, R1-11. Optional. **PC:** yes.
- **Risk:** low.

### T-59 Race harvest flag on `harvest_bdock_station.py`

- **Change:** `harvest_bdock_station.py --race-folder <results/<runId>_race> --race-name <n>`
  writes `harness/fixtures/race/<n>/` beside the save fixture, prunes the rebuildable
  `Parsek/Race/rivals/` caches from the harvested save (they would put rival `.prec.txt`
  files where `OptimizerTransferCohesionTests.cs:521-530` globs), and verifies the chain and
  payload with `raceparse` (T-21's facets when they exist). Its docstring already says a
  further forge reuses it rather than forking it. Replaces T-33's own tool. The harvested
  player folders are design 15.10's recorded opponents, R8's opponent source.
- **Proof:** cells of the flag (copy, prune, refuse a folder outside `results/`).
- **Registers:** `harness/README.md` fixtures section.
- **Size:** S. **Depends on:** T-03, T-05; soft: T-21 (chain facets). **PC:** no.
- **Risk:** low.

## 4b. The two scripted players

The owner asked (2026-11-15) to reuse the existing flight tests and missions, the scripted
career that lands on the Mun, to automate one player, to build a similar playthrough for a
second player at another site, to run the two careers side by side and to use them to test the
multiplayer systems and their integration. This section is that track. It is not one late
milestone: each stage joins a milestone's exit proof (README "Milestones").

### Player A: the career campaign on a race overlay

The campaign is ten operator lanes on `stock-tree`, each LIVE-PROVEN, chained by harvested
fixtures (`autotest-roadmap.md:6513-6565`):

| Stage | Host fixture (in) | What it does | Out | p50 s |
|---|---|---|---|---|
| CS-0 | fresh-career | `NewGame` career, Normal | cs-career-new | 48 |
| CS-3 | cs-career-new | two contracts by GUID; Flea hop with readings; recover | cs-career-contracts | 422 |
| CS-3b | cs-career-contracts | hire, Mission Control level 1, basicRocketry | cs-career-mc1 | 65 |
| CS-4 | cs-career-mc1 | two nodes; orbit and return (`b4_reentry_career`) | cs-career-orbit | 860 |
| CS-4b | cs-career-orbit | Tracking Station and Launch Pad level 1, two nodes | cs-career-ts1 | 66 |
| CS-5 | cs-career-ts1 | Mun flyby and return | cs-career-mun1 | 1129 |
| CS-6 | cs-career-mun1 | Mun orbit and return | cs-career-mun2 | 1000 |
| CS-6b | cs-career-mun2 | Astronaut Complex level 1, advRocketry | cs-career-mun3 | 70 |
| CS-6c | cs-career-mun3 | Mun science run | cs-career-mun4 | 2342 |
| CS-7 | cs-career-mun4 | landing, EVA, flag, ascent, return | (none) | 2414 |

Player A runs these stages on a race overlay (T-48) at slot 0, the stock KSC where every
fixture was flown, so every pin stays valid. Stages CS-0 to CS-3b earn no ladder row (the
Flea hop reaches no ladder milestone and `FirstLaunch` is off the ladder, R4-02), so the race
starts on overlays of `cs-career-mc1`. Every stage already ends with `RunTests
LedgerGroundTruth`, the strongest "a rival does not leak into my ledger" proof there is, and
keeps it in the race.

### Player B: the same specs, re-hosted and re-timed

Player B flies the same missions, craft and research order on its own chain of fixtures, at
another site and on another schedule. T-50 generates the `RCA-k` and `RCB-k` specs from the
CS specs and a schedule table; nobody hand-copies a spec. Both start on twin overlays of one
fixture, so the same contract offers and applicants hold for both (contracts are local and
never exported; recording ids and launch guids are fresh per flight, so nothing collides).

- Before R2 lands, B is slot 1 and still launches from the stock KSC (slots exist from
  R1-09; the move is R2-04).
- From MS-2 on, B's overlay names slot 2: R2-12's site moves B, and whichever of R2-12 and T-50
  lands second adds the schedule row. The specs do not change. B's science and funds margins
  are re-read on B's first flights at the moved site (Kerbin science per biome, spike S2;
  recovery value by distance to the moved KSC); if a node or upgrade is refused, B's schedule
  adds one cheap science hop, never an edit to A's.

### Which firsts trade

The schedule is the knob (design 15.10: "difficulty is the schedule"). A Kerbin day is
21,600 s; rival dates for one ladder row are at least a day apart.

| Ladder rows | Stage | Who first (race day) | Holder | What it exercises | Lanes |
|---|---|---|---|---|---|
| ReachedSpace, Kerbin/Orbit, Kerbin/ReturnFromOrbit | CS-4 | B (2), A (3) | B | the first change of hands; A sees "B took" on its next sync | RCA-1, RCB-1, RCA-2 (T-53) |
| Mun/Flyby, Mun/ReturnFromFlyBy | CS-5 | A (5), B (7) | A | the holder swings back | RCA-2, RCB-2 (T-57) |
| Mun/Orbit | CS-6 | A (9); B on day 10 rewinds to a day-8 launch and re-flies | B, by rewind | retake by rewind (RW-15's shape on a ladder row) | RCA-3r, RCB-3r (T-56) |
| Mun/Orbit, a second race with `noRetakes = on` | CS-6 | as above | A (B "seen first") | no retakes | RCA-3n, RCB-3n (T-56) |
| Mun/ReturnFromOrbit | CS-6 | B opens a Re-Fly of the return after A's sync | by dates | nothing exported during a Re-Fly; a merge replaces a shared flight | RCB-3f, RCA-3f (T-56) |
| Mun/Landing, surface EVA and flag, Mun/ReturnFromSurface | CS-7 | A only (B stops at CS-6c) | A | a rival flag and leftover; keeps B off the flakiest stage | RCA-5 (T-57) |

T-57's full race, KSC-only stages folded into the next flight lane: RCA-1 (CS-4, day 3),
RCB-1 (sync, CS-4, day 2), RCA-2 (sync, CS-4b, CS-5, day 5), RCB-2 (CS-4b, CS-5, day 7),
RCA-3 (CS-6, day 9), RCB-3 (CS-6, day 10), RCA-4 and RCB-4 (CS-6b, CS-6c), RCA-5 (CS-7),
RCB-5 (the last sync and the board oracle).

### How the two run

- **In turn, one install (primary).** Lanes alternate A1, B1, A2, B2 and so on; each stages
  its player's latest save and the folder harvested from the previous lane of either player.
  Deterministic, no rendezvous, every existing harness rule holds (one save, one instance,
  `${raceShared}` inside it). About 3.7 h serial for both chains as planned, from the CS
  p50s: A on CS-4 to CS-7, 860 + 66 + 1129 + 1000 + 70 + 2342 + 2414 = 7881 s; B on CS-4 to
  CS-6c, 7881 - 2414 = 5467 s; 7881 + 5467 = 13348 s = 3.71 h.
- **Pipelined on two slots (the same lanes, less wall time).** B-k needs A-k's folder, but
  A-(k+1) can read B's folder one stage late, so T-49 keeps two `stock-tree` slots busy:
  over T-57's five lane pairs (KSC stages folded into the next flight lane) RCA-5 ends at
  8076 s = 2.24 h (A's 7881 s plus 195 s that RCA-4 waits for RCB-2), then RCB-5's last
  sync and oracle. The parallel runner's rules hold (one DLL,
  memory guard, two slots while the owner works); the second copy needs `provision.py
  --profile stock-tree --copy 2` once (ports 50020 and 50022 are already distinct).
- **At once, sharing one folder (way E).** T-34 on T-54's paired lanes: both copies write
  `automation/rs/<run>/<group>/`, a barrier before each sync. Only invariants are asserted.

### What is exact and what is windowed

- **Exact:** the holder per ladder row; the order of holder changes and each `Holder
  changed:` line once; points per player in twelfths; the set of rival recording ids
  imported; "seen first" blocks; `exported=0` during an open Re-Fly; no rival id outside
  `Parsek/Race/` (the leak scan); `LedgerGroundTruth hardFailures=0`.
- **Windowed:** every game date. A launch follows a `WarpToUT` to a scheduled UT and the warp
  lands near it; flight time varies by minutes; a Mun transfer waits up to about one LKO
  period for its window. A date lies in `[scheduled UT, scheduled UT + the stage's budget]`,
  so holders are exact because rival dates for one row are a Kerbin day apart.
- **Never live:** a same-second co-holder (equal UT to the second). It stays headless
  (R4-04's tests, T-52).
- **Concurrency (way E):** only invariants: both boards equal after a final barrier and sync,
  and the folder well-formed (T-21's facets).

### The staged path

| Stage | What it proves | Tasks | Wave | Joins the exit proof of |
|---|---|---|---|---|
| S0 | a campaign fixture loads as a race save at slot 0 and slot 1 (gate on, `RaceCommand status`, `testGame=True`); the chain runner replays CS-3 to CS-4b | T-48; T-49 | 8 (headless: T-48's hlib validator and `test_run_smoke` staging cells; first flown in T-53's lanes, wave 16); 0 | MS-1b (overlay), MS-1a (runner) |
| S1 | one first changes hands, no ghosts (RCA-1, RCB-1, RCA-2 to its sync head) | T-53 (T-50, T-59) | 16 | MS-1d |
| S2 | each player sees the other's ghost (`kind=rivals` in flight) | T-53 with T-20 | 16 | MS-1d |
| S3 | B flies from another site (slot 2) | R2-12's schedule row in T-50 | 16 (with or after T-53) | MS-2 |
| S4 | retake by rewind, no retakes, Re-Fly against a rival | T-56 | 17 | MS-8 |
| S5 | both careers to the Mun, the board oracle armed | T-57 (T-49, T-50, T-51) | 18 | MS-10 |

S3 has no task of its own; it flies the change-of-hands lanes again from slot 2, with or after
T-53 (wave 16) with MS-1d's proof, and is signed off with MS-2 (milestones are signed off in
ladder order). T-52, the headless real-ledger board, lands at wave 14, two waves before S1, so
the first live change of hands has a headless twin.

### The integration suite

From S2 on the same pair doubles as the integration suite: race variants (T-55) of the
single-player lanes most likely to be disturbed by multiplayer, each on a race overlay of its
own campaign host with a rival folder staged (B's harvested folder, or T-17's
`rival-station`), the same pins, plus a gated leak scan, a `sync` after load and after each
commit, and one extra race assertion. "Does not disturb" is T-05's `save_equivalence`
between each variant and its single-player lane's snapshot under the pinned mask;
"interacts correctly" is the extra column. `LedgerGroundTruth` runs in every one of them, as
in every campaign stage.

| Lane | Single-player lane | Host | Extra race assertion |
|---|---|---|---|
| RCI-1 | RW-15-earlier-milestone | cs-career-mun4 | `MILESTONES` lists both dates; the board takes the earlier |
| RCI-2 | RW-3-save-reload-takeback | cs-career-mun4 | "shared flights left the race" once; the next standing drops the flight |
| RCI-3 | RW-5-rewind-refly-discard | cs-career-mun4 | `sync` during the session exports nothing (`notReady`) |
| RCI-4 | RW-6-refly-science-discard-merge | cs-career-mun4 | after the merge the superseded recording is absent from the standing |
| RCI-5 | RW-11-refly-cheap-debris-recovery | cs-career-mun4 | the ledger is unchanged by the rival |
| RCI-6 | RW-25-refly-recover-pressed | cs-career-mun4 | the ledger is unchanged by the rival |
| RCI-7 | RW-18-ksc-warp-across-committed-future | cs-career-mun4 | rival ghosts do not cross into the local timeline (no ledger row, no vessel) |
| RCI-8 | SWS-1-save-switch-through-main-menu | cs-career-mun4 | race save, single-player save, race save: gate on, cleared, on |
| RCI-9 | H79-route-rewind-to-launch-career | rover-route-recorded-career (`stock-minimal`; the lane answers R1-20's warning with `unscoredConfirm=continue`, since its race assertion needs no score) | the route event log is never exported (leak scan over `ROUTE_EVENT_LOG`) |

RF-21-refly-sibling-crosses-mun-soi, once listed here, is dropped: its host
`gs2-orbital-stack` is a Sandbox save on `stock-minimal`, so it cannot carry a career race
overlay, and no lane on a `cs-career` fixture re-flies a sibling across an SOI. Re-Fly against
a rival stays covered by RCI-3, RCI-4 and T-56's `RCB-3f` / `RCA-3f`.

### Blockers and the task that removes each

| Blocker (evidence) | Removed by |
|---|---|
| `stage_fixture` stages exactly one template (`run.py:1879-1882`), so a lane holds one save | each player stage is its own lane, chained by T-49; no `run.py` change beyond `--stage-from` |
| The chain is harvested by hand (`autotest-roadmap.md:6560-6562`) | T-49 (save chain) and T-59 (folder) |
| Contract GUIDs and an applicant name are pinned off one new game (`CS-3b-career-hire-upgrade.toml:92-97`, `:31-32`) | twin overlays of one fixture keep every pin (T-48); a new race from the seam needs T-58's selector |
| A save made by `RaceSetup` has no pad craft, and the shared-ships overlay is keyed by the staged template leaf (`fixtures/shared-ships.toml` header) | the Flea from the VAB for a created save (T-28, T-39); the overlay keeps the template leaf (T-48) |
| `local.cfg` holds the folder path of the instance the stage was flown in | T-48 re-points it at staging; T-12's `set-folder` is the in-game fallback |
| Rival caches in a harvested save would be read as own recordings (`OptimizerTransferCohesionTests.cs:521-530` globs every `*.prec.txt` under `fixtures/saves/`) | T-59 prunes `Parsek/Race/rivals/` |
| The parallel runner has no pairing (no pair concept in `harness/lib/parallel.py`) and no tag filter (`parallel_runner.py:1230-1243`) | T-54; T-45 |
| Harness flights were to be tool-marked, yet R3E-08 marks a recording only while a tool is used (`r3-export-and-exchange.md:563-572`) and r4 derives flight scoring from recording marks only (`r4-r6-board-and-rules.md:1097-1101`) | R3E-08's rule for a save armed from boot; R1-23's test game; R6-01's skip of the test-tools mark in a test game |
| CS-7's landing: 3 PASS and 35 INVALID in the run index | B stops at CS-6c; A keeps `[retry] once` (T-57) |
| The CS lanes pin `site=LaunchPad`; a moved site must keep that name (`mlib.py:17708` defaults to "LaunchPad") | spike S4a and R2-04 keep the stock names at a moved site |

## 4c. What is reused

| Existing thing | Where the race work uses it | Task |
|---|---|---|
| The career campaign CS-0 to CS-7 and its fixtures `cs-career-*` | player A as is; player B re-hosted; the integration hosts | T-48, T-50, T-53, T-55, T-56, T-57 |
| `science_bench_recover` and the stock Flea from the KSP root `Ships/` (`TestCommandEditorRoute.cs:214-238`) | the first flight of a created race save | T-28, T-39 |
| `b4_reentry_career` (CS-4, 3 of 3 PASS) | the first change of hands; the flown rival fixture | T-33, T-53 |
| `harvest_bdock_station.py` | the race harvest, one new flag | T-59 |
| `savepatch.py`'s staged-copy rule | the overlay splice touches only the staged copy | T-48 |
| `build_career_earned_pad.py` and its byte gate | the overlay and folder currency tests | T-17, T-48 |
| `oracle.py` (M-B2) and `hlib.MILESTONE_AWARD_LINE_RE` (`hlib.py:11289`) | the board oracle from stock award lines | T-51 |
| `mutsave.py` / `mutation_check.py` | proof that the leak scan and the oracle bite | T-38, T-51 |
| `ledger_audit.py` and `RunTests LedgerGroundTruth` (53 lanes) | a rival leak shows as a pool mismatch, in every stage | T-53, T-55, T-56, T-57 |
| The RW rewind lanes on `cs-career-mun4`, SWS-1, H79 | the integration variants | T-55 |
| `InvokeRewindToLaunch`, `AnswerMergeDialog`, `WarpToUT` | retake by rewind, Re-Fly against a rival, the schedule | T-26, T-56 |
| `C2CareerLedgerReplayTests` / `CareerSaveParser` and `AnchorEpsilonRebuildClockTests.cs:124` | a headless board over real flown ledgers | T-52 |
| `parallel_runner.py`, `lib/slots.py` ports | pipelined chains; paired lanes | T-49, T-54 |
| `run_index.py lanes-for --module` | a milestone exit proof as one query ("every lane that touches RaceSync is green on this DLL") | README rule 11 |
| `lane_catalog.py` and its currency cell | every generated race spec | T-50 |
| `run.py --tag` | the `race` tag | T-45 |

## 5. Parallel tracks

- **Track H (harness, Python):** T-03, T-04, T-05, T-06, T-38, T-59, T-45, then T-21,
  T-48, T-51. Hot files: `harness/run.py`, `harness/lib/hlib.py`,
  `harness/lib/test_hlib.py`. T-03 and T-04 both touch `stage_fixture` and the snapshot code,
  so T-04, T-06 and T-48 land after T-03, one at a time.
- **Track R (runners, Python):** T-49 (day one), T-54, then T-50 and the generated lanes.
  Hot files: `harness/lib/parallel.py`, `harness/tools/parallel_runner.py` (T-45, T-49 and
  T-54 all touch it: land them one at a time).
- **Track S (test project, C#):** T-01, T-02, then T-10, T-16, T-17, T-47, T-18, T-31, T-52,
  T-37. No shared hot file except `Source/Parsek.Tests/Generators/`, where T-01 and T-16 add
  files only, and T-17's writer, which T-40, T-47 and T-48's overlay writer extend.
- **Track V (seam verbs):** T-07, then (after R1-08, R1-10 and R1-11 land their actions,
  OV-48) T-12, T-19, T-20, T-30. Hot files: `TestCommandVerbs.cs`,
  `TestCommandRaceCommand.cs`, `TestCommandListHandles.cs`,
  `design-autotest-command-seam.md`, the hlib verb tables. Land them one at a time, in the
  order R1-08, R1-10, R1-11, T-12, T-19.
- **Track L (lanes):** each lane is its own spec, but every lane edits
  `docs/dev/flight-profiles.md` (regenerated), `harness/coverage/registry.toml`,
  `docs/dev/autotest-status.md` and, for batch lanes, `INTERIM_PIN_IDS` and `GROUP` in
  `test_hlib.py`. Two lane PRs at once will conflict in those files; regenerate the catalog
  after merging `origin/multiplayer` into the sub-branch (README rule 10), never splice it.
- Day one, eight agents can run T-01, T-02, T-03, T-04, T-05, T-07, T-45 and T-49 at once;
  T-06, T-38, T-54 and T-59 follow on T-03 and T-05.

## 6. Tests and proof

### Headless (xUnit, run by `scripts/cloud-test.sh` in CI)

| Test | Proves |
|---|---|
| `SaveWriterDriverDeterminismTests` (T-01) | The save writers are deterministic per process and culture, the premise of every byte-identity test |
| `FakeSyncedStoreTests` (T-02) | The fake store behaves as specified and replays by seed |
| `SinglePlayerInertnessTests` (T-10) | 15.8: a single-player save's output is byte-identical with the feature compiled in |
| `RaceFolderBuilderTests` (T-16) | The generator writes only what the production reader accepts, and each fault is caught for its stated reason |
| `RaceFixtureFoldersAreCurrentTests` (T-17) | Committed folders are what the builder writes today |
| `RaceSyncSimulatorTests` (T-18) | Sync rules under arrival order, outage, partial files, placeholders, conflict copies, foreign edits, late join; R3E-19's `RaceSyncLoopbackTests` run on the same simulator |
| `SameFilesSameBoardPropertyTests` (T-31) | 11's determinism across machines, with anti-vacuity and a mutation red |
| `TwoMachineChangedFileTests` (T-37) | 11's tamper scenario and the keep rule after the end date |
| `RealLedgerBoardTests` (T-52) | The board over real flown ledgers of the career campaign, firsts traded by a UT shift |

### Harness Python suites (CI)

`test_raceparse.py` (T-05, T-21, T-38, T-51), the validator and smoke cells of T-03, T-04,
T-06, T-48, T-54, the source-sync cells of every race verb and action (R1-08, R1-10, R1-11,
T-12, T-19) and of every log literal a race lane pins as present or absent,
`test_campaign.py` (T-49), `test_race_campaign.py` (T-50), the T-59 harvest cells, the
`mutation_check.py` mutators (T-38, T-51), the lane catalog and fixture-set cells.

### In-game categories (the slices write the tests; one scene and one host each, A2)

| Category | Scene | Hosts (every spec that pins it) | Owner tasks |
|---|---|---|---|
| RaceMode | SPACECENTER | RACE-9 | R1 |
| RaceSites | SPACECENTER | RACE-9 | R2 |
| RaceSitesFlight | FLIGHT | RACE-10 | R2 |
| RaceSitesTrackingStation | TRACKSTATION | RACE-11 | R2 |
| RaceSitesStock | SPACECENTER, single-player save after a race save | SITES-2 | R2-13 |
| RaceSitesEditor | EDITOR | SITES-3 | R2-14 |
| RaceExchange | SPACECENTER | RACE-9 | R3E-24 |
| RaceFacts | FLIGHT | RACE-10; RACE-20 (single-player save) | R3E-08, R3E-13 |
| RaceFactsTrackingStation | TRACKSTATION | RACE-11; RACE-20 (single-player save) | R3E-13 |
| RaceRivalLane | FLIGHT | RACE-10 | R3W |
| RaceRivalIsolation | FLIGHT | RACE-10 | R3W-30 |
| RaceRivalLaneTrackingStation | TRACKSTATION | RACE-11 | R3W-20 |
| RaceRivalIsolationTrackingStation | TRACKSTATION | RACE-11 | R3W-30 |
| SpaceRace | SPACECENTER | RACE-9 | R4-02 (the ladder against stock), R4-13 |
| SpaceRaceFlight | FLIGHT | RACE-10 | R4-13 |
| RaceLooks | SPACECENTER | RACE-9; RACE-20 (single-player save) | R5-11, R5-14 |
| RaceLooksFlight | FLIGHT | RACE-10 | R5-09; R5-01, R5-04, R5-08 only for the cells that need a race save |
| GhostVisuals (existing) | FLIGHT | H15 (its existing host) | R5-01, R5-02, R5-08 cells that need no race save |
| GhostMap (existing) | FLIGHT, TRACKSTATION | S1.6 (FLIGHT), H44 (TRACKSTATION); both re-pinned for a new cell | R5-04's cells that need no race save |

There is no `RaceStockKerbin`, `RaceRivalMap`, `RaceScenery`, `RaceTerrain`, `RivalGhosts`,
`RivalFlags` or `RivalMap` category.

Cost of one in-game category, measured on today's gates: the attributes in house style; a
pinning spec (or a slot in an existing multi-category lane) with an interim pin and an
`INTERIM_PIN_IDS` entry until its first flight; a `GROUP` entry for a multi-category lane; a
`flight-profiles.md` regeneration. After that, every new test in the category reds
`CommittedBatchTallySourceSyncTests` until the spec's `total=` is re-pinned in the same PR (a
one-line edit). Today 135 of 142 categories are pinned this way. A race category cannot ride
an existing single-player lane: it skips there, and the anti-vacuity rule needs `passed`
above zero; a cell that needs no race save goes into an existing category instead (the
GhostVisuals and GhostMap rows), so it flies on day one of its task. A spec may hold several
`RunTests` steps when each names exactly one category and the spec sets
`batchVacuityOptOut = true` with a written `batchVacuityOptOutReason` in
`[expectations.logContracts]` (`hlib.py:8321-8343`; only the first step is probed for
vacuity; `L13-career-ground-truth-after-refly-and-f9.toml` has two). The campaign stages use
that to run `LedgerGroundTruth` beside a race category; per-site values still come from
`ListHandles kind=sites` (T-07, T-46) rather than four batches. P2b-07's two categories
(`GhostReapplyFrame`, `ChainTipBlockedGhost`) get one host step each on H15 (GS-8 for a cell
whose context H15 lacks; playback plan).

### Lanes and their cost

| Lane | Task | Tier | Seam-only | Est. p50 | Proves |
|---|---|---|---|---|---|
| RACE-0-stock-sites-baseline | T-07 | operator (stock-minimal) | yes | 45 s | Stock site values, the reference for SITES-2 (R2-13) |
| RACE-1-setup-create-join | T-13 | operator | yes | 150 s | Create, three joins, refusals, slots, looks, the test game key; the gate on, cleared and off (R1-13's assertions) |
| RACE-2-import-generator-folder | T-24 | operator | yes | 70 s | Import with every refusal of 7.15, rival map objects in the TS |
| RACE-3-rival-ghosts-flight | T-26 | operator | no (launch) | 150 s | Rival ghosts in flight are no vessel, collider or CommNet node (seam view); a rewind keeps them |
| RACE-4-isolation-whole-save | T-27 | operator | no (pad) | 300 s | 6.4 whole-save isolation (the one whole-save lane) |
| RACE-6-two-saves-in-turn | T-39, T-28 | operator | no (kRPC flight) | 700 s | Real exporter to real importer; quickload drops a shared flight |
| RACE-7-holder-change-folder-update | T-32 | operator | yes | 90 s | Holder change and its message |
| RACE-8-export-self-heal-offline | T-25 | operator | yes | 70 s | Offline export, seq continuity, self-heal |
| RACE-9 / 10 / 11 batch hosts | T-29, T-35, T-36 | operator | yes | 3 x 90 s | The race in-game categories |
| RACE-12-flown-rival-campaign | T-33 | operator | no (kRPC) | 400 s | A flown rival folder fixture |
| RACE-13-paired-copies | T-34 | operator (two slots) | yes | unestimated | Two live writers on one folder |
| RACE-14-scoring-marks | T-40 | operator | yes | unestimated | Every board mark and the header marks |
| RACE-15-content-check-launch-warning | T-41 | operator (stock-minimal) | no (launch) | unestimated | The content warning and the launch warning |
| RACE-16-refused-load-modded-kerbin, RACE-17-unscored-off-at-load | T-42 | operator (modded-compat, stock-minimal) | yes | unestimated | Refused loads (edges 24, 32) |
| RACE-19-setup-screens | T-43 | operator | yes | unestimated | The menu entry, setup pages and Settings section drawn and driven |
| RACE-20-single-player-hosts | T-44 | operator (stock-minimal) | yes (a vessel on the home pad, as RACE-10; the cells start their own recording) | unestimated | `RaceLooks`, `RaceFacts`, `RaceFactsTrackingStation` in a single-player save, one step per category and scene |
| RCA-1, RCB-1, RCA-2 (first flight of T-50's specs) | T-53 | operator | no (kRPC) | 860 + 860 + 1195 = 2915 s (CS p50s) | The first change of hands; each sees the other's ghost |
| RCA-3r, RCB-3r, RCA-3n, RCB-3n, RCB-3f, RCA-3f | T-56 | operator | no (kRPC) | unestimated | Retake by rewind, no retakes, Re-Fly against a rival |
| RCA-1 to RCA-5, RCB-1 to RCB-5 (the full race) | T-57 | operator | no (kRPC) | 8076 s (2.24 h) on two slots to RCA-5's end, then RCB-5 | Both careers to the Mun under the board oracle |
| RCA-0, RCB-0 (optional) | T-58 | operator | no | unestimated | A campaign from a race created by the seam |
| RCI-1 to RCI-9 | T-55 | operator (RCI-9 on stock-minimal) | no (kRPC) | about 2268 s in all (9 x 252 s) | Multiplayer does not disturb the defect-dense single-player lanes |
| SITES-1 to SITES-3 | R2-12 to R2-14 | operator (SITES-2 on stock-minimal) | mixed | unestimated | The four sites (r2) |

Every race lane carries the tag `race`. The estimates are from the reference p50s of section 3
(boot floor 45 s, a main-menu round trip like SWS-1, a B1 flight, the CS stages in the run
index), not measured; each lane's first flight replaces them.

Capacity: no race lane joins the daily or nightly tier. One pass of every race lane with an
estimate is about 7428 s (about 2.1 h) of p50, plus T-49's chain replay (about 1413 s) and
T-57's campaign (about 2.24 h on two slots to RCA-5's end); task-index D.4 places them by milestone.
The race lanes are still small next to the prerefactors: P2b-10's proof is a representative set of
18 lanes plus the daily tier (owner's answer to Q-05, 2026-11-14; playback 6.4), with the full run
left to release time; P2c and P2a name one nightly run, P3 and P3b named lanes and P6 a daily run
(the prerefactor plans own those choices).

Parallel flights: the parallel runner (`harness/tools/parallel_runner.py`, on main;
`harness/README.md`, "Parallel runs") flies lanes on two or three copies at once, each copy
with its own kRPC ports ("Install slots"), so every race lane can run through it
(`parallel_runner.py run --tag race`, T-45). The one measurement so far is the daily tier:
12 min 35 s on three copies against about 25 minutes one lane at a time, about half; the race
lanes on it are not measured.

### What needs the owner's PC

Every lane task (T-07, T-13, T-24 to T-29, T-32 to T-36, T-39 to T-44, T-49, T-53 to T-58),
each with a reading run before any block is armed. Nothing headless does. What only the
owner's eyes or two PCs prove is the one owner list (section 1).

### Manual proofs replaced (2026-11-15 audit)

No task relies on a manual check where a headless test, an in-game test or a lane can prove it
(README rule 11). The tasks whose proof was manual, or partly manual, and what proves them now:

| Task | Was | Proved by | Left on the owner list |
|---|---|---|---|
| P2b-07 | two categories run by hand | host steps on H15 (GS-8 as the fallback) | - |
| R1-14, R1-15, R1-17, R1-18 | menu entry, pages and Settings section checked by hand | T-43 (RACE-19) | keyboard feel, legibility |
| R1-19 | the content dump checked by hand | `ContentProbeMatchesRnD` on RACE-9 (wave 8) | - |
| R1-22 | the launch warning checked by hand | T-41 (RACE-15) | - |
| R2-02 | a refused load checked by hand | T-42 (RACE-16) | - |
| R3E-23 | S8's sync clients rerun through the worker | xUnit over an injected slow file layer | the rerun on real sync clients |
| R3W-09, R3W-25 | the owner's look | RACE-10 and R3W-25's cells | the look |
| R3W-16 | a screenshot | a GUI-tree key assertion (`.gui.json`) on RACE-10 | the look |
| R3W-21 | a WRN line read by hand | T-47's unknown-part rival and a `logContract` on RACE-10 | - |
| R3W-32 | a scale check with no fixture | T-47's scale folder and a frame-time budget from the Diagnostics lines | frame cost with real sync clients |
| R4-09, R4-10 | the window looked at | the census step (R4-12) and `UiAction op=state` rows | legibility |
| R4-11 | the camera half | an in-game cell asserting the camera target through `op=state` | camera feel |
| R4-13 | the ladder half tied to the census | the ladder cell moves into R4-02 (RACE-9) | - |
| R5-12 | the release zip checked by hand | a zip-listing cell beside `release.py`'s validation | - |
| R6-07 | the header marks looked at | T-40's `op=state` rows | - |
| R6-10 | a measurement on the owner's PC | a diagnostics-line budget on a lane | the number with real sync clients |


## 7. Gates and registrations

In the order the first feature commit meets them (the first race module commit, R1-03, which
registers `RaceCore` with its first file, meets 1 to 11, as do R3W-02 and R3E-02 for
`RaceWorld` and `RaceExport`; this plan's own tasks meet 12 to 16):

| # | Gate | Where it reds | File to edit | Re-pin |
|---|---|---|---|---|
| 1 | Placement of every production file | `scripts/arch` DLL-free suite: a `.cs` file in the root of `Source/Parsek`, a module rule with no folder, or a module with no files reds it | the file's folder (the folder is the module); a new module's `[[module]] name / folder` rule in `scripts/arch/modules.toml`, in the commit of its first file | by hand |
| 2 | Every module has a layer | DLL-free suite | `modules.toml:162` `[layers]` | by hand |
| 3 | Forbidden edges of 15.4 | DLL-free text scan and `test_semantic_layers.py` | `modules.toml:213` | by hand |
| 4 | Headless modules (`RaceCore`) | `test_ilheadless.py` | `modules.toml:182`, `scripts/arch/headless_pins.toml` | `python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 5 | Backward layer pairs | `test_semantic_layers.py` | `scripts/arch/layer_pins.toml` | `python3 scripts/arch/layers.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 6 | Knot canary | `scripts/arch/test_archview.py:4127` | that line | re-derive by hand |
| 7 | Semantic residue ratchet | `scripts/arch/test_semantic.py:623` | the bound (may not rise) | by hand, per `docs/dev/arch/README.md` |
| 8 | Statics written by another type; uncalled methods; complexity | `test_ilanalysis.py` | `scripts/arch/facts_pins.toml` | `python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 9 | Static lifetimes | `test_static_lifetimes.py`, `SaveScopedSessionResetTests` | `scripts/arch/lifetimes.toml`, `lifetime_pins.toml`, `SaveScopedSessionResetTests.cs:25`, `scripts/session-reset-allowlist.txt` | `python3 scripts/arch/lifetimes.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 10 | Log tag map (report-only; lane health) | `test_iltags.py` | `scripts/arch/log_tags.json` | `python3 scripts/arch/logtags.py Source/Parsek/bin/Debug/Parsek.dll --write` |
| 11 | OnSave / OnLoad characterization and OnLoad step order | `ParsekScenarioOnSaveCharacterizationTests`, `ParsekScenarioOnLoadCharacterizationTests`, `ParsekScenarioOnLoadStepOrderTests` | `Fixtures/SourceCharacterization/ParsekScenario.*.tokens.txt`, `ParsekScenario.OnLoad.steps.txt` | regenerate per the test's message, reviewed |
| 12 | Load policy | `LoadReconcilePolicyTests`, `LoadReconcileWiringGateTests` | `LoadReconcilePolicy.cs:242` and the decision table | by hand |
| 13 | Background threads (copy worker, 15.9) | `GrepAuditTests` | `scripts/background-threads-audit-allowlist.txt` | by hand |
| 14 | Save formats (`race-link`, `race-exchange`) | `SaveFormatTests`, `SaveFormatGoldenGateTests` | `SaveFormat.cs` (R1-12 registers both rows at 1); frozen `Fixtures/FormatGolden/v2/` (R1-12: the race save, `race.cfg`, `player.cfg`, `local.cfg`; `formats.txt` names both rows at 1) and `v3/` (R3E-04: a standing, its payload and an own mirror, no new row or bump) | each written once by its generator through the T-01 driver, env-gated (OV-15) |
| 15 | GUI gates | GUI census and scale gates | the GUI slice's list (P11) | other slice |
| 16 | Harness: verbs, kinds, lanes | `test_hlib`, `test_lanecatalog`, `IngameBatchWiringGroupTests`, `CommittedBatchTallySourceSyncTests` | `TestCommandVerbs.cs:56` (R1-08, R1-10, R1-11), hlib tables (`hlib.py:887`, `:1286`, `:1544`, `:1782`; `set-folder` rows by T-12), `lanecatalog.py:75`, `docs/dev/flight-profiles.md`, `registry.toml`, `INTERIM_PIN_IDS`, `GROUP` | `python harness/tools/lane_catalog.py` |

The ERS / ELS gate needs nothing (`RaceExportSource` reads through `EffectiveState`).
`StockLoadabilityTests` red only if a slice adds a `PartModule` or `VesselModule`.
`test_fixture_set_is_exactly_the_committed_set` is untouched, because race folders live in
`harness/fixtures/race/`.

This plan adds no grep-audit gate. The race gates are R1-06's: `race-core-no-store` on the
shared managed scanner `RaceSourceScan` in `GrepAuditTests.Race.cs` (OV-10), `race-no-writer`
with the cell that the save-lifetime persistence writers are absent from its allowlist
(OV-11), `race-no-recorder`, and the loader arch cell, which reads
`scripts/race-loader-files-allowlist.txt` (R3W-27 adds the loader entries, OV-12); and
R3W-27's rival-store gate, its type names derived from the rival store's file (OV-13). New
gates of this plan: the gated leak scan of T-38 and the save equivalence of T-27 and T-55,
T-17's and T-48's fixture currency tests, T-50's generated-spec byte gate, T-44's race-tag
lane-health rule, T-45's tag cell and the mutators of T-38 and T-51.

Live roots (A5): `docs/dev/arch/README.md:292-295` counts in-game tests and TestCommands
tooling as live roots, so a feature slice may make a member live with an in-game cell of its
category or with one of this plan's seam reads (`ListHandles kind=sites|rivals|board`)
before its production caller lands; an xUnit test does not count. A category that no
committed spec pins yet needs no tally re-pin; its host (T-29, T-35 or T-36) pins it later.

Uncalled-method ratchet (A1): T-01, T-02, T-10, T-16, T-17, T-18, T-31, T-37, T-47, T-52
and T-48's overlay writer add test-project code only, and T-03 to T-06, T-21, T-38, T-45,
T-48 to T-51, T-54 and T-59 harness code only; the production members they call
(the save writers, R1-10's manifest writers, R3E-04's `StandingCodec.Write` and
`RaceSync.Run`, the R3E ports) each have a production caller in the task that adds them.
The seam actions and kinds of T-07, T-12, T-19, T-20 and T-30, and T-29's
`RaceTestContext`, live in TestCommands or InGameTests, which count as live callers, so a
member only they call (for example T-19's forced-offline switch on the transport) lands
with them.

Docs per task: `CHANGELOG.md` gets nothing (no player-visible change in any T task);
`docs/dev/autotest-status.md` gets a row per lane and per harness capability;
`docs/dev/todo-and-known-bugs.md` gets any PARSEK-FAIL a lane finds; CLAUDE.md only if the
grep-gate family list or the harness layout section changes (then `cp .claude/CLAUDE.md
AGENTS.md`); `design-gui-inventory.md` is untouched.

## 8. Risks

- **The isolation lane reds on noise.** Stock save content that differs between two runs
  (contract offers, a physics-moved vessel) reads as a leak. Noticed on the reading runs of
  T-27; each mask entry carries a written reason and a T-05 cell.
- **A generator that drifts from the exporter.** Mitigated by writing only through the
  production codec (T-16) and by RACE-6, which feeds the real exporter to the real importer.
- **The test-game branch hides a normal game's tool mark.** Harness races are test games
  (7.12), so their flights score and the scoring path is proven live; the cost is that the
  one branch that skips the test-tools mark in a test game could also skip it in a normal
  game. Covered by R6-01's cells, T-40's normal-game generator rival
  (`rival-tool-marked-normal-game`) and the owner's one normal-game flight (owner list).
- **The campaign finds defects that are not race defects.** The career campaign found real
  single-player defects on its way (the CS-3 double pay, RELAUNCH-ROLLOUT-DEDUPED), and the
  integration variants run the most defect-dense single-player lanes. A red there is filed
  as what it is, never re-run away; budget for it in MS-1d and MS-10.
- **Flight variance in the campaign.** CS-7 has 3 PASS and 35 INVALID in the run index; only
  player A flies it, with `[retry] once`, and every date is asserted in a window (section 4b).
- **Path length on the owner's PC.** `${raceShared}` and the rival cache sit under the
  umbrella path; 6.3's validator may refuse. Noticed on the first flight of RACE-1; the
  folder name is already short.
- **Lane PR conflicts.** Every lane edits the catalog, registry and status files. Noticed as
  merge conflicts; the catalog is regenerated, never spliced.
- **A two-phase verb across the main menu is slow and timing-sensitive.** Noticed as
  RACE-1 runs over its budget; the fix is a split lane, not a longer budget.
- **The fake store misses a real sync-client behaviour.** S8 findings become new controls;
  the owner's two-PC test is the backstop.

## 9. Corrections and gaps

1. **Design 11's second lane could not pass as first written.** 7.12 makes the seam, the test
   runner and injected recordings mark a race save, and a marked flight scores nothing, so a
   harness-flown B would never take a milestone from A. Settled 2026-11-15 by the owner
   ("yes, as labelled test games"; replaces OV-08's "no test race flag"): a game the test
   tools create is a TEST GAME (`testGame = True` in the manifest, R1-23; only the test tools
   set it: the seam's create step in a running game, or T-48's fixture writer staging a race
   save before the game starts through the same manifest writer; never the setup dialog or
   any other player-reachable path, and a normal game never becomes one), every
   player sees "Test game" in the Space Race window header and in the game's summary where
   it is joined or listed, and in it the test-tools mark does not stop a flight from scoring
   (R6-01 skips only that mark; cheat-menu use, debug completions, uncounted or changed
   parts, a different tree and save marks apply unchanged). R3E-08 states when a recording
   carries the mark in a save armed from boot (a recording started while the seam is armed
   carries it from its start), so a normal game flown by the harness really scores 0 and a
   test game really scores. Consequences: live lanes now prove holder changes (T-53),
   retakes and no retakes (T-56) and both careers under the board oracle (T-57) on real
   flights; the generator folders (T-16, T-17, T-40, T-47) stay for the faults and edges a
   flight cannot make (conflict copies, tampering, same-second ties, end-date arithmetic, a
   normal game's tool mark), R6-11's proofs included; the owner list keeps one real
   normal-game flight.
2. **Injected recordings are written offline** (`run.py:615` runs `inject-recordings.ps1`
   before boot; `ScenarioWriter.cs:575`), so Parsek cannot see that injection "ran". The
   design does not say how such a save is marked. The plan never injects into a race save;
   the disclosure slice decides whether the injector stamps the mark or refuses race saves.
3. **"Ran" is undefined for the seam.** An armed addon that received no verb, versus a verb
   executed. The plan assumes any received verb marks the save. Settled: R3E-08 marks the
   save at the seam's arming, at the in-game test runner and at the autorun hook, which
   covers every verb; no verb marks on its own (OV-33).
4. **"Identical saved games" (6.4) is not reachable byte for byte across two flown
   sessions**: explicit-UT warps land near the UT and flight physics is not repeatable. The
   plan uses a canonical diff with a pinned, reasoned mask (T-05, T-27); byte identity is
   headless only (T-23). Settled: the masked comparison is assumed (question 2), RACE-4 is
   the one whole-save lane (OV-05), and the headless byte-identity test is R3W-29's (OV-20).
5. **The seam's `LoadGame` bypasses the main menu** (`run.py:1978-1979`), as other mods
   can. A site restore tied to the main menu would miss it. RACE-5 drives both routes; the
   plan assumes R2 restores on the loaded save's gate, not on MAINMENU. Settled: SITES-2
   (R2-13) drives both routes against RACE-0's values, and `ParsekRaceBoot.OnSceneRequest`
   calls `RaceSites.Reconcile` on every scene request, the main menu included (OV-04,
   OV-51).
6. **15.14 misses gates the first commits meet:** the OnSave / OnLoad characterization
   goldens, the hlib per-verb tables and their source-sync cells, the lane catalog
   regeneration, the registry growth rule, the batch tally pins, `INTERIM_PIN_IDS` and the
   multi-category `GROUP`. Listed in section 7.
7. **Registering `raceFormat` forces a full frozen v2 golden folder**
   (`SaveFormatGoldenGateTests.cs:43-73`, `:133`): a `persistent.sfs` and a `formats.txt`
   listing every format, not only a race file. The plan supplies the driver (T-01); R1-12
   writes `v2` and R3E-04 adds `v3`. Settled: R1-12 registers both rows (`race-link`,
   `race-exchange`) and writes `v2` with what R1 writes (a race save, `race.cfg`,
   `player.cfg`, `local.cfg`; both rows at 1); R3E-04 registers nothing, bumps nothing and
   adds `v3` (a standing, its payload, an own mirror); both generators run through T-01's
   driver (OV-15).
8. **A recursive fixture glob** (`OptimizerTransferCohesionTests.cs:521-530`) would read rival
   payload placed under `harness/fixtures/saves/`. Race folders live in
   `harness/fixtures/race/`.
9. **Several `RunTests` per spec are allowed** (corrected 2026-11-15; the plan said one):
   `hlib.py:8321-8343` accepts more than one when each names exactly one category and the
   spec sets `batchVacuityOptOut` with a reason, and
   `L13-career-ground-truth-after-refly-and-f9.toml` flies two. Only the first is probed for
   vacuity, so the race hosts keep one comma-list batch each; the campaign stages use the rule
   to run `LedgerGroundTruth` beside a race category. Per-site values still come from
   `ListHandles kind=sites` (T-07, T-46).
10. **Known-gate 6:** a race save must be entered through the Space Center route
    (`design-autotest-command-seam.md:1010-1025`); the plan assumes `ParsekRaceScenario`
    shares `ParsekScenario`'s need, so every race lane loads with `scene=spacecenter`.
11. **Where the shared folder lives.** Design 11 does not say where the "temporary shared
    folder" lives. `harness/README.md:110-111` allows reads and writes under `harness/`,
    `automation/`, the target instance and the read-only dev install (corrected 2026-11-15:
    the plan said the instance only). A single-instance race lane uses `${raceShared}` =
    `<instance>/rs/<n>` (OV-02); a paired lane (T-54) shares
    `<umbrella>/automation/rs/<run>/<group>/`, outside both instances and inside the
    boundary. A folder elsewhere on disk (a real sync client's folder) stays on the owner
    list.
12. **The design has no seam surface for the race** (verbs, read kinds, a harness-side
    folder step). This plan adds `RaceSetup`, `RaceCommand`, three `ListHandles` kinds and
    one harness step (T-06), which the step grammar does not have today. Settled: the names
    and argument names are this plan's; `RaceCommand action=status` lands in R1-08,
    `RaceSetup action=create` in R1-10 and `action=join` in R1-11, T-12 adds `set-folder`
    and the remaining tables, T-19 adds `sync`, `offline` and `online` (OV-01).
13. **The analyzer never sees the rival cache** (`SaveDirectoryLoader.cs:62` reads only
    `Parsek/Recordings`). No offline check covers rival payload; the plan adds none and the
    import refusals carry that load.

## 10. Questions for the owner

1. **Test races and tool marks.** Answered 2026-11-15: "Yes, as labelled test games", option
   (b): a test game flag only the test tools can set (R1-23: the seam's create step or T-48's
   fixture writer, never the setup dialog), shown to every player, under which
   the test-tools mark does not zero the score (section 9 item 1).
2. **The whole-save isolation lane compares saves with a small masked list of time values.**
   Options: (a) accept a canonical comparison where only listed time values may differ,
   each with a written reason (recommended); (b) insist on byte-identical saves, which
   forces a session where no time passes and so cannot see a rival flag or a rival landing.

   Assumed until the owner says otherwise: (a), applied in T-05 and T-27.
3. **Two game copies racing on one PC.** With the parallel runner's copies, a lane can run two
   copies writing one folder at the same moment. Options: (a) build it as an operator lane
   (recommended: the only proof of two live writers short of your two-PC test); (b) leave
   concurrency to the two-PC play test.

   Assumed until the owner says otherwise: (a); T-34 stays optional. The parallel runner it
   builds on is on main (pull request #2592).
