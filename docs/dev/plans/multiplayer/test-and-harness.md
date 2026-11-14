# Race test and harness machinery - implementation plan

Status: PLAN (2026-11-14), not started. Design sections covered: 11 (in full), the gates of
6.4, 10 (log tags as test anchors), 15.8, 15.10 (harness-made opponents, the seed only),
15.14. Phases: R0b (the day-one tasks), then alongside R1 to R4; one optional task after the
parallel-flights work lands.

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
| C. Two race saves in one install, played in turn (the SWS-1 / `NewGame as=` precedent) (`RaceSetup` from R1-10 / R1-11, RACE-1, RACE-6) | The real exporter feeding the real importer through a real folder; join, slots and looks; a quickload that drops a shared flight | Concurrency; anything a scoring flight would show (every harness-flown flight is tool-marked, section 9 item 1) |
| D. A harness campaign that flies a race save and harvests its player folder (T-33, 15.10) | A real, flown, unmarked-by-generator rival folder as a fixture; the seed of R8 | Nothing new about sync; it is a fixture source |
| E. Two game copies on one PC sharing one folder (T-34, after parallel flights) | True concurrency of two live writers on one folder | Real sync clients, real clock skew |

Recommendation: **A is primary for the rules, B is primary for everything live**, C is the
one end-to-end proof of the real exporter, D feeds B with real flights, E is optional.

What none of A to E can prove, and what stays on the owner's two-PC play test: real sync
clients (placeholder files, their conflict-copy naming, partial upload; spike S8), real
clock skew between machines, two different operating systems reading one folder (the id and
path-length rules are unit-tested, not exercised), two different installs (partly reachable:
a fixture made on one profile can be staged on another), and a folder outside the game
instance (the harness may not touch one, section 9 item 11).

### Not in scope

- The feature itself and its own unit tests (each feature slice owns the tests of its code,
  design 11's first list).
- The in-game tests of each category (the slices write them; this plan writes the host lanes
  and the shared helper).
- The GUI gates of the Space Race window (P11, the GUI slice).
- Any co-op feature, release, version bump or tag.

## 2. Starts after

- Version 1.1.0 is released and the owner says go.
- **Day-one tasks (no feature code present):** T-01, T-02, T-03, T-04, T-05, T-06, T-07.
  They touch only the test project, the harness and the `TestCommands` tooling module.
- After R1-04 (`RaceGate`): T-10 (with T-01).
- After R1-08 (`RaceCommand action=status`, `local.cfg`): T-12. After R1-10 (`RaceSetup
  action=create`, on P10-01's core) and R1-11 (`RaceSetup action=join`): the lane T-13.
- After R1-10 and R3E-04 (the manifest write codecs, the standing codec and
  `RaceSync.Run(SyncPorts, SyncInputs)`): T-16, then T-17. After R3E-04 and R3E-16: T-18
  (its self-heal case with R3E-20). After R3E-17 and R3E-18: T-19. After R3E-04 and
  R3E-19: T-21. After R3W-05 and R3W-09 (the rival store and the rival ghost lane): T-20,
  then the lanes T-24 to T-29 (T-24 also after R3W-20, T-25 after R3E-20, T-29 after
  R2-03, R2-05, R5-11 and R3W-30). T-33 after R1-10, R3E-16 and T-21.
- After R4-04 (the board): T-30, T-31 (also R3E-19, R6-03), T-32 (also R4-07).
- T-34: after the sibling parallel-flights work lands (two copies, two kRPC port pairs).
- Dropped here and owned elsewhere: T-08, T-09, T-15 (R1-06), T-11 (R1-10, R1-11), T-14
  (R2-13), T-22 (R3W-27), T-23 (R3W-29).
- Spikes: none blocks a task here. S8 (sync clients) feeds the fake store's file behaviours
  (T-02 can start on the cases in this plan and gain S8's findings later).

## 3. What the code looks like today

### Headless save writing

- `Source/Parsek.Tests/FormatGoldenV1GeneratorTests.cs:283` `BuildState()` builds a populated
  `ParsekScenario` headlessly; `:189` `RunSaveWriters(scenario)` runs the production save
  writers in OnSave's phase order and returns the PARSEK node; `:87` `ResetAllStores()`.
  The writer is skipped unless `PARSEK_WRITE_FORMAT_GOLDEN=v1` (`:20`, `:117`). This is the
  base of the byte-identity tests; today it is private to one test class.
- `Source/Parsek/ParsekScenario.cs:483` is `OnSave`. Its token stream is pinned by
  `ParsekScenarioOnSaveCharacterizationTests` against
  `Source/Parsek.Tests/Fixtures/SourceCharacterization/ParsekScenario.OnSave.tokens.txt`; the
  OnLoad twin is `ParsekScenarioOnLoadCharacterizationTests`.
- `Source/Parsek.Tests/SaveFormatGoldenGateTests.cs:43-73` `FindCoverageProblems`: every
  registered format version from baseline to current must be carried by a golden folder's
  `formats.txt`; `:133` every golden folder needs a manifest and a `persistent.sfs`; `:149`
  committed golden files are frozen by checksum.

### Generators

- `Source/Parsek.Tests/Generators/ScenarioWriter.cs:81` `AddRecordingAsTree`, `:575`
  `InjectIntoSaveFile`, `:585` and `:679` call `WriteSidecarFiles(saveDir)`, which writes
  `Parsek/Recordings/<id>.prec`, `_vessel.craft`, `_ghost.craft`.
- `Source/Parsek/SidecarVersions.cs:26-30` `SidecarContentHash.Compute(byte[])`: the hash the
  payload file names of 6.1 carry.
- `Source/Parsek/RecordingPaths.cs:302` validates ids with `Path.GetInvalidFileNameChars()`,
  which differs per OS (P7 moves it).

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
  operation; `harness/lib/test_hlib.py:18778` `NewGameSourceSyncTests`.

### The harness

- `harness/run.py:1341` `stage_fixture`: template copy, shared ships, injection, liveState
  patches; `:1450-1451` "The seam's LoadGame bypasses the Main Menu"; `:542` `run_inject`
  runs `inject-recordings.ps1` offline before boot; `:192` `INJECTION_PRESETS`.
- `harness/README.md:110`: no new reads or writes outside `harness/`, `automation/`, the
  target instance, the read-only dev install. A shared folder must therefore sit inside the
  target instance.
- `harness/README.md:1419-1430`: the produced-save snapshot copies one save directory into
  `results/<runId>_save/`; nothing else.
- `harness/README.md:366-414`: "Authoring a fast lane" (reuse a host's flight, explicit-UT
  `WarpToUT`, minimal dwells, fail fast, tight windows, tier by measured p50,
  `flight_efficiency.py` after the first green flight).
- `harness/README.md:1573-1579`: tiers daily / nightly / operator / longhaul / full.
- `harness/lib/saveparse.py:165` `parse_sfs`, `:419-534` the row types: the model for a
  pure evaluator. Its gating is armed per spec through `ARMED_ALLOWLIST`
  (`harness/lib/test_hlib.py:12255`).
- `harness/lib/test_hlib.py:3902` `CommittedBatchTallySourceSyncTests` (a spec's pinned batch
  tally must equal the C# attributes), `:4955` `INTERIM_PIN_IDS` (never-flown specs),
  `:7807` the multi-category `GROUP`.
- `harness/lib/test_saveparse.py:4111` `test_fixture_set_is_exactly_the_committed_set` pins
  the directories under `harness/fixtures/saves/`.
- `harness/lib/test_lanecatalog.py:217` `test_the_committed_catalog_is_current`: a new spec
  regenerates `docs/dev/flight-profiles.md` in the same commit.
- `harness/coverage/registry.toml`: dimensions D1 to D18; a new feature adds its values in the
  same PR.
- `harness/fixtures/saves/fresh-career` is the boot save `CS-0-new-career-game` uses for
  `NewGame` (`harness/scenarios/CS-0-new-career-game.toml:32`).
- No spec id starts with `RACE`, `MP` or `ISO` on `main` or on the 15 remote branches read
  with `git ls-tree -r --name-only <branch> -- harness/scenarios`.
- `docs/dev/autotest-roadmap.md:6685`: runs are strictly serial; no parallel lane today.

### Grep-audit gates

- `Source/Parsek.Tests/GrepAuditTests.cs:180` `RunGrepAuditScript(scriptFileName,
  managedFallback)` probes for pwsh and falls back to a managed scan. The gallery write-set
  gate is the model for an allowlist-of-types gate: `:539` `GuiMockAllowedTypes`, `:99` the
  gate, `:105` anti-vacuity, `:125` the cases a denylist missed, `:158` mirror equality with
  the script. `:71` `ParsekFileDeletes_PlantedDeleteUnderRecordings_IsRed` is the
  planted-red pattern.

### Architecture

- `scripts/arch/modules.toml:511` `[layers]`, `:531` `[headless.modules]`, `:562-563`
  `[forbidden]` edges.
- `scripts/arch/test_archview.py:4090` the knot canary `[435, 9, 4, 3, 2]`, re-derived by
  hand.
- `scripts/arch/test_semantic.py:623` the semantic residue ratchet (a hand-set bound).
- `Source/Parsek/LoadReconcilePolicy.cs:242` `KnownLoadInitiators`.
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

Reference p50s: SWS-1 73 s; CS-0 new career 48 s; PPB lanes 45 to 51 s (the boot floor of a
seam-only lane); R7c 52 s; RW-1 146 s; B2-lko-ascent 272 s; B1-pad-hop 389 s.

## 4. Tasks

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
    present, length zero or unreadable), conflict copies (two writers of one path become
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
- **Proof:** `test_hlib` cells for the validator (accepts, refuses outside paths, refuses an
  unknown fixture), `test_run_smoke` with `_fake_ksp` (staged, snapshotted, wiped before the
  next lane).
- **Registers:** `harness/README.md` (fixtures section, snapshot section);
  `docs/dev/autotest-status.md` row for the harness capability.
- **Size:** M. **Depends on:** nothing. **PC:** no.
- **Risk:** low; inert for every spec without `[fixture.race]`.

### T-04 Snapshot every save a lane creates

- **Change:** `[fixture] snapshotSaves = ["${runSave}-b", ...]`: `run.py` copies each named
  save folder of the instance into `results/<runId>_save-<suffix>/` the same way it copies
  the run save. The `as=` saves a lane creates are named in advance, so the list is static.
- **Proof:** `test_hlib` validator cells; `test_run_smoke` cell that two saves are snapshotted.
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
  `LISTHANDLES_KIND_VALUES` and `ListHandlesSourceSyncTests` updated; a new daily lane
  **RACE-0-stock-sites-baseline** (fresh-career, `ListHandles kind=sites`, about 45 s)
  pins the stock values SITES-2 (R2-13) compares against (OV-04). SITES-1 to SITES-3 read
  their per-site values with this kind, not with extra batches (OV-03).
- **Registers:** `design-autotest-command-seam.md` ListHandles grammar; hlib tables; lane
  catalog regeneration; `registry.toml` D19 `race` dimension with value `stock-sites`;
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
  race modules reads `RaceGate` first (AST-free, over a list of entry points kept in this
  test file; no production attribute, OV-14).
- **Proof:** the tests themselves; log capture asserts the one `[Race]` "inert" line.
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

- **Change:** `harness/scenarios/RACE-1-setup-create-join.toml`, tier nightly, profile
  stock-minimal, boot `fresh-career`, `[fixture.race] sharedFolder = "empty"`,
  `snapshotSaves` for the joiners. Flow: create as A; join as B, C, D (`as=` saves); a fifth
  join refused `race-full`; a name already taken refused; Create on a folder with a race
  refused; `RaceCommand status` after each; slots 0 to 3 and four distinct looks pinned
  through `[Race]` lines; `ListHandles kind=committed` count 0 in each. It takes R1-13's
  assertions (OV-02): from MP-1, `Gate: race save` in the Space Center of the new save,
  `Gate: cleared` at the main menu, the boot single-player save loaded with
  `LoadGame scene=spacecenter` reads `Gate: not a race save reason=no-mark` and gets no
  `SCENARIO` node from R1, and the race save loaded again turns the gate on with the same
  `raceId`; from MP-2, B's join into a folder that already holds A's race takes slot 1 and
  the first free look (`[Race] Join: ... slot=1 look=..`). Last, B starts over in its seat
  (`RaceSetup action=join startOverPlayerId=<B>`, R1-11): same slot and look, a fresh save.
- **Proof:** the lane; `raceParse` row report-only (T-21 arms it later).
- **Registers:** lane catalog; `registry.toml` D19 values `create`, `join`, `refusal`;
  `autotest-status.md`; tags.
- **Size:** M. **Depends on:** T-03, T-04, R1-10, R1-11. **PC:** yes.
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
  `noRetakes` and end date, payload made with
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
  reader; each fault is refused or flagged with the reason 7.15 names.
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
  back (seq continuity, own mirror copy-out); partial files and placeholders never imported;
  conflict copies ignored with a note; a foreign edit to the own subfolder healed
  (R3E-20); a late joiner; export-before-import in one tick.
- **Proof:** the tests; each seed printed on failure.
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

- **Change:** `raceparse.py` gains structure facets over `results/<runId>_race/` and the
  produced save's `Parsek/Race/`: per player chain intact, latest complete seq, seq gaps,
  completeness of payload, witness log entries, own mirror equals own subfolder. A
  `raceParse` verifier row in `run.py` with spec block `[expectations.race]`. Two
  verdicts: the leak scan is GATED from the first lane (`PARSEK-FAIL(race-isolation)`);
  structure windows are report-only until armed per spec (`PARSEK-FAIL(race-structure)`),
  armed through the same allowlist discipline as `saveParse`.
- **Proof:** `test_raceparse.py` over folders the T-16 writer produced (copied in as text
  test data); a smoke run with `_fake_ksp`; a cell that every verdict subkind is in hlib's
  verdict table.
- **Registers:** `harness/README.md` verifier list; `design-autotest-harness-core.md`
  verdict subkinds; `ARMED_ALLOWLIST` grows a race block entry when one is armed.
- **Size:** M. **Depends on:** T-05, T-03, R3E-04, R3E-19. **PC:** no.
- **Risk:** medium; a format change in R3 moves these facets, so the test data is
  regenerated, never hand-edited.

### T-22 Race grep gate: rival store names only in allowlisted files

Dropped: replaced by R3W-27 (OV-13).

### T-23 Isolation byte-identity test (6.4)

Dropped: replaced by R3W-29 (OV-20).

### T-24 Lane RACE-2-import-generator-folder

- **Change:** nightly, seam-only. Join the race staged from `three-rivals-faults` (a folder
  that already holds a race is joined, never created into); `sync`; assert each refusal by
  its reason, the conflict copy ignored, the incomplete newest standing skipped, the
  newer-format standing refused whole, the last good state kept; `ListHandles
  kind=rivals`; then the Tracking Station through `LoadGame scene=trackstation` with the
  rival map objects present and guarded.
- **Proof:** lane; `raceParse` leak scan gated.
- **Registers:** catalog, D19 `import`, `refusal`, status.
- **Size:** S. **Depends on:** T-17, T-19, T-20, T-21, R3W-20. **PC:** yes.
- **Risk:** low.

### T-25 Lane RACE-8-export-self-heal-offline

- **Change:** nightly, seam-only. Create; `RaceCommand offline`; drive one export while
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

- **Change:** nightly. Join the staged `rival-station` race; `sync`; launch the stock craft
  from the VAB (`GoToEditor` / `LaunchFromEditor`), `WarpToUT` with explicit UTs across the
  rival SOI crossing, the rival landing and the rival flag plant; `ListHandles kind=rivals`
  after each (no vessel, no collider, no CommNet node). It keeps only these seam
  assertions and runs no batch: the in-game isolation cells (progress and ledger unchanged
  across the SOI crossing and the landing) are R3W-30's `RaceRivalIsolation`, hosted by
  RACE-10 and RACE-11 (OV-21).
- **Proof:** lane; leak scan.
- **Registers:** catalog, D19 `rival-ghosts`, status.
- **Size:** M. **Depends on:** T-17, T-19, T-20. **PC:** yes.
- **Risk:** medium; the warp lands near, not on, each UT, so the waits target after-event
  UTs with margin.

### T-27 Lane RACE-4-isolation-whole-save

- **Change:** nightly; the one whole-save isolation lane of the race work (OV-05; R3W's
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

- **Change:** nightly. A creates, flies the B1 pad-hop shape (the shortest committed flight
  the mission library offers), commits, quicksaves before the flight first, exports; B
  joins (`as=`), syncs: A's ghost launches from site 0 (`ListHandles kind=rivals`), A's
  board row is present and marked (A's flight is tool-marked, section 9 item 1), B's
  ledger untouched (`RunTests LedgerGroundTruth`). Then A loads the pre-flight quicksave,
  exports; B syncs: the ghost and the row are gone. Design 11's first and third lanes in
  one spec, per "reuse a host's flight". From R4-04 on, its `logContract` pins
  `[RaceBoard] Board computed` after each of B's syncs (R4-04's line, OV-07).
- **Proof:** lane; `raceParse` gated leak scan, armed structure after two reading runs.
- **Registers:** catalog, D19 `two-saves`, `quickload-drop`, status.
- **Size:** M. **Depends on:** R1-10, R1-11, T-19, T-20, T-21, R3E-16; soft: R4-04 (the
  row assertion and the board line are added when it lands; the lane flies before it
  without them). **PC:** yes.
- **Risk:** medium; about 700 s, the longest RACE lane; a quickload across a shared flight is
  exactly the case most likely to find a real defect.

### T-29 In-game race context helper and the host lanes

- **Change:** `Source/Parsek/InGameTests/RaceTestContext.cs` (tooling module): `RequireRace
  (out RaceGate)` which `InGameAssert.Skip`s with the required context named, accessors for
  the rival store and the applied site. Three host lanes, one per scene, each one `RunTests`
  with a comma list of the categories of the table in section 6 (A2; one scene per
  category, OV-09):
  - **RACE-9-sc-batch**, SPACECENTER, 5 categories:
    `RaceMode,RaceSites,RaceExchange,SpaceRace,RaceLooks`.
  - **RACE-10-flight-batch**, FLIGHT, 6 categories:
    `RaceSitesFlight,RaceFacts,RaceRivalLane,RaceRivalIsolation,SpaceRaceFlight,RaceLooksFlight`.
  - **RACE-11-ts-batch**, TRACKSTATION, 4 categories: `RaceSitesTrackingStation`,
    `RaceFactsTrackingStation`, `RaceRivalLaneTrackingStation`,
    `RaceRivalIsolationTrackingStation`.
  Each host joins a staged race as a later player, so it runs in a race save at slot 1 to 3
  (the moved space center, the patches and the copies exist there), and the race
  categories run with `passed` above zero (a single-player batch would skip them all);
  RACE-10 enters FLIGHT with a vessel on the home pad. These three are the only specs that
  pin these categories, and no other spec runs a batch of any of them: SITES-1 runs no
  batch (OV-03), R5-13 is dropped (OV-06), and RACE-3 and the other RACE lanes run none.
  `RaceSitesStock` (SITES-2) and `RaceSitesEditor` (SITES-3) are R2's lanes' own.
- The hosts list every category whose first test has landed when T-29 lands; a category
  whose first test lands later joins its host's comma list in that pull request, and after
  T-29 every new test in a hosted category re-pins its host in its own pull request
  (section 6).
- **Proof:** the lanes; each pinned through `INTERIM_PIN_IDS` until its first flight, then
  whole.
- **Registers:** `MultiCategoryBatchWiringGroupTests.GROUP` (test_hlib.py:7807), catalog,
  autotest-status, D19 `ingame`.
- **Size:** M. **Depends on:** T-17, T-19, R2-03, R2-05, R5-11, R3W-09, R3W-30; soft: the
  first test of each other category (R1-03, R2-11, R3E-08, R3E-13, R3E-24, R3W-20, R4-13,
  R5-01). **PC:** yes.
- **Risk:** medium; every later test in a pinned category reds the pin until the spec is
  re-pinned in the same PR (cost in section 6).

### T-30 `ListHandles kind=board`

- **Change:** one line per milestone row: ladder id, holder(s), date, points, marks
  (unverified, tool-marked, blocked by no retakes), and a summary line with totals.
- **Proof:** formatter tests, hlib.
- **Registers:** seam doc, hlib.
- **Size:** S. **Depends on:** R4-04. **PC:** through T-32.
- **Risk:** low.

### T-31 "Same files, same board" and the changed-file scenario

- **Change:** `Source/Parsek.Tests/Race/SameFilesSameBoardPropertyTests.cs`: a seeded
  hand-rolled generator (no new NuGet package) builds about 200 races with T-16 and runs
  them through T-18 with random arrival orders, calendar settings and installed-body sets;
  every machine's board is byte-equal. `TwoMachineChangedFileTests.cs`: the 11 scenario
  (machine 1 saw the real standing and keeps it; machine 2 applies the changed one and
  shows the receipt disagreement; after the writer's heal both boards agree), plus the
  keep rule after the end date.
- **Proof:** the tests; a failing seed prints its schedule.
- **Registers:** none.
- **Size:** M. **Depends on:** T-16, T-18, R4-04, R3E-19, R6-03. **PC:** no.
- **Risk:** low.

### T-32 Lane RACE-7-holder-change-folder-update

- **Change:** nightly, seam-only. Join a race staged from `holder-gen1`; `sync`; board shows
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

### T-33 Lane RACE-12-flown-rival-campaign and the harvest tool

- **Change:** operator lane: create a race, warp to day 3, LKO ascent (B2 shape), commit,
  export. `harness/tools/harvest_race_folder.py --from results/<runId>_race --as <name>`
  copies the player folder into `harness/fixtures/race/flown-<name>/` with a verify step
  (chain intact, payload complete). Its flights are tool-marked; the fixture is used for
  import and ghosts, not for scoring.
- **Proof:** the lane; `test_harvest_race_folder.py` (pure parts).
- **Registers:** catalog, fixtures README, status.
- **Size:** M. **Depends on:** R1-10, T-21, R3E-16. **PC:** yes.
- **Risk:** low.

### T-34 Lane RACE-13-paired-copies (optional)

- **Change:** operator lane that runs two game copies at once on one shared folder (the
  sibling parallel-flights work): both create or join, both export and sync for a few
  cycles; assert both boards equal and the folder well-formed.
- **Proof:** the lane.
- **Registers:** as any lane.
- **Size:** M. **Depends on:** parallel flights (two instances, two kRPC port pairs: the
  parallel runner `harness/tools/parallel_runner.py` of pull request #2592, outside this
  plan), T-21, T-30. Built only once that runner is on main (question 3, assumed (a)).
  **PC:** yes.
- **Risk:** medium; it is the only proof of two live writers, and it depends on machinery
  this plan does not own.

## 5. Parallel tracks

- **Track H (harness, Python):** T-03, T-04, T-05, T-06, then T-21. Hot files:
  `harness/run.py`, `harness/lib/hlib.py`, `harness/lib/test_hlib.py`. T-03 and T-04 both
  touch `stage_fixture` and the snapshot code, so T-04 and T-06 land after T-03.
- **Track S (test project, C#):** T-01, T-02, then T-10, T-16, T-17, T-18, T-31. No
  shared hot file except `Source/Parsek.Tests/Generators/`, where T-01 and T-16 add files
  only.
- **Track V (seam verbs):** T-07, then (after R1-08, R1-10 and R1-11 land their actions,
  OV-48) T-12, T-19, T-20, T-30. Hot files: `TestCommandVerbs.cs`,
  `TestCommandRaceCommand.cs`, `TestCommandListHandles.cs`,
  `design-autotest-command-seam.md`, the hlib verb tables. Land them one at a time, in the
  order R1-08, R1-10, R1-11, T-12, T-19.
- **Track L (lanes):** each lane is its own spec, but every lane edits
  `docs/dev/flight-profiles.md` (regenerated), `harness/coverage/registry.toml`,
  `docs/dev/autotest-status.md` and, for batch lanes, `INTERIM_PIN_IDS` and `GROUP` in
  `test_hlib.py`. Two lane PRs at once will conflict in those files; regenerate the catalog
  after merging `main`, never splice it.
- Day one, four agents can run T-01, T-02, T-03 and T-05 at once; T-04, T-06 and T-07 follow.

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
| `SameFilesSameBoardPropertyTests`, `TwoMachineChangedFileTests` (T-31) | 11's determinism and tamper scenarios across machines |

### Harness Python suites (CI)

`test_raceparse.py` (T-05, T-21), the validator and smoke cells of T-03, T-04, T-06, the
source-sync cells of every race verb and action (R1-08, R1-10, R1-11, T-12, T-19), the lane
catalog and fixture-set cells.

### In-game categories (the slices write the tests; one scene and one host each, A2)

| Category | Scene | Host (the only spec that pins it) | Owner tasks |
|---|---|---|---|
| RaceMode | SPACECENTER | RACE-9 | R1 |
| RaceSites | SPACECENTER | RACE-9 | R2 |
| RaceSitesFlight | FLIGHT | RACE-10 | R2 |
| RaceSitesTrackingStation | TRACKSTATION | RACE-11 | R2 |
| RaceSitesStock | SPACECENTER, single-player save after a race save | SITES-2 | R2-13 |
| RaceSitesEditor | EDITOR | SITES-3 | R2-14 |
| RaceExchange | SPACECENTER | RACE-9 | R3E-24 |
| RaceFacts | FLIGHT | RACE-10 | R3E-08, R3E-13 |
| RaceFactsTrackingStation | TRACKSTATION | RACE-11 | R3E-13 |
| RaceRivalLane | FLIGHT | RACE-10 | R3W |
| RaceRivalIsolation | FLIGHT | RACE-10 | R3W-30 |
| RaceRivalLaneTrackingStation | TRACKSTATION | RACE-11 | R3W-20 |
| RaceRivalIsolationTrackingStation | TRACKSTATION | RACE-11 | R3W-30 |
| SpaceRace | SPACECENTER | RACE-9 | R4-13 |
| SpaceRaceFlight | FLIGHT | RACE-10 | R4-13 |
| RaceLooks | SPACECENTER | RACE-9 | R5-11 |
| RaceLooksFlight | FLIGHT | RACE-10 | R5-01, R5-04, R5-09 |

There is no `RaceStockKerbin`, `RaceRivalMap`, `RaceScenery`, `RaceTerrain`, `RivalGhosts`,
`RivalFlags` or `RivalMap` category.

Cost of one in-game category, measured on today's gates: the attributes in house style; a
pinning spec (or a slot in an existing multi-category lane) with an interim pin and an
`INTERIM_PIN_IDS` entry until its first flight; a `GROUP` entry for a multi-category lane; a
`flight-profiles.md` regeneration. After that, every new test in the category reds
`CommittedBatchTallySourceSyncTests` until the spec's `total=` is re-pinned in the same PR (a
one-line edit). Today 135 of 142 categories are pinned this way. A race category cannot ride
an existing single-player lane: it skips there, and the anti-vacuity rule needs `passed`
above zero. A spec has one `RunTests` step, so per-site values come from `ListHandles
kind=sites` (T-07), not from four batches.

### Lanes and their cost

| Lane | Tier | Seam-only | Est. p50 | Proves |
|---|---|---|---|---|
| RACE-0-stock-sites-baseline | daily | yes | 45 s | Stock site values, the reference for SITES-2 (R2-13) |
| RACE-1-setup-create-join | nightly | yes | 150 s | Create, three joins, refusals, slots, looks; the gate on, cleared and off (R1-13's assertions) |
| RACE-2-import-generator-folder | nightly | yes | 70 s | Import with every refusal of 7.15, rival map objects in the TS |
| RACE-3-rival-ghosts-flight | nightly | no (launch) | 150 s | Rival ghosts in flight are no vessel, collider or CommNet node (seam view) |
| RACE-4-isolation-whole-save | nightly | no (pad) | 300 s | 6.4 whole-save isolation (the one whole-save lane) |
| RACE-6-two-saves-in-turn | nightly | no (kRPC flight) | 700 s | Real exporter to real importer; quickload drops a shared flight |
| RACE-7-holder-change-folder-update | nightly | yes | 90 s | Holder change and its message |
| RACE-8-export-self-heal-offline | nightly | yes | 70 s | Offline export, seq continuity, self-heal |
| RACE-9 / 10 / 11 batch hosts | nightly | yes | 3 x 90 s | The race in-game categories |
| RACE-12-flown-rival-campaign | operator | no (kRPC) | 400 s | A flown rival folder fixture |
| RACE-13-paired-copies | operator | yes | unmeasured | Two live writers on one folder |

All estimates are from the reference p50s of section 3 (boot floor 45 s, a main-menu round
trip like SWS-1, a B1 or B2 flight), not measured; each lane's first flight replaces them.

Capacity: daily gains about 45 s (+3% on 0.42 h); nightly about 1800 s (+4% on 12.5 h);
operator about 400 s. The race lanes are small next to the prerefactors: P2b-10 wants a full
run as its proof ("about 21.5 h measured p50 plus 169 untimed; plan on a day and a half",
`harness/README.md:1652-1728`); P2c and P2a name one nightly run, P3 and P3b named lanes and
P6 a daily run (the prerefactor plans own those choices). That is where the PC hours of the
project go, not the RACE lanes.

Parallel flights: every RACE lane except RACE-3, RACE-6 and RACE-12 drives the seam only
and needs no kRPC port, so two or three copies can run them side by side; the expected gain
is near the copy count for those lanes (an estimate; the sibling work measures it). The
kRPC lanes stay limited by the two hard-coded ports and the one GPU
(`harness/README.md:1492-1553`) until that work gives each copy its own ports.

### What needs the owner's PC

Every lane task (T-07, T-13, T-24 to T-29, T-32 to T-34), each with a reading run
before any block is armed. Nothing headless does.

## 7. Gates and registrations

In the order the first feature commit meets them (the R1 module commit, R1-01, meets 1 to 11; this
plan's own tasks meet 12 to 16):

| # | Gate | Where it reds | File to edit | Re-pin |
|---|---|---|---|---|
| 1 | Placement of every production file | `scripts/arch` DLL-free suite | `scripts/arch/modules.toml` rules (P12 for the Controllers name pattern) | by hand |
| 2 | Every module has a layer | DLL-free suite | `modules.toml:511` `[layers]` | by hand |
| 3 | Forbidden edges of 15.4 | DLL-free text scan and `test_semantic_layers.py` | `modules.toml:562` | by hand |
| 4 | Headless modules (`RaceCore`) | `test_ilheadless.py` | `modules.toml:531`, `scripts/arch/headless_pins.toml` | `python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 5 | Backward layer pairs | `test_semantic_layers.py` | `scripts/arch/layer_pins.toml` | `python3 scripts/arch/layers.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 6 | Knot canary | `scripts/arch/test_archview.py:4090` | that line | re-derive by hand |
| 7 | Semantic residue ratchet | `scripts/arch/test_semantic.py:623` | the bound (may not rise) | by hand, per `docs/dev/arch/README.md` |
| 8 | Statics written by another type; uncalled methods; complexity | `test_ilanalysis.py` | `scripts/arch/facts_pins.toml` | `python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 9 | Static lifetimes | `test_static_lifetimes.py`, `SaveScopedSessionResetTests` | `scripts/arch/lifetimes.toml`, `lifetime_pins.toml`, `SaveScopedSessionResetTests.cs:25`, `scripts/session-reset-allowlist.txt` | `python3 scripts/arch/lifetimes.py Source/Parsek/bin/Debug/Parsek.dll --write-pins` |
| 10 | Log tag map (report-only; lane health) | `test_iltags.py` | `scripts/arch/log_tags.json` | `python3 scripts/arch/logtags.py Source/Parsek/bin/Debug/Parsek.dll --write` |
| 11 | OnSave / OnLoad characterization | `ParsekScenarioOnSaveCharacterizationTests`, `...OnLoad...` | `Fixtures/SourceCharacterization/ParsekScenario.*.tokens.txt` | regenerate per the test's message, reviewed |
| 12 | Load policy | `LoadReconcilePolicyTests`, `LoadReconcileWiringGateTests` | `LoadReconcilePolicy.cs:242` and the decision table | by hand |
| 13 | Background threads (copy worker, 15.9) | `GrepAuditTests` | `scripts/background-threads-audit-allowlist.txt` | by hand |
| 14 | Save formats (`race-link`, `race-exchange`) | `SaveFormatTests`, `SaveFormatGoldenGateTests` | `SaveFormat.cs` (R1-12 registers both rows at 1); frozen `Fixtures/FormatGolden/v2/` (R1-12: the race save, `race.cfg`, `player.cfg`, `local.cfg`; `formats.txt` names both rows at 1) and `v3/` (R3E-04: a standing, its payload and an own mirror, no new row or bump) | each written once by its generator through the T-01 driver, env-gated (OV-15) |
| 15 | GUI gates | GUI census and scale gates | the GUI slice's list (P11) | other slice |
| 16 | Harness: verbs, kinds, lanes | `test_hlib`, `test_lanecatalog`, `IngameBatchWiringGroupTests`, `CommittedBatchTallySourceSyncTests` | `TestCommandVerbs.cs:56` (R1-08, R1-10, R1-11), hlib tables (`:887`, `:1286`, `:1544`, `:1782`; `set-folder` rows by T-12), `lanecatalog.py:75`, `docs/dev/flight-profiles.md`, `registry.toml`, `INTERIM_PIN_IDS`, `GROUP` | `python harness/tools/lane_catalog.py` |

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
gates of this plan: the gated leak scan and save equivalence of T-21 / T-27, and T-17's
fixture currency test.

Live roots (A5): `docs/dev/arch/README.md:292-295` counts in-game tests and TestCommands
tooling as live roots, so a feature slice may make a member live with an in-game cell of its
category or with one of this plan's seam reads (`ListHandles kind=sites|rivals|board`)
before its production caller lands; an xUnit test does not count. A category that no
committed spec pins yet needs no tally re-pin; T-29 pins it later.

Uncalled-method ratchet (A1): T-01, T-02, T-10, T-16, T-17, T-18 and T-31 add test-project
code only, and T-03 to T-06 and T-21 harness code only; the production members they call
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
- **Tool marks hide scoring defects live.** Every harness-flown flight scores zero, so a
  scoring defect in the local player's own path is caught only headless. Noticed if the
  owner's play test finds what T-31 did not.
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

1. **Design 11's second lane cannot pass as written.** 7.12 makes the seam, the test runner
   and injected recordings mark a race save for good, and a marked flight scores nothing. A
   harness-flown B never takes a milestone from A. The plan proves the holder change with
   generator-built rival folders (rivals carry no local mark) in RACE-7, and asserts the
   marked, non-scoring rows in RACE-6. Settled: harness-flown race saves stay tool-marked,
   with no "test race" flag; scoring, holder changes, no retakes and the end date are
   proven headless and on T-16 / T-17 folders, R6-11's proofs included (OV-08).
2. **Injected recordings are written offline** (`run.py:542` runs `inject-recordings.ps1`
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
5. **The seam's `LoadGame` bypasses the main menu** (`run.py:1450-1451`), as other mods
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
9. **One `RunTests` per spec** limits per-site in-game checks; per-site values come from
   `ListHandles kind=sites` (T-07).
10. **Known-gate 6:** a race save must be entered through the Space Center route
    (`design-autotest-command-seam.md:1010-1025`); the plan assumes `ParsekRaceScenario`
    shares `ParsekScenario`'s need, so every race lane loads with `scene=spacecenter`.
11. **The harness may not touch a folder outside the instance** (`harness/README.md:110`),
    and design 11 does not say where the "temporary shared folder" lives. The plan stages
    it inside the instance, so a folder elsewhere on disk is never exercised. Settled: every
    race lane uses `${raceShared}` = `<instance>/rs/<n>` (OV-02).
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

1. **Test races and tool marks.** Every race the harness plays is marked by the test tooling,
   so its own flights never score. Options: (a) keep it so; prove scoring headless and with
   generator-built rivals, which carry no mark (recommended: no product change, and nothing
   a player can misuse); (b) a "test race" flag only the seam can set, shown to every
   player of that race, under which marks show but do not zero the score.

   Assumed until the owner says otherwise: (a); test races stay tool-marked (OV-08).
2. **The whole-save isolation lane compares saves with a small masked list of time values.**
   Options: (a) accept a canonical comparison where only listed time values may differ,
   each with a written reason (recommended); (b) insist on byte-identical saves, which
   forces a session where no time passes and so cannot see a rival flag or a rival landing.

   Assumed until the owner says otherwise: (a), applied in T-05 and T-27.
3. **Two game copies racing on one PC.** Once parallel flights exist, a lane can run two
   copies writing one folder at the same moment. Options: (a) build it as an operator lane
   (recommended: the only proof of two live writers short of your two-PC test); (b) leave
   concurrency to the two-PC play test.

   Assumed until the owner says otherwise: (a); T-34 stays optional and is built only once
   parallel flights exist.
