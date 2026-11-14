# Base Parsek refactoring before competitive multiplayer

Date: 2026-11-14. Status: analysis only, nothing here is started. Feature design:
`docs/dev/design-competitive-multiplayer.md` (its section 15.11 carries the resulting list).

Question from the owner: do we need extra refactoring of base Parsek to make the multiplayer
features easier to implement?

## 1. Answer

Yes, but it is a short, mostly small list, and it is not the list the design had.

- **Eleven changes to base Parsek should land before any feature code** (section 4). Five are
  small, six are medium, none is large. Each is a change with no behavior change in single
  player, proven by the existing suites and lanes.
- **Seven additions to base Parsek come with the feature** (section 5; none is larger than
  medium): new facts that
  single player does not record or compute today (whether a flight used cheat tools, when a
  deleted vessel stopped existing, the dates a milestone was reached), and small seams on
  ghost visuals.
- **Six things the design called refactors are not needed** if the design makes a simpler
  choice (section 6). The largest saving: a rival flight is turned into plain positions when
  it is loaded, so the code that places a ghost relative to another vessel never runs for a
  rival and does not have to be generalized.
- **Most of what multiplayer needs is already in the right shape** (section 7): the playback
  engine is an instance with one store lookup, ghost building takes the playback interface,
  CommNet is not wired to the engine, the ghost map vessel is already typed on the interface.

The design's largest worry was wrong in a useful direction. It said the playback path reads
the player's own stores in about 120 places. That is the count for the whole path. The part
a second engine actually runs holds about 28 of them, in 8 lookup functions, and 5 of those
functions are copies of one lookup. What the design missed instead: the code that places
ghosts keeps its working state on the flight scene object, keyed by position in the
player's own list, so two lanes would overwrite each other. That, not the store reads, is
the real preparation work on the playback side.

Sizes used below. **S**: one small change, under about 15 call sites. **M**: one change of
15 to 60 sites, or one that touches saved bytes or a per-frame path. **L**: several changes.

## 2. Method

Four read-only inventories of the 1.1 code (commit c48b137 of the design branch, which
changes no source file), each returning counts and `file:line` evidence:
playback path; space center position, flags and kerbal looks; export data; windows, menus,
save creation and the build gates. Counts are token counts on comment-stripped source. The
headline findings were re-read in the code afterwards (the four constants, the lookup
copies, the positioner's host state, the cast sites, the table window, the create-save
code, the effective-set filter, the forbidden-edge parser, the spec literals). Sizes are
judgments from the site counts, not measurements of work. Nothing was built or flown.

Paths are under `Source/Parsek/` unless they start with `scripts/`, `harness/` or `docs/`.

## 3. What was found, by area

### 3.1 Ghost playback

| Piece | Lines | What a rival lane meets |
|-------|-------|-------------------------|
| `GhostPlaybackEngine*.cs` (13 files) | 10,570 | One store-reading function, `TryFindPlaybackRecordingTree` (`GhostPlaybackEngine.SectionPositioning.cs:354`), reached only for debris. It also writes the global `DiagnosticsState` at 16 sites (the playback budget is "last writer wins", so a second engine overwrites the first), and its per-frame spawn throttle is a field of the instance, so two engines double the build budget |
| `IPlaybackTrajectory.cs` | 96 | 37 members, one setter: `LoopSyncParentIdx` (:82), a position in the player's own list. `ReaimedTrajectory` already proves a non-`Recording` implementation runs in production |
| `ParsekFlightGhostPositioner.cs` and two partials | 7,071 | Four store lookups (:509, :1677, :4551, and the watch-protection check in `ParsekFlight.GhostPositioner.cs:323`). About 60 references to 14 members of its `ParsekFlight` host: the orbit cache (`Dictionary<long, Orbit>`, key `index * 10000 + segment`, `ParsekFlight.cs:2503`), the ghost position entries the late update re-applies, five log-once sets, the watch state, the loop units. A second positioner on the same host reads and writes the first one's entries |
| Relative anchor resolvers | 3,243 | `RelativeAnchorResolver.cs` reads no store, but is typed on `Recording` and `RecordingTree` (52 mentions) and reads chain and tree fields that are not on the interface. Three more files carry their own copy of the tree lookup |
| `GhostPlaybackLogic*.cs` (14 files) | 15,465 | The engine calls only the flag pair from the store-bound half. The two casts back to `Recording` (`GhostPlaybackLogic.PartEvents.cs:1320`, `:1373`) read segment events only for flag pick-ups, which remove real flag vessels and write saved tombstones |
| `ParsekFlightPlaybackHost.cs`, `ParsekPlaybackPolicy.cs` | 5,529 | Not reused. A rival host is new code of roughly 150 to 250 lines, once the late-update helpers take a lane's state |
| `GhostMapPresence*.cs` (16 files) | 16,575 | 36 static collections (11 keyed by position in the own list). The part rivals need is about 400 lines in `GhostMapPresence.ProtoVesselLifecycle.cs` (:1800-1994, :2077, :1345), already typed on the interface. The guard check `IsGhostMapVessel` is called at 120 sites in 61 files and tolerates a ghost with no list position (chain ghosts are the precedent). Three patch sites assume a guarded vessel belongs to the player's own timeline (`Patches/GhostVesselLoadPatch.cs:266`, `:526`; `Patches/GhostTrackingStationPatch.cs:1012`) |
| `WatchModeController*.cs` | 5,232 | Identity is a position in the own list (`watchedRecordingIndex`, :95): 13 list reads and 32 uses of the host's engine |
| `ParsekKSC*.cs` | 3,387 | The Space Center scene does not use the engine at all. It has its own slots and a single-ghost path typed on `Recording` (43 lines) |
| `ParsekTrackingStation.cs` | 2,919 | No engine either: map presence plus markers keyed by list position |
| Labels, icons, colors | | No style concept exists. The map icon is drawn in a fixed grey (`MapMarkerRenderer.cs:350`), Parsek never sets a ghost's orbit line color, the trajectory line is a fixed grey (`Display/GhostTrajectoryPolylineRenderer.LegDraw.cs:420`). About 6 sites |
| Ghost budget | | There is no ghost-count cap today. `FrameContext.externalGhostCount` is written and never read; `GhostPlaybackState.simplified` is never set |

The five copies of "find the tree that holds this recording": the engine (:354, pending tree
first), the positioner (:4551, committed trees first), `RecordedRelativeAnchorPoseResolver.cs`
(:165, with a by-id lookup at :203), `Rendering/ProductionAnchorWorldFrameResolver.cs` (:411)
and `GhostMapPresence.StateVectors.cs` (:749). The two orders differ, which only matters when
one id is in both places (a quickload resume, a Re-Fly), so a merged lookup has to keep each
caller's order.

### 3.2 Space center position, flags, kerbal looks

- **The position problem is small.** Four constants and a radius hold the pad and runway
  position (`SpawnCollisionDetector.cs:216-232`), with one live reader
  (`ClassifyKscExclusionZone`, :271) and about 12 production sites in all. Everything else
  matches a launch site NAME or reads stock live. The optimizer's launch-site rule and the
  route origin proof read no position, so the design's P1 overstated the reader set. 64 test
  references and three harness lanes pin the constants and the log wording (EX-1, and LF-1 and
  LF-2, whose rover parks 59.7 m from the runway threshold, just outside the 50 m circle).
- **A cache that is never reset.** `LaunchSiteExclusionZones.cachedRuntimeCircles` (:130)
  holds the Making History sites for the life of the process, and a partial read is frozen
  with fallback rows merged in (:237-247). Only a test hook clears it.
- **The runway is one point** (its west threshold), not a strip, and the distance helper does
  not wrap longitude (`SpawnCollisionDetector.cs:385`), which starts to matter at a site near
  the date line (162.3 W).
- **Flags**: the replay plants a real flag vessel (`GhostVisualBuilder.SpawnFlagVessel`,
  saved with the game). There is no reusable flag visual, so a rival's readable flag is new
  code. A trajectory that hands the engine no flag events is already a no-op.
- **Kerbal looks**: a ghost kerbal is built from the snapshot's part name and shares the
  prefab's materials (`PartInfoBuilders.cs:766`), so every ghost kerbal wears the default
  texture today; suit and color are captured nowhere. A missing suit part makes the whole
  ghost fall back to a 12 m sphere (`GhostPlaybackEngine.GhostVisuals.cs:163`), not a
  skipped part.

### 3.3 Export data

- **Trajectory files decode into a `Recording`** (`TrajectorySidecarBinary.Read(path,
  Recording, probe)`, :290). The private readers already take plain lists, so a variant that
  fills a plain value is mostly plumbing; two helpers of the text codec stay typed on
  `Recording` (`TrajectoryTextSidecarCodec.cs:601`, `:658`).
  Loading from a path outside the save exists only under test-hook names
  (`RecordingSidecarStore.cs:190`, `:910`, `:2092`). Snapshot files already decode into a plain
  node.
- **The recording's saved metadata is not a public projection.**
  `RecordingTreeRecordCodec.SaveRecordingInto` writes private state next to what a ghost
  needs (funds at launch, reservations, crew end states, merge state).
- **Paths and ids.** `RecordingPaths` resolves everything under the loaded save through a
  private helper (:329); the only root parameter is a test seam. `ValidateRecordingId` uses
  `Path.GetInvalidFileNameChars()` (:302), so the same id can pass on Linux and fail on
  Windows.
- **The effective set hides an open Re-Fly.** `EffectiveState.ComputeERS()` drops the subtree
  of an active Re-Fly session (:2092, :2123). An export taken during such a session would
  withdraw flights rivals already have.
- **Milestones**: no query returns "the credited first of each milestone, with its date and
  flight". The nearest block is `CommittedMilestoneReach.CollectRowsAtOrBefore` (:177). Six
  kinds of milestone row carry no flight id (reached outside a recording, in a clock-jump
  scope, world records, recovery with no owner, tags cleared by a supersede, pre-ledger
  seeds).
- **Leftovers**: the pieces exist (`RecordingTree.IsSpawnableLeaf`, `VesselIdentity.Resolve`,
  `GhostChainWalker`, `RecoveredRecordingEvidence.IsRecovered`), but nothing composes them
  into "this vessel stands from date A until date B". A vessel deleted in the Tracking
  Station or destroyed outside a recording leaves no fact on a committed flight
  (`ParsekScenario.VesselLifecycle.cs:336` updates pending trees only).
- **Cheats**: detection and log lines exist (`GravityHackDetector`, `CheatTeleportPatch`,
  `IsCheatCurrencyReason`); nothing is stored per flight.
- **Hashes**: the content hash is the first 64 bits of SHA-1 (`SidecarVersions.cs:30`). Good
  for file naming; too short to be the tamper chain of design 7.15. In-memory hashes are
  valid for an export only while the recording's files are not dirty.
- **A ghost snapshot file may not exist.** In alias mode the ghost visual is the vessel
  snapshot (`RecordingSidecarStore.cs:638`), which the design says is never shared.
- **Save handling**: `scripts/collect-logs.py` (:185) copies every `Parsek/<dir>` beside a
  save, and the test-batch crash reconcile deletes and restores the whole `Parsek/` folder
  (`ParsekScenario.TestBatchReconcile.cs:187`), so both would sweep up a `Race/` folder.
  Seven places hand-write "find the Parsek scenario node of a game before scenarios load".

### 3.4 Windows, menus, save creation

- **The "log-table window template" is not a template.** `UI/StructureListWindowUI.cs` (680
  lines) is one concrete window with four fixed columns (:122-125), label-only cells and a
  mode enum (:30). A board with a column per player, colored cells and hovers fits none of
  it.
- **A new window is registered in about 17 places**, the worst being 100 literal
  `windows=11` pins in 25 harness specs.
- **Creating and starting a save from code exists only in test tooling**
  (`TestCommands/ParsekTestCommandAddon.NewGame.cs:254-304`).
- **Nothing of Parsek draws at the main menu**, and no dialog in the mod has a typed field.
  Both are new code for the setup dialog.
- **Only the Flight and Space Center scenes host the Parsek UI** (`ParsekFlight.cs:1448`,
  `ParsekKSC.cs:259`). The Tracking Station and the editors do not.
- **Settings**: the window opens 280 wide (`UI/SettingsWindowUI.cs:107`), too narrow for a
  folder path, and settings live either in the save or per install; a machine-local file
  beside a save has no precedent.

### 3.5 Build gates and the harness

- `[forbidden]` edges take one `"From -> To"` string each (`scripts/arch/layers.py:225`,
  `scripts/arch/archview.py:6517`). The design's `A -> B | C` lists are rejected by both
  parsers; written out they are 71 edges against 2 today.
- `ParsekRaceMenu.cs` and `ParsekRaceScenario.cs` match no placement rule (the Controllers
  rule at `scripts/arch/modules.toml:322` is a file-name pattern that does not cover them).
  A root file with no rule is reported as unplaced and reds the map suite
  (`scripts/arch/archview.py:7179`), so they need a rule in the change that adds them.
- `UiSurface` only expresses Basic and Advanced; it cannot say "race saves only".
- `KnownLoadInitiators` gates `GamePersistence.LoadGame(` per file. Creating and starting a
  new game is not gated by it.
- A production method that only tests call counts as uncalled, and that bound may not rise
  (`scripts/arch/facts_pins.toml:75`). A pure rules module landed before anything calls it
  raises the bound.
- A second scenario module's statics are outside the cold-load roots, so they read as never
  cleared.
- The harness can already run two saves in one process (lane SWS-1). It has no verb that
  creates, joins or syncs a race, and cannot name a folder outside the game instance.

## 4. Refactor before the feature

Each lands on its own with no behavior change. The id column is the design's 15.11 numbering.

| Id | Change | Size | Evidence | Risk and proof |
|----|--------|------|----------|----------------|
| P1 | One source for the working space center's position: a `HomeSpaceCenter` value in `Core` (pad, runway threshold and end, strip half-width, a version) that returns the stock numbers unless set; a shared launch-area predicate (circle or strip, longitude wrapped) that single player's 50 m rule calls; a reset for `cachedRuntimeCircles` | M | 4 constants, about 12 production sites, 64 test references, lanes EX-1, LF-1, LF-2 | Low. The log wording EX-1 pins must not move |
| P2a | The catalog port: `IPlaybackCatalog` (find a recording by id, find the tree holding an id, the pending tree) on the floor; the five copies of the tree lookup and the two by-id lookups call it; the own lane's catalog wraps the stores | M | About 28 store reads in 8 functions | Medium. The copies search in different orders; each call keeps its order. Per-frame path: all flight lanes |
| P2b | Lane state for the positioner: the orbit cache, the ghost position entries, the log-once sets, the watch query and the loop-unit lookup move from `ParsekFlight` fields to one per-lane object the positioner and the late-update helpers of `ParsekFlightPlaybackHost` are handed | M | About 60 host references to 14 members, plus about 10 helper methods | Medium. The largest one. Per-frame path, index-keyed caches. Mechanical re-homing provable with `scripts/refactor/il_compare.py`, then all flight lanes |
| P2c | `LoopSyncParentIdx` leaves `IPlaybackTrajectory` (the optimizer keeps it beside the list) and the engine gets its own diagnostics sink and a shared spawn budget | S | 2 writers, 2 readers; 16 diagnostics writes | Low |
| P3 | Extract the ghost-map-vessel primitive (build, register in the guard set, remove, strip at save) into a small service, and give the three patch sites a lane-neutral "whose ghost is this" query | M | About 400 lines already typed on the interface; 3 patch sites | Low to medium. Map and Tracking Station lanes |
| P3b | A lane-qualified watch target (engine, trajectory list, index, id) in `WatchModeController` | M | 45 sites | Medium. Camera code. Can be deferred with rival watching (section 6) |
| P6 | Decode a trajectory file into a plain value, with `Recording` as one consumer, and a production (not test-named) load from a given path | M | `TrajectorySidecarBinary.cs:290`, private readers :540-824 | Medium. Saved bytes: the format golden gates and every lane's analyzer pass prove it |
| P7 | The pure id check moves to the floor with a FIXED character set; `RecordingPaths` gets a public save-root accessor | S | 32 callers of the check keep calling the same name | Low. An id that today passes only on Linux would be refused; none is produced (ids are GUIDs) |
| P10 | Lift the create-and-start-a-save core out of `TestCommands` into a production file both the test verb and the race setup call | S | `ParsekTestCommandAddon.NewGame.cs:254-304`; 3 specs; `NewGameSourceSyncTests` | Low. The three NewGame lanes |
| P11 | Harness specs derive the window count instead of pinning `windows=11`; the two near-identical `ParsekUI` constructors share one helper | S | 100 literals in 25 specs | Low. Mechanical |
| P12 | `[forbidden]` accepts a list on the right side (`A -> B | C`) in both parsers; the Controllers placement rule covers `ParsekRace*` files | S | `layers.py:225`, `archview.py:6517`, `modules.toml:322` | Low |

Five of these are worth doing even if multiplayer never ships: P2a (one lookup instead of five
that disagree on order), P1's cache reset and longitude wrap, P7 (an id check that differs by
operating system), P10 (production-worthy code living in test tooling) and P11.

## 5. Additions to base Parsek that come with the feature

Not refactors: single player has no such fact or seam today. Each is additive (no save
format bump under the save-format policy) and inert outside a race.

| Id | Addition | Size | Why it must be in base code |
|----|----------|------|-----------------------------|
| A1 | One pure query beside `CommittedMilestoneReach`: the reach rows per milestone id over the effective ledger, earliest first (the first is the one the once-ever walk credits), each with its date and flight id | S | The board's input (design 7.8). VERIFY with it: what a milestone does when the flight that reached it is superseded by a Re-Fly (its row is tombstone-eligible, `GameActions/TombstoneEligibility.cs:87`) |
| A2 | One pure "stands from date A until date B" query composing the four existing blocks, plus a recorded fact when a committed flight's vessel is deleted or destroyed outside a recording | M | Leftovers (design 7.7). The second half is new capture: today that vessel's end is not recorded anywhere |
| A3 | A per-flight "tools used" fact (gravity hack, cheat teleport, cheat currency), stored on the recording so it follows optimizer splits and merges | S to M | Disclosure (design 7.12, owner decision D3). The detectors exist; nothing is stored |
| A4 | Build options on the ghost kerbal build: a part-name substitute before the part lookup, and a material override after the materials are cloned | S to M | Looks (design 7.4). Without the substitute a missing suit part gives a 12 m sphere |
| A5 | Neutral ghost seams: a per-trajectory style (label prefix, color) read at about 6 sites, and a ghost budget object | S each | Colors and the shared cap (design 15.6). Neither concept exists today, so this is new, not extracted |
| A6 | Window chrome shared by the table windows (id, rectangle, input lock, resize, UI scale, hover strip, Close) | S | The Space Race window. The board's grid is new drawing either way |
| A7 | `collect-logs.py` lists `Parsek/Race/` instead of copying it; the test-batch crash reconcile leaves it alone; one shared "find the scenario node before scenarios load" helper replaces seven copies | S | Other players' files must not ride into log bundles or be restored from a test snapshot |

A1, A2 and A3 record or compute things that cannot be rebuilt afterwards. Race saves are
created after the feature exists, so nothing is lost there. For the later optional
"recorded opponent from any playthrough", flights recorded before A2 and A3 ship will read as
"not known".

## 6. Refactors the design can avoid

| The design said | Measured | Cheaper choice | What it costs the player |
|-----------------|----------|----------------|--------------------------|
| Relative anchors resolve through the catalog (15.6) | `RelativeAnchorResolver` is 2,236 lines typed on `Recording`, reading chain and tree fields off the interface: generalizing it is the one LARGE item | The rival loader turns every relative stretch into plain body-fixed positions. Both recorders already keep that second copy beside a relative stretch (`FlightRecorder.EnvironmentAndTrackSections.cs:435`, `FlightRecorder.PointCommit.cs:236`, `BackgroundRecorder.TrackSections.cs:457`), and single-player debris already plays from it. The rival lane then never asks for an anchor | A rival's docking or a kerbal beside its lander may show a small wobble between the two ghosts. To be looked at in a spike (S10), which also counts how many relative stretches in real saves carry a complete copy; a stretch with fewer than two stored positions is not drawn |
| P8: segment events through the interface (15.5) | The only segment events playback reads are flag pick-ups, which delete real flags and write saved tombstones | Drop P8. Keep the casts: they are what keeps a non-`Recording` trajectory away from those side effects | Nothing |
| A public projection of the saved recording node | The writer mixes private state in and its output is byte-pinned | No split. The standing has its own codec in `RaceCore` for the dozen public fields (it may not name the `Recording` module anyway) | Nothing |
| Export reads the effective set | The set hides an open Re-Fly's subtree | No export while a Re-Fly session is open (a flight is in progress, and the design already exports only after a commit) | Nothing |
| A second slot set in the Space Center scene (15.6) | That scene has no engine and a `Recording`-typed path: about 43 lines to retype plus slots | First build: rival ghosts in flight, map view and the Tracking Station; the Space Center scene shows rival scenery only | A rival flying over your space center is not seen from the Space Center scene. Rare: the sites are a quarter of the planet apart |
| `IGhostPositioner` moves to the floor (15.6) | The scene controllers compose the lane, and a rival host placed with them names it freely | No move | Nothing |

One more that can be deferred, with a visible cost: **rival watching** (P3b). The camera
follow is keyed by position in the player's own list at 45 sites. Without it, a rival flight
far from your own vessel is seen on the map only. It is the most fragile code on this list,
so it is the natural one to trim if the first "see your rivals" phase needs trimming.

## 7. Already fine

- The playback engine: an instance, its own index space, one store lookup, no CommNet, no
  ledger, no roster. 22 in-game tests already build extra engines.
- Ghost building (`GhostVisualBuilder*`): no store reads, entry points take the interface.
- The effects half of `GhostPlaybackLogic` (5 files): store-free.
- CommNet: nodes are registered by the scene host only; a guarded map vessel gets none.
- The map-vessel guard: 120 call sites already skip any guarded vessel, and patches already
  handle a ghost with no list position.
- A trajectory with no flag events and no loop: valid input today.
- Snapshot decoding, the file-name scheme `<id>.<hash><suffix>`, `SaveFormat.Classify`,
  `RecordingTree.Load`, `VesselIdentity.Resolve`, `RecoveredRecordingEvidence`, safe file
  writes, the difficulty settings capture.
- Two saves in one process, the main-menu transition reset, reading a game's node before
  its scenarios load, screen messages across a load, conditional elements in the main
  window, UI scale inside the existing hosts.
- Recording ids are GUIDs, so the caches keyed by bare id do not collide across players.

## 8. Order of work

No step starts before the owner says so. Each row is one change with its own review.

| Step | Changes | Why here |
|------|---------|----------|
| 1 | P12, P11 | Tooling only. Makes every later change cheaper to gate |
| 2 | P7, P1, P10 | Small and independent of each other; P1 and P10 unblock the sites and the setup dialog |
| 3 | P2c, P2a | The interface first, then the lookup, so P2b moves code that already has no store reads |
| 4 | P2b | The large one, alone, with every flight lane behind it |
| 5 | P3, then P3b | Map presence, then watching |
| 6 | P6 | Saved bytes; last so nothing else is in flight when the golden gates run |

Steps 1 and 2 can run side by side. Steps 3 to 5 touch the same files and go one at a time.
The additions of section 5 land with the phase that first uses them (A1 to A3 with export,
A4 and A5 with looks and the rival lane, A6 with the window, A7 with the first race save).

## 9. Corrections this forces in the design document

Folded into `design-competitive-multiplayer.md` in the same change:

- 15.6: the store-read count (about 28 on the path a rival lane runs, not 120), the lane
  state the positioner needs, the Space Center scene and Tracking Station having no engine,
  36 static collections in `GhostMapPresence`, the budget and the style being new concepts.
- 15.5: P8 dropped and why; relative stretches flattened at load; the cast sites.
- 15.4: the forbidden-edge list syntax; `Timeline` is a presentation module.
- 15.7, 15.14: no `UiSurface` key; the load-initiator gate covers loads only; the second
  scenario module and the cold-load roots; the uncalled-method ratchet and "pure core first".
- 7.2, 7.13, 15.12: the setup dialog is the first with typed fields; the window is new
  drawing on shared chrome, shown in the Flight and Space Center scenes.
- 7.3, 7.4: the cache-reset reason; the sphere fallback; ghosts show no suit color today.
- 7.5, 7.8, 7.12, 7.15: no export during a Re-Fly; alias-mode ghost snapshots; the Re-Fly
  milestone VERIFY; all cheat capture is new; the chain hashes with SHA-256, not the 64-bit
  file-name hash.
- 15.11: the list of section 4, and phase R0 no longer starts with this inventory.

## 10. Found on the way, not about multiplayer

Not verified as defects; candidates for the todo list if the owner wants them filed.

- `LaunchSiteExclusionZones.cachedRuntimeCircles` freezes a partial first read for the
  process.
- `RecordingPaths.ValidateRecordingId` accepts different ids on different operating systems.
- `FrameContext.externalGhostCount` is written and never read; `GhostPlaybackState.simplified`
  is never set.
- The engine and the positioner look up a recording's tree in opposite orders (pending first,
  committed first).
- `.claude/CLAUDE.md` says the engine has zero `Recording` references (4 lines do) and that
  specs pin 106 of 112 in-game test categories (there are 141 now).
