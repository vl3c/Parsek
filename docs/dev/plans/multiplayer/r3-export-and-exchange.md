# Race export and exchange - implementation plan

Status: PLAN (2026-11-14), not started. Design sections covered: 4 (4.1 to 4.4), 6.1 (the
standing, `RaceLocal`, the own mirror and the rival cache files), 6.2 (the two new facts),
6.3, 6.5, 7.5, 7.15 (the simple version), 9, 15.3, 15.9, and additions A1, A2 and A3 of 15.11.
Phase R3, the export and exchange half (the rival world half is a sibling plan).

## 1. Goal and scope

Built by this plan:

- **The exchange vocabulary in `RaceCore`** (headless): content hashes (a 64-bit name hash
  and a SHA-256 proof), the stamp rule added to R1-08's `RaceTimestamp`, the strict
  file-name grammar of the shared folder, the path layout and the longest-path figures (the
  short cache directory names and their collision check join R1-08's `RaceIds`), the
  `Standing` model and its codec,
  the `PublicState` model and its digest. `raceFormat` is R1-03's `RaceFormat.Current`.
- **The ports of 15.3** that the exchange needs, bundled as `SyncPorts` (R3E-04): R1-08's
  `IRaceFolder` (this plan adds list, exists, move and delete), `IRaceClock`,
  `IPublicStateSource`, the own half of `IRacePayload`, and `IRaceTransport` (joins the
  bundle with R3E-16).
- **The pure sync pipeline** `RaceSync.Run`: export (digest, payload first, standing last,
  seq and stamp rules, the chain), copy-out oldest first, copy-in of rival standings and their
  payload, completeness, the witness log, the chain and receipt checks, the neutral mark,
  self-heal of the own subfolder, the keep rule and pruning, the "flights left the race"
  message, and the just-joined exception.
- **`RaceExport`**: `RaceExportSource` over ERS and ELS through `EffectiveState`, the tree
  projection, the public ghost snapshot, the own payload reader, the milestone, leftover,
  tool and disclosure blocks.
- **`RaceWorld` adapters and driver**: move and exists on R1-08's `RaceFolder`, the system
  clock, the main-thread transport (which absorbs R1-10's `IRaceSharedFolder`), the copy
  worker (only with the owner's yes), and `RaceSyncDriver`, which runs the pipeline at the
  sync ticks of 7.5, applies its result and is the one producer of race screen messages.
- **Base additions**, each additive, written in race saves only:
  A1 (milestone reach rows), A2 (a ledger row for a vessel that ended outside a recording,
  and the "stands from A until B" query), A3 (the per-flight tool-use fact and its capture,
  plus the save-level debug-completion fact), plus the neutral capture switch that keeps A2
  and A3 silent in single-player saves.
- Golden folder `v3` for the exchange format (the `race-exchange` row and `v2` are R1-12's).

Explicitly not built here:

- Turning a verified rival standing into rival recordings, the rival store, the rival ghost
  lane, leftovers and flags on screen, launch areas, the isolation lanes (sibling plan
  "R3 rival world", called R3RW below). The seam: this plan hands R3RW one
  `RivalStandingVerdict` per rival (the standing to apply, its local cache directory, its
  status and marks). Everything from "a complete, verified rival standing is in the local
  cache" onward is R3RW's.
- The board, the milestone ladder, scoring, holder-change messages, the Space Race window
  (R4); disclosure display and the end date's final board (R6); signatures and flight checks
  (R7); the setup dialog, `race.cfg`, `player.cfg`, `local.cfg` codecs and the race mark (R1).
- Any co-op feature. Any release, version bump or tag.

## 2. Starts after

| Needed | Why | Blocks |
|--------|-----|--------|
| P7-01 (pure id check on the floor, fixed charset) | `RaceCore` validates every id that becomes a path (through R1-08's `RaceIds`); no P7-02: `RaceWorld` takes the local race directory from R1-08's `RaceFolder` (OV-23) | R3E-01 |
| P12-01, P12-02 (`[forbidden]` list form, placement rules) | the race edges of 15.4 that this plan's files must respect | R3E-01 |
| R1-07 (A7 parts 1 and 2: `collect-logs.py` lists `Parsek/Race/`, the test-batch crash reconcile leaves it alone) | the own mirror and rival cache hold other players' files | R3E-01 (first file under `Race/`) |
| R1-01 (the three module rules: layers, `RaceCore` headless, forbidden edges), R1-06 (the 15.4 grep gates and the log tag list) | every file of this plan lands in those modules | R3E-01 |
| R1-03 (race mark, `RaceFormat.Current` and `Classify`, `RaceFormatPinTests`, the scenario's child Save / Load hook `IRaceScenarioChild`), R1-04 (`RaceGate`) | the format number every standing carries; the persisted facts child | R3E-01, R3E-04, R3E-06, R3E-15 |
| R1-08 (`RaceSession` in `RaceWorld` and its Controllers composition point; `RaceLocal` codec with `lastStandingDigest`, `firstImportDone`, `lastSyncRealTime`; `RaceManifest` and `PlayerManifest` codecs; `IRaceFolder`, `RaceFolder` and the in-memory folder; `RaceTimestamp`; `RaceIds`) | the driver's owner and the place `RaceExportSource` is handed in; `sharedFolderPath`, rival ids, `endRealTime`; shared vocabulary R3E-01 extends | R3E-01, R3E-02, R3E-04, R3E-06, R3E-16, R3E-17, R3E-18, R3E-21 |
| R1-09 (`RaceSlots.ResolveClashes`) | the clash re-check after a sync (E.1.7) | R3E-18 |
| R1-10 (difficulty digest, `RaceFolderCheck`, `IRaceSharedFolder` and its adapter) | the disclosure digest; the shared-folder port R3E-16 folds into `IRaceTransport`; `RaceFolderCheck`'s path-length check, whose conservative constant R3E-01 replaces with `RacePaths.LongestExpected*` | R3E-01, R3E-15, R3E-16 |
| R1-11 (the join writes `firstImportDone = false`) | the just-joined exception | R3E-18 (soft) |
| R1-12 (both race format rows, golden `v2`) | `v3` follows it | R3E-04 |
| T-01 (`SaveWriterDriver`) | the `v3` generator runs through it | R3E-04 |
| T-02 (`FakeSyncedStore`, `FakeClock`) | the fake transport and the clock of the sync tests | R3E-16 |
| T-16 (`RaceFolderBuilder`) | the shared folders of the import tests | R3E-18 |
| T-18 (two-machine simulator) | the loopback tests, including their self-heal case | R3E-19, R3E-20 |
| R3W-05 (the verdict consumer) | R3E-18 hands verdicts to it; until it lands the driver only logs them | none (soft) |
| R3W-03 (the tree loader) | agrees the tree key list in R3E-02's pull request (OV-35) | none (soft) |
| Spikes S8a, S8b (stall of a first open on sync clients) and the owner's yes on the worker | the copy worker | R3E-23 |
| Spike S9 (which stock debug actions a mod can observe) | the debug-action list of A3 and the debug-completion fact | R3E-09 |

No other spike gates this plan. No task needs a flight before R3E-08.

## 3. What the code looks like today

### 3.1 Sidecar files and their hashes

- A recording's hashed sidecars are `.prec`, `_vessel.craft`, `_ghost.craft`
  (`Source/Parsek/SidecarVersions.cs:14-20`, suffixes `:90-99`). The hash is the first 64 bits
  of SHA-1 as 16 lower-hex characters, "identity, not security"
  (`SidecarVersions.cs:22-47`); `IsWellFormed` at `:55-68`.
- A head lives at `Parsek/Recordings/<id><suffix>`; a retained version at
  `Parsek/Recordings/_versions/<id>.<hash><suffix>` (`SidecarVersions.cs:71-81`,
  `VersionFileName` `:112-115`, `VersionPathForHead` `:128-132`). The design's payload naming
  is this same shape.
- Memory keeps per kind the hash of the bytes last written or read: `PrecHash`,
  `VesselSnapshotHash`, `GhostSnapshotHash` (`Recording.cs:89-97`). `FilesDirty` is at
  `Recording.cs:70`; the clone copies all three hashes (`Recording.cs:1130-1135`).
- The sidecar epoch is written INTO the `.prec` bytes (`TrajectorySidecarBinary.cs:225`,
  `:251`). `RecordingStore.FlushDirtyFiles` writes dirty recordings after a commit without
  advancing an existing epoch and sets `SidecarEpochAdvancePending`
  (`RecordingStore.OptimizerSupport.cs:43-71`, field doc `Recording.cs:80-87`); the next
  OnSave rewrites with epoch + 1, so the same trajectory gets new bytes and a new hash.
- Ghost snapshot mode: `GhostSnapshotMode { Unspecified, Separate = 1, AliasVessel = 2 }`
  (`Recording.cs:7`). In alias mode no ghost file is kept and the ghost is a copy of the
  vessel snapshot (`RecordingSidecarStore.cs:638-643`); alias is chosen when the ghost is null
  or equal to the vessel snapshot (`RecordingSidecarStore.cs:1927-1945`). The ghost visual
  snapshot is a full vessel `ConfigNode` (`Recording.cs:433`); stock part nodes carry their
  kerbals as `crew` values (the key `CrewReservationManager.cs:53` reads).
- Save path: `SaveRecordingFiles` (`RecordingSidecarStore.cs:24`) stages prec, vessel and
  ghost writes as retained versions and deletes an alias ghost head
  (`RecordingSidecarStore.cs:1490-1552`). Snapshot files are PSN0 Deflate
  (`SnapshotSidecarCodec.cs:31-46`; `TryLoad` `:123`; `Write(path, node)` `:183`; there is no
  bytes-returning encoder).
- Relative paths: `RecordingPaths.BuildTrajectoryRelativePath` (`RecordingPaths.cs:14`),
  `BuildGhostSnapshotRelativePath` (`:38`), `ResolveSaveScopedPath` (`:67`).
- Id check: `RecordingPaths.ValidateRecordingId` (`RecordingPaths.cs:284-312`) refuses null,
  separators and `..`, then asks the OS for invalid characters at `:302` (P7 replaces that).
- Tree metadata codec: `RecordingTreeRecordCodec.SaveRecordingInto` (`:11`; hashes written at
  `:118`, `:122`) and `LoadRecordingFrom` (`:543`; hashes parsed at `:796-798`).
  `RecordingTree` has `Id`, `TreeName`, `RootRecordingId`, `BranchPoints`
  (`RecordingTree.cs:13-29`) and `Save(ConfigNode)` (`:148`).

### 3.2 Safe writes

- `FileIOUtils.SafeWriteTempSuffix = ".tmp"` (`FileIOUtils.cs:61`); `SafeWriteConfigNode`
  (`:256-306`), `SafeWriteBytes` (`:317-344`), `SafeMove` (`:483`), `ReplaceDestination` with
  `File.Replace` and a `.bak.<guid>` move-aside fallback (`:548-627`), `CopyDirectory`
  (`:654`). Every path logs through `ParsekLog`, so it is main-thread code. There is no retry
  and no backoff.
- `ParsekLog.ScreenMessage(string, float)` is at `ParsekLog.cs:605`; rate-limited Warn and
  Info at `:419` and `:470`.

### 3.3 The effective sets and the Re-Fly session

- `EffectiveState.ComputeERS` (`EffectiveState.cs:2117-2190`) keeps committed recordings that
  are not `NotCommitted`, visible (not superseded), not rewind-retired, and not in the active
  Re-Fly marker's suppressed subtree. A `CommittedProvisional` recording stays in ERS. The
  marker is read from `ParsekScenario.Instance.ActiveReFlySessionMarker` (`:2123`). The result
  is cached and the same list instance is returned until a version or the marker changes
  (`:2129-2135`).
- `EffectiveState.ComputeELS` (`EffectiveState.cs:2202-2263`) drops only tombstoned
  `ActionId`s (`:2240-2250`).
- `EffectiveState.ResolveOwningTree(rec, treeContext)` (`EffectiveState.cs:1280-1315`) scans
  `RecordingStore.CommittedTrees`, so `RaceExport` reaches trees without naming the store.
- `SessionSuppressionState.ActiveMarker` (`SessionSuppressionState.cs:50`) is in the
  Controllers module (`scripts/arch/modules.toml:454-457`), the entry layer, so `RaceExport`
  (features) must not call it.
- `MissionStore.DisplayNameForTree(RecordingTree)` (`MissionStore.cs:825`) gives the mission
  name.

### 3.4 Milestone rows (A1) and the 7.8 VERIFY items

- `CommittedMilestoneReach.IsOnceEverMilestoneRow` (`GameActions/CommittedMilestoneReach.cs:
  101-107`): a `MilestoneAchievement` row that is not a repeatable world record.
  `CollectRowsAtOrBefore` (`:177-199`) is the existing clock-bounded collector.
- `MilestonesModule.ProcessAction` (`GameActions/MilestonesModule.cs:121-172`) credits the
  first hit; a later hit of the same id is marked `Effective = false`,
  `NotCountedReason = MilestoneAlreadyAchieved` (`:157-158`). The row stays in the ledger.
- `GameStateEventConverter.ConvertMilestoneAchieved` (`GameActions/GameStateEventConverter.cs:
  1184-1216`) sets `UT`, `RecordingId` and `MilestoneId`. Six other milestone row kinds carry
  no flight id (inventory report, A1).
- Supersede tombstones only eligible rows of the superseded subtree
  (`SupersedeCommit.RetireSet.cs:190-249`); `MilestoneAchievement` is eligible
  (`GameActions/TombstoneEligibility.cs:87`); a row with no `RecordingId` never is (`:59`).
- **VERIFY 7.8 (1), answered: yes.** After a rewind and an earlier re-earn, the later row
  stays in the effective ledger: `MilestonesModule` only flips it to not-effective
  (`MilestonesModule.cs:157-158`) and `ComputeELS` filters nothing but tombstones
  (`EffectiveState.cs:2240-2250`). So both dates are on record, as 6.1 expects.
- **VERIFY 7.8 (2), answered.** A Re-Fly merge tombstones the eligible milestone rows tagged
  to the superseded subtree and nothing else. What it leaves: the attempt's own row (new
  date), any row of the same milestone tagged to a flight in another tree (it becomes the
  earliest if it is), and every untagged row (never tombstoned, `TombstoneEligibility.cs:59`),
  which export resolves by covering range or lists as unverified (edge case 12).

### 3.5 Vessel ends outside a recording (A2)

- `ParsekScenario.SubscribeVesselLifecycleEvents` subscribes `onVesselTerminated`
  (`ParsekScenario.VesselLifecycle.cs:19-44`, `:30`). `OnVesselTerminated` (`:336-353`)
  updates pending-tree recordings only; committed recordings are never touched by terminal
  events (comment `:454-457`). The comment at `:346-347` says `onVesselTerminated` also fires
  after `onVesselRecovered` for the same vessel.
- The recovery precedent: `LedgerOrchestrator.OnRealVesselRecovered(ut, pid, guid, name)`
  (`GameActions/LedgerOrchestrator.Recovery.cs:331-409`) skips programmatic recoveries, pid 0
  and an active Re-Fly; owners come from `CrewRecoveryReservationClose.SelectOwnerRecordings`
  (`CrewRecoveryReservationClose.cs:52`); rows are deduplicated per owner recording
  (`LedgerOrchestrator.CommitRows.cs:461-464`); the row builder is at `Recovery.cs:415`.
- `GameActionType.VesselRecovered = 35` (`GameActions/GameAction.cs:184`, doc `:160-183`: no
  pool moves, an older build reads the unknown type with a Warn and loses it); the highest
  member is `KerbalDismissal = 41` (`:263`). The type is named in eleven switch sites,
  including `SupersedeCommit.cs:2218` (a NEW type that is missing there strict-blocks every
  supersede, comment `:2215-2217`), `TombstoneEligibility.cs:116`,
  `LedgerEventIdentityRegistry.cs:532`, `KscActionExpectationClassifier.cs:334`,
  `GameActionDisplay.cs:152`, `LedgerLoadMigration.cs:99`, `TimelineBuilder.cs:1219`
  (spawn evidence) and the codec (`GameAction.cs:1375`, `:1559`).
- `RecoveredRecordingEvidence` reads recovery rows for spawn decisions
  (`GameActions/RecoveredRecordingEvidence.cs:55-134`, `HasRecoveryRow` `:91`).
- Destruction events: `FlightRecorder.OnVesselWillDestroy` handles the active recorded vessel
  only (`FlightRecorder.cs:1899-1928`). Stock fires `onVesselDestroy` synchronously from
  `Vessel.Die()` (`GhostMapPresence.cs:203`) and also from `Vessel.OnDestroy` at quit
  (`Patches/GhostTrackingStationPatch.cs:225`), so a destroy event alone is not proof of a
  real destruction.
- Inputs of the stand query: `RecordingTree.IsSpawnableLeaf` (`RecordingTree.cs:971`),
  `VesselIdentity.Resolve` (`VesselIdentity.cs:467`, Missions module),
  `GhostChainWalker.ComputeAllGhostChains` (`GhostChainWalker.cs:34`, Ghost module).

### 3.6 Tool use (A3)

- Hack gravity: `GravityHackDetector` (`GravityHackDetector.cs:22-97`) and
  `FlightRecorder.NoteGravityHack(trigger, factor)` (`GravityHackDetector.cs:110`), called at
  record start (`FlightRecorder.StartRecording.cs:216`) and on toggle
  (`Patches/HackGravityPatch.cs:28`). It writes a log line only.
- Cheat teleport: `CheatTeleportPatch` (Postfix on `FlightGlobals.PostOrbitSet`,
  `Patches/CheatTeleportPatch.cs:17-41`) -> `ParsekFlight.HandleCheatTeleport`
  (`ParsekFlight.RecorderSession.cs:555`) -> `FlightRecorder.OnCheatTeleport(v, oldBody,
  cause)` (`FlightRecorder.CheatTeleport.cs:171`). `TeleportCause { CheatMenu, TimeJump }`
  (`:44-48`); Parsek's own time jump also calls it (`TimeJumpManager.Placement.cs:1056`).
- Cheat currency: `GameStateRecorder.IsCheatCurrencyReason` (`GameStateRecorder.cs:1333`) and
  `LogUnledgeredCheatCurrency` (`:1343`), called for funds, science and reputation
  (`:1026`, `:1201`, `:1276`). A stock `Set` call fires no change query and is not seen.
- No code reads stock `CheatOptions` switches for capture today (the only read is a test
  command, `TestCommands/ParsekTestCommandAddon.EvaGroundScience.cs:741`).
- Test tooling entry points: `InGameTestRunner.RunAll` (`InGameTests/InGameTestRunner.cs:
  383`), `RunBatchSelector` (`:494`).
- Recordings change shape in `RecordingOptimizer.MergeInto` (`RecordingOptimizer.cs:934`),
  `SplitAtUT` (`:1711`), the clone (`Recording.cs:1130`) and `SessionMerger` (`:13`).

### 3.7 Save format and golden folders

- `SaveFormatInfo(name, key, baseline, current)` (`SaveFormat.cs:31-51`), the registry rows
  and `All` (`:93-126`), `Classify` (`:139-151`). `SaveFormat.cs` is in the Recording
  module, which may not name `RaceCore` (15.4), so `raceFormat` needs its own literal there.
- `SaveFormatGoldenGateTests.FindCoverageProblems` (`Source/Parsek.Tests/
  SaveFormatGoldenGateTests.cs:42-71`) reds until some `vN/formats.txt` carries every version
  of every `All` row; every `vN` needs `formats.txt` and `persistent.sfs` (`:133-145`); every
  file is pinned in `checksums.txt` (`:149-157`). Today only `v1` exists; its manifest lists
  ten rows (`Fixtures/FormatGolden/v1/formats.txt`). Folders are frozen
  (`Fixtures/FormatGolden/README.md`).

### 3.8 Gates the plan touches

- Background threads: `scripts/background-threads-audit-allowlist.txt` is empty by design; a
  file added needs its own culture handling and a rationale line. The scan is a raw substring
  match, comments included (`scripts/grep-audit-background-threads.ps1`, header), mirrored in
  `GrepAuditTests.GrepAudit_SourceParsekStartsNoBackgroundThreads`
  (`Source/Parsek.Tests/GrepAuditTests.cs:59`).
- ERS/ELS routing: `scripts/grep-audit-ers-els.ps1` matches `.CommittedRecordings` and
  `Ledger.Actions`; `RaceExport` reads only through `EffectiveState` and needs no entry.
- Layers (`modules.toml:505-519`): floor = LogIO, Config, Core, Trajectory; model =
  Recording, GameActions, Crew; features = Ghost, Rewind, Missions, ...; entry = Controllers,
  Patches. Headless modules (`:531-536`). Forbidden edges today: two (`:562-566`).
  `ConfigNode` is an inert type for the headless gate (`scripts/arch/facts.toml:18-19`).
- Placement: the Config rule is `^ParsekConfig|^ParsekSettings|^SettingWhitelist`
  (`modules.toml:345-347`).
- Lifetimes: categories and the Cold-load roots (`scripts/arch/lifetimes.toml:20-40`).

## 4. Tasks

File names are proposals; the shapes are what the tasks rest on. `RaceSync` is a static
partial class split by stage (`RaceSync.cs`, `.Export.cs`, `.CopyOut.cs`, `.Import.cs`,
`.Witness.cs`, `.Heal.cs`, `.Prune.cs`) so parallel tasks do not edit one file.
`RaceExportSource` is split the same way (`.Milestones.cs`, `.Leftovers.cs`, `.Tools.cs`,
`.Disclosure.cs`). Every log line uses a tag from {`RaceSync`, `RaceExport`, `RaceCopy`,
`RaceWitness`, `FactCapture`}, entered in R1-06's tag list (OV-57: the poll and the import
log under `RaceSync`, never `RaceImport`), and follows the batch counting convention.

The uncalled-method ratchet (second-pass ruling A1; `uncalledInAssembly`,
`docs/dev/arch/README.md:509-512`) counts every production method the roots do not reach,
so a member lands in the pull request of its first production caller. A task below that
names a member with a later first caller specifies it ("lands with R3E-NN"); that later
task adds it to the earlier task's file. Fields and enum members are not methods.

### R3E-01 Exchange vocabulary and the own-mirror scan

- **Change:**
  - `RaceCore/RaceHash.cs`: `Name64(byte[])` (16 lower hex, first 64 bits of SHA-1, the
    same function as `SidecarContentHash.Compute`, reimplemented because `RaceCore` cannot
    name Recording), `Proof256(byte[])` (64 lower hex SHA-256), `IsName64`, `IsProof256`
    (the codec's checks). The streaming variants over a `Stream` land with R3E-16's
    transport, their first caller.
  - Specified here, landing with R3E-04's export (its first caller): R1-08's
    `RaceCore/RaceTimestamp.cs` gains `NextStamp(now, previous)` = `max(now, previous + 1 s)`
    (format and exact parse are R1-08's; OV-24).
  - `RaceCore/RaceFileNames.cs`: the strict grammar. Standing `^[0-9]{6,9}\.pstd$` written as
    `D6`; payload `<id>.<16 hex>.prec` and `<id>.<16 hex>_ghost.craft`; tree
    `trees/<treeId>.<16 hex>.cfg`. `TryParse(name, out RaceFileName)` returns a kind or an
    ignore reason (`conflict-copy` for names with ` (` or `conflicted`, `temp` for names the
    two transports and `FileIOUtils` leave behind, `unknown`). Ids pass the P7-01 floor
    check through R1-08's `RaceIds`.
  - R1-08's `RaceCore/RaceIds.cs` gains the 8-character short id with its collision check
    and collision lengthening (4 more characters on each collision with the taken set;
    design 6.3, OV-24); no R1 task calls it. This task's in-game cell is its first live
    caller (A5); R3E-18's rival cache directories are its first production caller.
  - `RaceCore/RacePaths.cs`: relative layout of `Parsek/Race/` own `standings/`, `payload/`,
    `index.cfg` (what the scan reads). Specified here, each landing with its first caller:
    the shared-folder layout (4.1) with R3E-16, the rival cache
    (`rivals/<short>/standings/`, `payload/`, `incoming/`, `rivals/index.cfg` mapping short
    names from `RaceIds` to ids) with R3E-18, and `witness.cfg` with R3E-19. This task adds
    `LongestExpectedSharedPath(sharedRoot)` / `LongestExpectedLocalPath(saveDir)` (from the
    maximum id, hash and suffix lengths of both layouts) and replaces the conservative
    constant R1-10 created in `RaceCore/RaceFolderCheck.cs` with them, updating R1-10's
    call site there, which is their production caller.
  - `RaceCore/Standing.cs`: the immutable model of 6.1 with the additions of section 9:
    `seq`, `playerId`, `exportedRealTime`, `prevSeq`, `prevHash`, `note`, `frontierUT`,
    `raceFormat`, `recordingFormat`, `recordingSchemaGeneration`, `Recordings`
    (`REC { id treeId startUT endUT prec precProof ghost ghostProof }`, later `TOOL`
    children), `Trees` (`TREE { id hash proof }`), `Milestones`, `Leftovers`, `Names`,
    `Disclosure`, `Seen`, `PartManifest`, `signature` (empty). Members are readonly fields,
    so no getter waits for a caller (A1); a block task adds its block's fields.
  - `RaceCore/StandingCodec.cs`: `TryRead(byte[], out Standing, out string reason)` only.
    Root node `STANDING`. Refuses whole: unsafe id, unparseable stamp, missing `seq` or
    `playerId`, duplicate `REC` id, malformed hash or proof, truncated text. Unknown keys and
    blocks are ignored (additive rule). The stored `raceFormat` is classified by R1-03's
    `RaceFormat.Classify` (the five classes of `SaveFormat.Classify`) against
    `RaceFormat.Current` (OV-16); this plan adds no format constant.
  - R1-08's `IRaceFolder` (`RaceCore/RacePorts.cs`: `TryRead` from R1-08, `SafeWrite` from
    R1-10; paths relative to the local race root) and its adapter `RaceWorld/RaceFolder.cs`
    (over `FileIOUtils`, rooted where R1-08 computes the local race directory, refusing any
    path that leaves the root) gain here the members no R1 task calls (OV-22, OV-23):
    `List` (the scan calls it), `Exists`, `Move` and `Delete`, each on the port, the adapter
    and R1-08's in-memory folder. Their first production callers come later in this plan:
    R3E-04's skip of a payload file already present (`Exists`), R3E-18's move out of
    `incoming/` (`Move`), R3E-21's prune (`Delete`).
  - `InGameTests/RaceExchangeTests.cs` (category `RaceExchange`, SPACECENTER; R3E-24 adds
    the exchange check to it): one cell that, in a race save, writes two files under a
    scratch directory of the local race root through the real `RaceFolder`, lists them,
    checks `Exists`, moves one, deletes both, and builds short ids for a set of ids made to
    collide; it skips naming the context in a single-player save. It is the live root (A5)
    that keeps `Exists`, `Move`, `Delete` and the short id whole in this task.
    `RaceCore/OwnMirror.cs`: `Scan(IRaceFolder)` -> seqs, latest, chain status (each
    standing's `prevSeq` / `prevHash` against the file it names).
  - `RaceWorld/RaceSyncDriver.cs`: created by R1-08's `RaceSession`; `OnSessionStart()` scans
    the own mirror and logs `[RaceSync] own mirror: standings=N latest=S chain=ok` (or
    `chain=broken at=K`). This is the production caller the uncalled-method ratchet needs:
    every member this task ships is reached from it (hash checks, file names, own paths,
    the codec's read, the scan), from the in-game cell (the folder members, the short id)
    or from R1-10's `RaceFolderCheck` (the longest-path figures).
- **Proof:** xUnit `RaceHashTests` (Name64 equals `SidecarContentHash.Compute` over random
  buffers), `RaceFileNamesTests` (every kind, conflict copies, `.tmp`, `.bak.<guid>`,
  uppercase hex refused), `RacePathsTests` (own layout), `StandingCodecReadTests` (each
  refusal reason, unknown blocks ignored, a newer `raceFormat` classified Newer),
  `OwnMirrorScanTests` over R1-08's in-memory `IRaceFolder`; `RaceIdsTests` gains the short
  id and its clash cells; `RaceFolderTests` gains list, exists, move and delete (temp dir);
  `RacePathsTests` the longest paths, and R1-10's `RaceFolderCheckTests` budget cells pass
  with the computed figures. The tests of the specified members move with them (the stamp
  rule with a clock going backwards and `de-DE` culture to R3E-04, streaming hashes to
  R3E-16). Log: the scan line.
- **Registers:** no new static; `RaceCore` headless pins unchanged (no leak: `ConfigNode` is
  inert, hashing is BCL); `scripts/arch/log_tags.json` refresh (report-only); the
  `RaceExchange` category's first cell (if T-29's RACE-9 spec is committed by then, its
  `total=` is re-pinned in the same pull request; `python harness/tools/lane_catalog.py`
  regenerates `docs/dev/flight-profiles.md` for the new category); a
  `grep-audit-parsek-file-deletes` allowlist entry for `RaceFolder.cs`'s delete unless it
  goes through a `FileIOUtils` helper.
- **Size:** M. **Depends on:** P7-01, P12-01, P12-02, R1-01, R1-03, R1-06, R1-07,
  R1-08, R1-10. **PC:** no (the cell's first run rides T-29's RACE-9 or the owner's PC).
- **Risk:** low; pure code plus a scan of an empty directory.

### R3E-02 Public state from the own model

- **Change:**
  - `RaceCore/PublicState.cs`: immutable `PublicState { Recordings, Trees, FrontierUT, Names,
    ... }` with `PublicRecording { Id, TreeId, StartUT, EndUT, VesselName, PrecName }` and
    `PublicTree { Id, Name, Bytes, Name64, Proof }`; `PublicStateResult` = state or
    `NotReady(reason)`.
  - `RaceCore/IPublicStateSource.cs`: `PublicStateResult Snapshot()`. `object
    ChangeToken()` is specified here and lands with R3E-17, its first caller (A1).
  - `RaceCore/PublicStateDigest.cs`: SHA-256 over a canonical text (records sorted by id,
    doubles `"R"` invariant), excluding `seq`, stamps, `prevSeq`, `prevHash`, `note`, `SEEN`.
  - `EffectiveState.ExportReads.cs` (new partial, Rewind module): `HasActiveReFlySession()`,
    reading the marker the way `ComputeERS` does (`EffectiveState.cs:2123`), so `RaceExport`
    needs no edge to Controllers.
  - `RaceExport/RaceExportSource.cs`: reads `EffectiveState.ComputeERS()`; `NotReady` when a
    Re-Fly session is open (7.5), when any ERS recording has `FilesDirty` or
    `SidecarEpochAdvancePending`, or a malformed `PrecHash` (each reason logged with the
    count); otherwise one `PublicRecording` per ERS recording with `PrecName = PrecHash`;
    trees through `EffectiveState.ResolveOwningTree(rec, null)`; names through
    `MissionStore.DisplayNameForTree` and `Recording.VesselName`; frontier = max `EndUT`.
    Its `ChangeToken()` (R3E-17) returns the ERS and ELS list instances as a pair.
  - `RaceExport/RaceTreeProjection.cs`: the public tree file in the production
    `RecordingTree` node shape (OV-35): a filtered copy of the tree holding only its ERS
    recordings and the branch points whose endpoints are all in ERS, written through the
    production writer (`RecordingTree.Save`, `RecordingTree.cs:148`, and
    `RecordingTreeRecordCodec.SaveRecordingInto`), then cut to one agreed key list so no
    private key leaves the save. The list keeps what `RecordingTree.Load` needs plus the
    terminal and endpoint keys R3W-03 reads (terminal state, terminal orbit, end position,
    endpoint body, isDebris; r3w section 9.2), and id, vesselName, startUT, endUT,
    parentAnchorRecordingId, chainId, chainIndex, sidecarEpoch (the rival loader's epoch
    check, `RecordingSidecarStore.cs:180-185`). R3E-02 and R3W-03 agree the one key list in
    R3E-02's pull request, so R3W-03 loads the file with the production reader.
  - Composition: R1-08's Controllers composer builds `RaceExportSource` and hands it to the
    driver as `IPublicStateSource` (`RaceWorld` never names `RaceExport`). The driver's
    `OnSessionStart` logs `[RaceExport] public state: recordings=N trees=M frontierUT=X
    digest=D` or `notReady reason=R`.
- **Proof:** xUnit `[Collection("Sequential")]` `RaceExportSourceTests` over `RecordingBuilder`
  trees: superseded and `NotCommitted` excluded, `CommittedProvisional` included, Re-Fly
  marker -> notReady, dirty -> notReady, epoch-pending -> notReady; `PublicStateDigestTests`
  (order and culture invariant, SEEN excluded); `RaceTreeProjectionTests` (hidden recordings
  and their branch points dropped; the written file loads through `RecordingTree.Load`; a
  key outside the agreed list never appears).
- **Registers:** R1-06's 15.4 grep gates (no `RecordingStore` in race files; test setup
  lives in the test project); layer pins (all new edges point down or sideways); log tags.
- **Size:** M. **Depends on:** R3E-01; R3W-03 agrees the key list (soft). **PC:** no.
- **Risk:** medium; the not-ready rules decide how often an export can happen.

### R3E-03 The public ghost snapshot

- **Change:**
  - `RaceExport/PublicGhostProjection.cs`: source = the ghost visual snapshot in memory, or
    the vessel snapshot in alias mode; when not hydrated, loads the file whose hash equals
    the recorded `GhostSnapshotHash` / `VesselSnapshotHash` with
    `SnapshotSidecarCodec.TryLoad` (`SnapshotSidecarCodec.cs:123`); hash mismatch -> notReady.
    Copies the node and strips every `crew` value of every part (4.3: the roster is never
    shared); keeps parts, modules and resources (the "parts only" of 6.3 is undefined, see
    section 9). Encodes with a new bytes-returning `SnapshotSidecarCodec.Encode(ConfigNode)`
    that `Write` (`:183`) then calls, so the file format stays one code path.
  - `PublicRecording.GhostName` = `Name64` of the derived bytes. The source instance caches
    `(recordingId, kind, sourceHash) -> (name, bytes)` so the projection runs once per source
    version; bounded by the ERS size.
- **Proof:** `PublicGhostProjectionTests` (`VesselSnapshotBuilder` snapshot with crew -> no
  `crew` value; alias and separate give equal bytes for equal snapshots; two runs give equal
  bytes; a cache hit logs no re-encode); `SnapshotSidecarCodecTests` gains `Encode` equals
  the bytes `Write` puts on disk.
- **Registers:** none beyond log tags.
- **Size:** M. **Depends on:** R3E-02. **PC:** no.
- **Risk:** medium; a missed private key in a snapshot would leak a name.

### R3E-04 Write a standing to the own mirror; golden v3

- **Change:**
  - `StandingCodec.Write(Standing) -> byte[]`: UTF-8, no BOM, LF, fixed key order, doubles
    `"R"` invariant; the `raceFormat` written is R1-03's `RaceFormat.Current`.
  - `RaceCore/IRacePayload.cs` (own half): `TryReadOwn(recordingId, kind, name, out byte[],
    out string reason)`. `RaceExport/OwnPayloadReader.cs` reads the prec head
    (`RecordingPaths.BuildTrajectoryRelativePath`) or the retained version under `_versions`
    (`SidecarVersions.VersionPathForHead`), checks `Name64(bytes) == name` and otherwise
    refuses; ghost and tree bytes come from the source's caches. The rival read half of
    `IRacePayload` is R3W's (OV-37).
  - `RaceCore/SyncPorts.cs`: the port bundle `RaceSync.Run` takes (OV-18, E.1.10), as
    fields: R1-08's `IRaceFolder` (local), `IRaceClock` (`UtcNow`, the stamp's clock; T-02's
    `FakeClock` implements it), `IPublicStateSource`, and the own `IRacePayload`. The
    `Transport` slot and the `IRaceTransport` interface are specified here and land with
    R3E-16, their first caller and implementation (A1 over E.1.10: the early declaration
    served R3E-05's fake, which is dropped, and T-18 already depends on R3E-16). T-18 builds
    a `SyncPorts` from its simulated machine.
  - `RaceCore/RivalStandingVerdict.cs`: the value type of the import output (OV-37; A4:
    declared by this task), so R3W-05 and R3W-09 can build on it before the import lands:
    `{ playerId, cacheDir, appliedSeq, standing, manifest (the rival's `PlayerManifest`
    from the cached `player.cfg`, null when missing), status (Applied, KeptPrevious,
    NewerFormat, Refused), marks, disagreements }`, a struct of public fields with no constructor or
    method, so it costs the ratchet nothing until R3E-18 builds the first value.
    `SyncResult.Verdicts` (a field, empty here) carries one per rival; R3E-18 fills it.
  - `RaceSync.cs` and `RaceSync.Export.cs`: `Run(SyncPorts, SyncInputs) -> SyncResult`.
    Export step: snapshot; notReady -> status only; digest equal to `lastStandingDigest` ->
    nothing; otherwise seq = max(own mirror) + 1 (R3E-16 adds the shared listing), stamp by
    `RaceTimestamp.NextStamp` (lands here, R3E-01), `prevSeq` and `prevHash` = the latest own
    standing; copy each newly named payload file into `own/payload/` (skipped when R3E-01's
    `IRaceFolder.Exists` and an equal proof say it is present; its first production caller),
    hashing the COPY with `Proof256`; write the standing last; return the new digest.
    `SyncResult` carries the status, the verdicts and `Messages`, the one list of race
    screen messages. `RaceSync.Run` hosts the board call (R4-04 adds it after the import
    step) and appends every message to the list, R4-07's holder changes included. Here the
    list is a field only: the driver's posting of it lands with R3E-22 (OV-38).
  - `RaceCore/OwnMirrorIndex.cs` (`own/index.cfg`): per payload file its proof, `copiedOut`
    (R3E-16) and `unkeptSince` (R3E-21).
  - Driver: runs an export at session start and on Space Center entry (more ticks in
    R3E-17) and stores the digest as `lastStandingDigest` through R1-08's `RaceLocal` codec
    (this task is that key's writer, OV-27).
  - No `SaveFormat.cs` change and no bump (OV-15): R1-12 registers the `race-exchange` row at
    1 together with `race-link`. `RaceFormatPinTests` is R1-03's class; this task adds its
    cells (the standing codec writes and reads `RaceFormat.Current`; a standing at
    `Current + 1` classifies Newer).
  - Golden `Fixtures/FormatGolden/v3/` (after R1-12's `v2`), at `race-exchange = 1`, written
    once by `FormatGoldenV3GeneratorTests.WriteV3` (skipped unless
    `PARSEK_WRITE_FORMAT_GOLDEN=v3`) through T-01's `SaveWriterDriver`: `persistent.sfs` (a
    minimal race save with R1-03's race scenario node and two committed trees),
    `formats.txt` (every row at its current, both race rows at 1), `Parsek/Race/own/` with
    two chained standings, their payload and the own index, and the same files under
    `shared/players/<id>/`; `checksums.txt` additions; `FormatGoldenV3Tests` reads every
    standing through `StandingCodec`, checks the chain and every payload proof. README gains
    a `v3` section. A new folder without a bump is allowed: `SaveFormatGoldenGateTests` asks
    only that every version is carried by some folder and that frozen files keep their
    checksums. The golden may be a second pull request of this task (E.6).
- **Proof:** `StandingCodecRoundTripTests`; `RaceTimestampTests` gains the stamp rule (a
  clock going backwards, `de-DE` culture);
  `RaceSyncExportTests` with R1-08's in-memory folder and a fake source (no change -> no
  write; change -> payload writes precede the standing in the recorded write order; seq and
  stamp rules; chain); `OwnPayloadReaderTests`
  (head, retained version, hash mismatch refused); the `RaceFormatPinTests` cells; the three
  golden gates. Logs: `[RaceSync] exported seq=S payload=N bytes=B digest=D` and the skip
  reasons.
- **Registers:** golden folder `v3`, `checksums.txt`, golden README (the format row and the
  save-format policy line naming the exchange format are R1-12's).
- **Size:** M (the golden folder may be its own pull request). **Depends on:** R3E-03,
  R1-03, R1-08, R1-12, T-01. **PC:** no.
- **Risk:** medium; golden folders are frozen, so the first one must be right.

### R3E-05 Synthetic shared-folder builder (test only)

Dropped: replaced by T-16, R1-08, T-02 and R3E-16's tests (OV-17).

### R3E-06 The capture switch for race-only facts

- **Change:** `Source/Parsek/ParsekConfigFactCapture.cs` (Config module by the existing
  prefix rule): `internal static class FactCapture` with `Armed` (private setter) and
  `ArmForLoadedSave(bool raceSave, string reason)`, which logs `[FactCapture]` only when
  the state changes, comparing against `Armed`, so the getter is reached here (A1). The
  neutral name keeps the base modules free of race names (15.4). R1-08's race session
  build, which runs after the Cold reset of each load, calls `ArmForLoadedSave` (this task
  adds the call). `TestToolingUsed` and `NoteTestTooling` are specified here and land with
  R3E-08, their first caller; `NoteDebugCompletion` lands with R3E-09.
- **Proof:** `FactCaptureTests` (default false; arm and disarm log; no log without a change;
  the save-scoped reset clears it); a source wiring test that the arm call follows
  `ResetSaveScopedSessionState` on every load path R1-08 uses.
- **Registers:** `scripts/arch/lifetimes.toml` (`save`), `scripts/session-reset-allowlist.txt`,
  `SaveScopedSessionResetTests.InventoryTypes`; the statics-written-by-another-type ratchet
  is satisfied because only `FactCapture` writes its fields.
- **Size:** S. **Depends on:** R1-04, R1-08. **PC:** no.
- **Risk:** low; an arm before the reset would silently disarm, which the wiring test catches.

### R3E-07 The tool-use fact on a recording (A3 data)

- **Change:**
  - `Recording.cs`: `List<ToolUse> ToolUses` (null by default);
    `ToolUse { ToolKind Kind; string Detail; double FirstUT }`;
    `ToolKind { GravityHack = 1, CheatTeleport = 2, CheatCurrency = 3, CheatOption = 4,
    DebugAction = 5, TestTooling = 6 }` with explicit numbers.
  - `RecordingTreeRecordCodec.SaveRecordingInto` writes `TOOL_USE { kind detail ut }`
    children only when the list is non-empty; `LoadRecordingFrom` reads them and skips an
    unknown kind with a Verbose line.
  - Clone copies; `RecordingOptimizer.MergeInto` unions keeping the minimum UT per
    (kind, detail); `SplitAtUT` gives each half the uses inside its range (the first half
    also keeps uses before its start); `SessionMerger` carries them if it builds recordings
    field by field (VERIFY when implementing).
  - `RecordingBuilder.WithToolUse(...)` in the generators.
- **Proof:** codec round trip; a recording with no uses serializes byte-identical to today
  (the single-player promise); merge, split and clone tests.
- **Registers:** the post-change checklist (OnSave and OnLoad carry it through the tree
  codec, generators, a synthetic recording); no format bump (an optional child node, the
  additive rule); recording schema generation unchanged.
- **Size:** S. **Depends on:** none. **PC:** no. **Risk:** low.

### R3E-08 Capture the tools Parsek already detects (A3)

- **Change:**
  - `FlightRecorder.ToolUse.cs` (Recording module): `NoteToolUse(ToolKind, string detail,
    double ut)`: no-op unless `FactCapture.Armed` and recording; first use per
    (kind, detail) on the active recording (the board's rule reads the whole tree, so the
    active recording is enough); one Info line per first use. Pure core
    `ToolUseDecision.ShouldRecord(armed, isRecording, kind, cause)`.
  - Sites: `NoteGravityHack` (`GravityHackDetector.cs:110`); `OnCheatTeleport` with
    `TeleportCause.CheatMenu` only (`FlightRecorder.CheatTeleport.cs:171`, never `TimeJump`);
    `LogUnledgeredCheatCurrency` (`GameStateRecorder.cs:1343`) through the same recorder
    accessor `HackGravityPatch.cs:28` uses, marking the active recording when one is
    recording; test tooling at `InGameTestRunner.RunAll` / `RunBatchSelector`, the command
    seam's arming and the autorun hook, each calling `FactCapture.NoteTestTooling` and,
    when recording, `NoteToolUse(TestTooling)`. This task owns the test-tooling mark
    (OV-33, second-pass A3): the seam marks the save once, at its arming, which covers every
    verb; no seam verb (T-11's `RaceSetup` included) marks on its own; the in-game test
    runner and the autorun hook mark too.
  - `FactCapture` gains `TestToolingUsed` (private setter; the member the standing's key
    `testTooling` maps to, OV-28) and `NoteTestTooling(string what)`, which sets it while
    armed and logs `[FactCapture]` only for the first mark, reading `TestToolingUsed` (R3E-06
    specified both; this is their first caller, A1). The save-scoped reset clears it.
- **Proof:** `ToolUseDecisionTests`, log assertions per site helper; in-game test category
  `RaceFacts` (FLIGHT only, OV-09): toggle hack gravity during a recording in a race-armed
  save and read `ToolUses`; the same in a single-player save reads empty.
- **Registers:** a new in-game category (regenerate the lane catalog,
  `python harness/tools/lane_catalog.py`), hosted by T-29's FLIGHT host lane RACE-10
  (OV-09); once T-29's spec is committed, each later test in it re-pins that spec's
  `total=` in the same pull request (`CommittedBatchTallySourceSyncTests`); the
  `TestToolingUsed` field entry in `scripts/session-reset-allowlist.txt` and
  `lifetimes.toml`; log tags.
- **Size:** M. **Depends on:** R3E-06, R3E-07. **PC:** yes (the in-game test).
- **Risk:** medium; VERIFY with S9 that no non-cheat stock path calls `PostOrbitSet`.

### R3E-09 Stock cheat switches and debug actions (A3)

- **Change:** `FlightRecorder.CheatOptions.cs`: reads the stock `CheatOptions` switches of
  the 7.12 list (exact field names from a decompile) at record start and once per second of
  real time while recording; notes the first "on" of each as `CheatOption` with the field
  name as detail. The debug actions S9 finds observable become `DebugAction` sites; the
  ones it finds unobservable are named in the design's 7.12 out-of-reach list in the same
  change.
  - The save-level debug-completion fact (OV-34; owner decision D3, design 13.A: a debug
    tool means no score). For every milestone completion S9 shows a mod can observe
    (`MaxProgression`, the console, the legacy cheat window reaching
    `ProgressNode.CheatComplete`), in or out of a flight, `FactCapture.NoteDebugCompletion(
    milestoneId, ut, action)` records `{ milestoneId, ut }` while armed, with one Info line
    each naming the action. The list is persisted in R3E-15's race facts child (R1-03's
    `IRaceScenarioChild` hook). The standing carries it (A4): wire form `DISCLOSURE` rows
    `DEBUG_COMPLETION { id ut }` (the milestone id and its UT), written by
    `RaceExportSource.Disclosure.cs` and read by the codec into the codec member
    `Standing.DebugCompletions` (a list of `{ MilestoneId, UT }`, R4's input shape). R6-01
    drops the milestone rows that match one by id and UT from the board and gives the player
    the mark `DebugCompleted`. A completion S9 cannot observe is listed as out of reach in
    design 7.12. Every member here is reached from a capture site or the export (A1).
- **Proof:** a pure `CheatOptionsWatch.Diff(previous, current)` test; codec round trip of
  `DEBUG_COMPLETION` rows into `Standing.DebugCompletions`; the list survives a save and a
  load through the race facts child; a disarmed save records none; in-game test in `RaceFacts` that
  switches one option on during a recording, and one that completes a milestone through an
  observable debug action and reads the fact.
- **Registers:** design doc 7.12 wording (the out-of-reach list); log tags.
- **Size:** S. **Depends on:** R3E-08, R3E-15, S9. **PC:** yes. **Risk:** low.

### R3E-10 Tools in the standing

- **Change:** `RaceExportSource.Tools.cs` copies `ToolUses` into
  `PublicRecording.Tools`; the codec writes `REC { ... TOOL { kind detail ut } }`, the one
  wire form of tool data (OV-32: no `toolsFromUT` key); the digest includes them;
  `FactCapture.TestToolingUsed` goes to the disclosure block (R3E-15). Specified here,
  landing with R6-01 as an edit to the standing codec's file (its first caller, second-pass
  A1): the codec's derived view per recording for R4, `ToolsFromUT` = the least `ut` of its
  `TOOL` children (NaN when none) and `ToolIds` = their kinds, sorted (E.1.11). The `TOOL`
  read and write branches are live here (reached from the scan and the export).
- **Proof:** export test with a builder recording carrying two uses; codec round trip. The
  view's tests (two uses, none) land with R6-01.
- **Registers:** none. **Size:** S. **Depends on:** R3E-04, R3E-07. **PC:** no.
- **Risk:** low.

### R3E-11 Milestone reach rows (A1) and the MILESTONES block

- **Change:**
  - `CommittedMilestoneReach.CollectReachRows(IReadOnlyList<GameAction> els)`: every
    once-ever row, effective or not, as `MilestoneReachRow { MilestoneId, UT, RecordingId,
    LedgerIndex }`, ordered by id, then UT, then ledger order; one Verbose summary.
  - `RaceExportSource.Milestones.cs`: reads `EffectiveState.ComputeELS()`, maps to the
    neutral `RaceCore` row `{ id, ut, recordingId, treeId }`; a row tagged to a recording that
    is not in ERS is dropped and counted (section 9).
  - `RaceCore/MilestoneTable.cs`, owned in full by this task (OV-29; R4-03 builds no copy):
    `Build(rows, recordings, previousOwnStanding, newSeq)` over the four-field reach rows:
    one entry per (id, ut); two rows of one (id, ut) tagged to different recordings keep the
    lowest recording id (R4's rule, the tie-break here); an untagged row takes the ERS
    recording whose range covers its UT (earliest start, then id), else stays empty
    (unverified, edge case 12); `since` = the previous own standing's `since` for the same
    (id, ut), else `newSeq`. Every once-ever stock milestone id is exported, so a ladder
    retune needs no new export; the ladder filter is R4-03's, applied when the board is built
    (`Ladder.MemberOf`, R4-04).
  - Codec: `MILESTONES { M { id ut recordingId since } }`.
- **Proof:** `CommittedMilestoneReachCollectTests` (rewind plus earlier re-earn gives two rows
  and the later one is listed though not effective; repeatable records excluded; a
  tombstoned row is absent through ELS); `MilestoneTableTests` (since carried, a new date
  gets the new seq, covering resolution, unverified, the lowest-recording-id tie-break, a
  milestone on no ladder still exported); export test.
- **Registers:** none beyond log tags.
- **Size:** M. **Depends on:** R3E-04 (no R4 task). **PC:** no. **Risk:** low.

### R3E-12 The vessel-ended ledger row type (A2 data)

- **Change:** `GameActionType.VesselEnded = 42` with a doc block like `VesselRecovered`'s;
  fields: `RecordingId`, the vessel name, `EndCause { Destroyed = 1, Deleted = 2 }`.
  Serializer and deserializer next to `VesselRecovered`'s (`GameAction.cs:1375`, `:1559`).
  Every switch that names `VesselRecovered` handles it in the same group: no pool moves
  (`LedgerLoadMigration`, `KscActionExpectationClassifier`), `NoEffect` identity
  (`LedgerEventIdentityRegistry`), display text "Vessel lost: <name>" (`GameActionDisplay`),
  dedup key per owner recording (`LedgerOrchestrator.CommitRows`), tombstone eligible
  (`TombstoneEligibility`), the non-blocking list in `SupersedeCommit.cs:2218`. It is NOT
  spawn evidence (`TimelineBuilder.cs:1219`) and NOT read by `RecoveredRecordingEvidence`,
  so spawns, crew and every single-player decision stay as they are. This task adds an
  enum member, fields and cases inside methods that are already live; the row builder
  (beside `Recovery.cs:415`'s) lands with R3E-13, its first caller (A1).
- **Proof:** a test enumerating every `GameActionType` member through each switch helper
  (if the registry's own completeness test exists it reds until done); serialization round
  trip; a supersede over a recording with such a row is not strict-blocked; ELS keeps it;
  tombstone eligibility.
- **Registers:** `docs/dev/design-save-format-policy.md` needs no bump (additive enum member,
  the accepted one-way downgrade of `GameAction.cs:160-183`); post-change checklist
  (ledger generator can build the row); `harness/lib/ledgeraudit.py` `GAME_ACTION_TYPES`
  gains `42: "VesselEnded"` (`test_ledgeraudit.test_game_action_types_and_seeds` compares
  it with the C# enum).
- **Size:** M. **Depends on:** none. **PC:** no.
- **Risk:** medium; a missed switch falls into a default that may block or misclassify.

### R3E-13 Record a vessel's end outside a recording (A2 capture)

- **Change:**
  - `GameActions/LedgerOrchestrator.VesselEnded.cs`: `OnRealVesselEnded(ut, pid, guid, name,
    cause)` mirrors `OnRealVesselRecovered`: no-op unless `FactCapture.Armed`; skips
    programmatic events, pid 0, an active Re-Fly, `RewindContext.IsRewinding`, ghost map
    vessels; owners through `SelectOwnerRecordings`; skips an owner that has a recovery row
    at the same UT (`HasRecoveryRow`), because terminate follows recover; deduplicated.
  - Deleted: `ParsekScenario.OnVesselTerminated` (`ParsekScenario.VesselLifecycle.cs:336`)
    calls it with `Deleted`.
  - Destroyed: `Patches/VesselEndCapturePatch.cs`, a Prefix on stock `Vessel.Die()` (the
    path that fires `onVesselDestroy` synchronously, `GhostMapPresence.cs:203`), because the
    destroy event also fires on scene unload and quit
    (`Patches/GhostTrackingStationPatch.cs:225`). It skips a vessel the active or a
    background recorder records (the recorder's own end handling, `FlightRecorder.cs:1899`).
    VERIFY by decompile that `Die` is the path for a loaded crash and for an unloaded
    vessel removed by the game, and that `onVesselTerminated` fires only for the Tracking
    Station's terminate.
- **Proof:** pure `VesselEndCaptureDecision` tests and log assertions; in-game tests, one
  scene per category (OV-09): `RaceFactsTrackingStation` (TRACKSTATION, hosted by T-29's
  RACE-11): terminate a spawned committed vessel -> one row; `RaceFacts` (FLIGHT, RACE-10):
  leave and re-enter flight -> zero rows; crash an unrecorded spawned vessel -> one row;
  a single-player save -> zero rows in all three.
- **Registers:** the lane catalog for the new `RaceFactsTrackingStation` category
  (`python harness/tools/lane_catalog.py`); `StartupNotices.cs` patch list; `ParsekHarmony`
  patch registration;
  `docs/dev/todo-and-known-bugs.md` entry for any destruction path the VERIFY finds
  unobservable.
- **Size:** M. **Depends on:** R3E-06, R3E-12. **PC:** yes.
- **Risk:** high; a false row on scene unload would end a rival's leftover early.

### R3E-14 The stand query (A2) and the LEFTOVERS block

- **Change:**
  - `EffectiveState.StandWindows.cs` (Rewind module): pure `StandWindowQuery.Compute(ers,
    trees, elsRows, sameVesselNextLeg)` returns, per spawnable non-debris leaf
    (`RecordingTree.IsSpawnableLeaf`), `{ recordingId, fromUT = EndUT, untilUT? }`, where
    `untilUT` = the earliest of: the start of the next ERS leg of the same vessel
    (`VesselIdentity.Resolve`), the first recovery row of its owner recording, the first
    `VesselEnded` row, and a later committed claim of the vessel
    (`GhostChainWalker.ComputeAllGhostChains`; VERIFY that this is the claim A2 means). A
    live wrapper gathers the inputs.
  - `RaceExportSource.Leftovers.cs` -> `PublicState.Leftovers`; codec
    `LEFTOVERS { L { recordingId fromUT untilUT vtype } }`, `untilUT` empty while standing.
    `vtype` (additive, OV-36) is the vessel type of the leftover's own vessel snapshot (the
    value `ResolveVesselType` reads, `GhostMapPresence.ProtoVesselLifecycle.cs:2090`), so
    R3W can draw the right map icon at scene load (r3w section 9.3); empty when unknown.
  - `RaceExportSource.Flags.cs` -> `PublicState.Flags`; additive codec block
    `FLAGS { F { recordingId site placedBy plaque flagUrl body lat lon alt rot ut pickupUT } }`
    (OV-36, E.1.1): one row per flag planted in an ERS recording, read from that recording's
    flag events (the ones `TrajectorySidecarBinary.cs:316` reads), with `pickupUT` the UT of
    a matching pick-up when the own model holds one, else empty. The fields are those of
    r3w section 9.1 and R3W-23's `RivalFlagRecord`; R3W-23 reads this block, so its
    fallback (opening each past flight's trajectory) is not needed.
  - Both writers, the `vtype` key and the `FLAGS` block, are reached from the export and
    their read branches from the codec's read, so every member here is live (A1); the
    rival-side readers are R3W-11 / R3W-18 (`vtype`) and R3W-23 (`FLAGS`).
- **Proof:** `StandWindowQueryTests` table: standing, recovered, flown again, deleted,
  destroyed, debris skipped, superseded leaf absent; export block tests (`vtype` from the
  snapshot, empty when none; a planted flag gives one `F` row, a picked-up one carries
  `pickupUT`, a flag of a superseded recording is absent).
- **Registers:** layer pins (Rewind -> Missions and Rewind -> Ghost are same-layer pairs,
  reported only).
- **Size:** M. **Depends on:** R3E-04, R3E-12. **PC:** no. **Risk:** medium.

### R3E-15 Disclosure facts

- **Change:** `RaceExportSource.Disclosure.cs`: the loaded assemblies' names and versions,
  sorted, and their digest; the digest of the save's difficulty parameters through the same
  `RaceCore` canonical digest R1-10 uses for `race.cfg`'s `difficultyDigest`;
  `testTooling` from `FactCapture.TestToolingUsed` (key `testTooling`, member
  `TestToolingUsed`, OV-28 / OV-32; R4 adds nothing to the wire). Persistence: a race facts
  child (`RACE_FACTS { testTooling }`, later R3E-09's `DEBUG_COMPLETION` rows), an
  `IRaceScenarioChild` (R1-03's child Save / Load hook, A4) registered on the race scenario
  node, whose `Load` restores `FactCapture` through a restore member added here (its first
  caller) and whose `Save` writes it, so the fact holds across scene changes; its row in
  `LoadReconcilePolicy` (every load takes the loaded save's copy, section 9 item 8) lands
  here. Codec `DISCLOSURE { modDigest difficultyDigest
  testTooling MOD { name version } }`. Showing it is R6.
- **Proof:** digest stability tests; export block test; the facts child round trips through
  the hooks; `LoadReconcilePolicyTests` cover the new row.
- **Registers:** `LoadReconcilePolicy` row. **Size:** S. **Depends on:** R3E-04, R3E-06,
  R1-03, R1-10. **PC:** no. **Risk:** low.

### R3E-16 The main-thread transport and the copy-out

- **Change:**
  - `RaceCore/IRaceTransport.cs` and the `Transport` slot of R3E-04's `SyncPorts` land here,
    with their first caller (A1): `RequestCopy(direction, from, to)`,
    `RequestListing(sharedRelativeDir)`, `Drain()` -> arrivals (copied: path, proof,
    length; listed: names; failed: reason). `RequestProof(sharedRelativePath)` (arrival:
    proof value) is specified here and lands with R3E-20, its first caller. Requests are
    idempotent by key; the port is what the worker (R3E-23) also implements. The streaming
    hash variants and the shared-folder layout of R3E-01 land here too.
  - The fold (OV-25): `IRaceTransport` replaces R1-10's `IRaceSharedFolder`. R1-10's adapter
    `RaceWorld/RaceSharedFolder.cs` and its pure `RaceFolderCheck` move onto the transport in
    this pull request, and `IRaceSharedFolder` is deleted with its last caller.
  - `RaceWorld/RaceMainThreadTransport.cs`: at most one operation per frame with a byte
    budget (256 KB chunks into `<name>.parsek-part`, then rename; streaming SHA-256), retry
    with backoff across frames (three tries at about 0.2 s, 1 s, 5 s) on `IOException`, then
    the request fails until the next tick. It never runs inside `OnGUI`.
  - `RaceSync.CopyOut.cs`: seq = max(shared listing, own mirror) + 1 (6.3); every own
    standing not yet copied out goes oldest first, each after every payload file it names is
    confirmed in the shared folder; `copiedOut` marked in the own index. While the transport
    is the main-thread one, an export in FLIGHT writes the own mirror only and the copy-out
    waits for the next tick outside flight (section 9).
  - `SyncResult.Status` { offline, exporting n, up to date }; one rate-limited Warn per
    failure kind.
- **Proof:** the test project gains `FakeRaceTransport`, a thin `IRaceTransport` over T-02's
  `FakeSyncedStore` (completes at once, on demand, or fails; OV-17). `RaceSyncCopyOutTests`
  with it, R1-08's in-memory folder and T-02's `FakeClock` (offline then back: oldest
  first, payload before standing; partial arrival; a higher seq in the shared folder);
  `RaceMainThreadTransportTests` on temp dirs (a destination held open with `FileShare.None`
  retries, then succeeds after release); R1-10's `RaceFolderCheckTests` pass over the
  transport.
- **Registers:** none beyond log tags.
- **Size:** M. **Depends on:** R3E-04, R1-08 (`sharedFolderPath`, the in-memory folder),
  R1-10 (`IRaceSharedFolder` and its adapter), T-02. **PC:** no (S8 adds the real numbers).
  **Risk:** medium.

### R3E-17 Sync ticks

- **Change:** `RaceSyncDriver.Tick(TickKind { Load, SceneEnter, Poll, Commit })`.
  Controllers wiring (R1-08's composition files): session load, `ParsekKSC` and
  `ParsekTrackingStation` scene entry and a 60 s real-time poll there (logged under
  `RaceSync`, OV-57). Specified here, landing with R4-09 as an edit to this driver's file
  (its first caller, second-pass A1): `RequestSyncNow()` and `TickKind.SyncNow`, for the
  Sync now button R4-09 draws in R1-18's Settings section (OV-40). Commit ticks: once a
  second the driver compares `IPublicStateSource.ChangeToken()` (R3E-02 specified it; it
  lands here, its first caller) with the last one (the cached ERS / ELS instances) and,
  when it changed, runs an export-only tick; in FLIGHT every tick is export-only and no
  rival standing is applied. No new static event subscription. After each tick that
  reached the shared folder the driver writes `lastSyncRealTime` through R1-08's
  `RaceLocal` codec (this task is that key's writer, OV-27).
- **Proof:** driver tests with a fake source and T-02's `FakeClock` (token change -> one
  export tick; FLIGHT -> import never called; `lastSyncRealTime` written after a reaching
  tick, not after an offline one); a source gate test that `RaceWorld` calls no import step
  from a FLIGHT path.
- **Registers:** none. **Size:** S. **Depends on:** R3E-16. **PC:** no. **Risk:** low.

### R3E-18 Import: copy-in, completeness and the verdict

- **Change:**
  - `RaceSync.Import.cs`: for each rival id listed by the race's player manifests (rule 1 of
    7.15: only `players/<id>/`), list its standings; take the highest candidate; copy it into
    `rivals/<short>/incoming/`; parse (refused whole on an unsafe id, a bad stamp, a
    `playerId` that differs from the folder); copy every named payload file not already
    cached; complete = every named file present with a matching proof -> move into
    `standings/` and `payload/`; incomplete -> keep the previous standing, status "still
    arriving", retry next tick (edge case 1); a newer `raceFormat` or recording format ->
    status `NewerFormat` with the milestone block parsed when it parses (6.3); conflict
    copies ignored with a rate-limited Warn (edge case 2).
  - First production callers of R3E-01's `IRaceFolder.Move` (out of `incoming/`) and the
    `RaceIds` short id; the rival cache paths of `RacePaths`, which R3E-01 specified, land
    here with their tests (A1).
  - Applying a standing also copies the rival's `player.cfg` (the manifest read in this
    tick) to `rivals/<short>/player.cfg`, so the cache alone can name and dress a rival.
  - Builds the values of R3E-04's `RivalStandingVerdict` (the type stays R3E-04's, A4; one
    per rival, in `SyncResult.Verdicts`; an Applied verdict is read back from the cache
    through R3W-05's `RaceCore/RivalCache.ReadVerdicts`, the same reader a scene load uses,
    which lands with whichever of R3W-05 and this task merges first) for
    R3W-05 (OV-37: it takes the verdict in place of `AppliedRival`; R4-04 builds
    `RacePlayerRecord` from verdicts and manifests); until R3W-05 consumes it the driver
    logs one line per rival.
  - Just joined: the key `firstImportDone` in `RaceLocal`, carried by R1-08's codec (R1-11's
    join writes false; this task is its writer of true, OV-27); export is skipped until
    every listed rival's newest standing was applied or refused for a reason other than
    incomplete (7.5).
  - Clash re-check (E.1.7): after the import, the driver calls R1-09's
    `RaceSlots.ResolveClashes` over the player manifests read in this tick and hands the
    result to R1-08's `RaceSession`; a "same site" or changed-look outcome becomes one
    message in `SyncResult.Messages`, posted by R3E-22's driver site.
  - Order inside one tick: export, then import.
- **Proof:** `RaceSyncImportTests` with folders built by T-16's `RaceFolderBuilder`:
  half-synced, proof mismatch, unsafe id, newer format, conflict copy, late joiner, export
  before import, a later joiner taking a seen look gets the clash outcome once.
- **Registers:** log tags.
- **Size:** M. **Depends on:** R3E-16, R3E-22, R1-08, R1-09, T-16; R1-11 writes the false
  (soft).
  **PC:** no. **Risk:** medium.

### R3E-19 Witness log, chain, receipts and the neutral mark

- **Change:**
  - `RaceCore/WitnessLog.cs` + codec: `rivals/<short>/witness.cfg`,
    `SEEN_STANDING { seq proof firstSeenUtc }` per applied standing (6.1); the
    `witness.cfg` path R3E-01 specified lands here.
  - `RaceSync.Witness.cs`, before applying candidate N: a witnessed seq whose file now has
    another proof -> changed; N's `prevSeq` present with a proof that is not `prevHash` ->
    chain broken; `prevSeq` absent -> "history incomplete" and applied; the highest witnessed
    seq gone with no higher complete standing -> went backwards; two proofs seen for one seq
    -> the two-machine warning of edge case 3. On changed, broken or backwards: not applied,
    the last good standing stays, mark `FilesChanged(playerId, reason)`.
  - Receipts: export writes `SEEN { RIVAL { playerId seq hash } }` from the witness maxima
    taken before this tick's import; import checks a rival's `SEEN` against the own witness
    for third players and against the own mirror for this player, and records
    `ReceiptDisagreement(a, b, seq)`. Marks change no score.
- **Proof:** `RaceSyncWitnessTests` (edge cases 7b and 7c, pruned middle -> incomplete,
  deleted latest -> backwards, receipt disagreement, rebuilt cache applies and shows the
  disagreement); `RaceSyncLoopbackTests`: two players export and import each other for
  twenty ticks with edits injected, run on T-18's two-machine simulator (one shared
  `FakeSyncedStore`; OV-19: no second machine fake here). They test the feature's message
  flow; T-31 tests the board as a property on the same simulator.
- **Registers:** none. **Size:** M. **Depends on:** R3E-18, T-18. **PC:** no.
  **Risk:** medium.

### R3E-20 Self-heal of the own subfolder

- **Change:** `RaceSync.Heal.cs`, each tick with the folder reachable: compare the own
  subfolder's listing and proofs (`RequestProof`, which lands here on `IRaceTransport`, the
  main-thread transport and the fake, its first caller; R3E-16 specified it) with the own
  mirror. A changed or deleted
  standing is put back from the mirror; a payload file from the mirror or, while the save
  still holds those bytes, through `IRacePayload.TryReadOwn`; an unknown higher seq gets a
  fresh standing after it at once with `note = "replaces <a>..<b>"`; unpublished own
  standings that collide with an unknown file of the same seq are renumbered after it (they
  were never seen). One screen message per heal event through `SyncResult.Messages`. A file
  put back twice within three ticks is not put back again; the driver warns naming the
  two-machine cause (question 1).
- **Proof:** `RaceSyncHealTests` (each case; the fight stop); `RaceSyncLoopbackTests` gains
  its self-heal case on T-18's simulator (section 6).
- **Registers:** `docs/dev/design-gui-inventory.md`: the heal message is listed under the
  driver's one screen-message site (R3E-22; no new site, OV-38).
- **Size:** M. **Depends on:** R3E-16, R3E-19, R3E-22, T-18. **PC:** no. **Risk:** medium.

### R3E-21 Keep and prune

- **Change:** `RaceCore/KeepRule.cs`: kept = the last 20 standings, every standing that is
  the `since` of a milestone date listed by the latest standing or by the last standing
  stamped before `endRealTime`, and that last pre-end standing; plus the payload they name.
  `RaceSync.Prune.cs`: deletes unkept files of the own subfolder 3 days of real time after
  they became unkept (`unkeptSince` in the own index); the own mirror keeps the same set and
  the last pre-end standing's payload for good; the rival cache keeps the same set per rival,
  pruned only on import ticks outside FLIGHT.
- **Proof:** `KeepRuleTests`; prune tests with T-02's `FakeClock`; `endRealTime` from
  R1-08's `RaceManifest`.
- **Registers:** none. **Size:** M. **Depends on:** R3E-11, R3E-18. **PC:** no.
- **Risk:** low.

### R3E-22 "N shared flights left the race"

- **Change:** at a successful export, count tree ids present in the previous own standing
  and absent from the new public state (a Re-Fly inside a tree is not counted); when above
  zero, one message ("2 shared flights left the race") in `SyncResult.Messages`.
  - The driver's one screen-message site lands here, in the first task with a message to
    post (OV-38): after each sync tick the driver posts every entry of
    `SyncResult.Messages` with `ParsekLog.ScreenMessage` and logs one Info line per message,
    `[RaceSync] Message posted: <text>` (a screen message writes no log line of its own;
    T-32's `logContract` reads this one). Every later race message
    (R3E-18's clash note, R3E-20's heal, R4-07's holder changes) is appended to that list
    and uses this site; no other task adds one.
- **Proof:** tests for quickload-shaped and Re-Fly-shaped differences; a driver test that
  each message of a result is posted once.
- **Registers:** `design-gui-inventory.md` gains the driver's one screen-message site with
  this message under it (OV-38); CHANGELOG line when the feature becomes
  player-visible.
- **Size:** S. **Depends on:** R3E-04. **PC:** no. **Risk:** low.

### R3E-23 The copy worker (only with the owner's yes)

- **Change:** `RaceWorld/RaceCopyWorker.cs`: one background thread with lock-protected
  request and result queues; copies to a temp name and renames, retries with backoff
  (sleeping on the worker), streams SHA-256, lists directories; touches no Unity object,
  calls neither `ParsekLog` nor `FileIOUtils`, formats only invariant numbers; its log
  records are queued and written by the main thread. `RaceWorkerTransport` implements
  `IRaceTransport` over it; the driver uses it when present. It stops at session end (join
  with a timeout) and on quit. With the worker, a FLIGHT export also copies out.
- **Proof:** xUnit over temp dirs with an injected slow file layer (a stalled open delays
  only the worker); owner PC: S8's sync clients rerun through the worker.
- **Registers:** `scripts/background-threads-audit-allowlist.txt` entry with its rationale;
  `.claude/CLAUDE.md` InvariantCulture paragraph names the one thread (then
  `cp .claude/CLAUDE.md AGENTS.md`); lifetimes (instance state only).
- **Size:** M. **Depends on:** R3E-16, S8a, S8b, owner's yes (question 2). **PC:** yes.
- **Risk:** high; a thread in a mod that has none.

### R3E-24 In-game exchange check

- **Change:** `InGameTests/RaceExchangeTests.cs`, category `RaceExchange` (SPACECENTER): in
  a race save, export to a temporary shared folder, read the standing back, check every
  proof, load the public ghost through `SnapshotSidecarCodec.TryLoad`; skip naming the
  context in a single-player save.
- **Proof:** the test itself; `parsek-test-results.txt` and `KSP.log` kept as evidence.
- **Registers:** the category (SPACECENTER only) is hosted by T-29's RACE-9 (OV-09); a later
  test in it re-pins that spec's `total=` in the same pull request.
- **Size:** S. **Depends on:** R3E-18, R3E-20. **PC:** yes. **Risk:** low.

## 5. Parallel tracks

| Track | Tasks | Notes |
|-------|-------|-------|
| Core export | R3E-01 (after R1-10) -> 02 -> 03 -> 04 | sequential; they share `RaceSync.cs`, `PublicState.cs`, `RaceExportSource.cs`; R3E-04's golden `v3` may follow as its second pull request |
| Base facts | R3E-07 and R3E-12 at once (no dependency); R3E-06 when R1-04 and R1-08 land; then R3E-08 (needs 06, 07), R3E-13 (needs 06, 12), R3E-09 (needs 08, 15, S9) | run beside the core track from day one |
| Blocks | after 04: R3E-10, 11, 14, 15, 22 together | each adds a `RaceExportSource.<Block>.cs` partial and one codec block (R3E-14 two: `LEFTOVERS` and `FLAGS`); they touch `PublicState.cs`, `Standing.cs` and `StandingCodec.cs` with small additive edits, so merge them one at a time |
| Exchange | after 04 (with T-02 and R1-10): R3E-16 -> 17; 16 -> 18 (with T-16) -> 19 (with T-18) -> 20; 21 after 11 and 18; 23 after 16; 24 after 20 | R3E-17 and R3E-18 can run together |

Hot files that force an order: `RaceSync.cs` and `RaceSyncDriver.cs` (01, 02, 04, 16, 17, 18,
20, 22); `PublicState.cs`, `Standing.cs`, `StandingCodec.cs` (the block tasks and R3E-09);
`Recording.cs`, `RecordingTreeRecordCodec.cs`, `RecordingOptimizer.cs` (R3E-07, and any other
slice that adds recording fields); `GameAction.cs` and the ledger switch files (R3E-12);
`ParsekScenario.VesselLifecycle.cs` (R3E-13); `Fixtures/FormatGolden/` (R3E-04's `v3` after
R1-12's `v2`); R1-08's `RacePorts.cs`, `RaceFolder.cs`, `RaceTimestamp.cs`, `RaceIds.cs`
(R3E-01) and R1-10's `RaceSharedFolder.cs` (R3E-16), `RaceFolderCheck.cs` (R3E-01, R3E-16),
`RaceExchangeTests.cs` (R3E-01, R3E-24), each after its
R1 task; `ParsekConfigFactCapture.cs` (R3E-06, 09, 15); `LoadReconcilePolicy.cs` (R3E-15);
`lifetimes.toml` and the reset allowlist (R3E-06); the threads allowlist (R3E-23).

## 6. Tests and proof

- **xUnit, pure (`RaceCore`):** hash, time, file names, paths, standing codec (read, write,
  round trip, refusals, culture; the `ToolsFromUT` / `ToolIds` view's tests land with
  R6-01), digest, milestone
  table, keep rule, witness, every `RaceSync` stage against R1-08's in-memory `IRaceFolder`
  + `FakeRaceTransport` (R3E-16's tests, over T-02's `FakeSyncedStore`) + T-02's
  `FakeClock`, with shared folders built by T-16's `RaceFolderBuilder` (OV-17). They prove
  the protocol of 6.3 and 7.15 without a game.
- **xUnit, loopback:** `RaceSyncLoopbackTests` runs two or three players for many ticks on
  T-18's two-machine simulator with offline windows, edits, prunes and late joins (OV-19);
  it proves that no tick applies a changed file, every player converges on the others'
  latest complete standing, and no standing names a missing file. Its self-heal case (a
  player's shared subfolder edited or deleted between ticks is put back) depends on R3E-20
  and lands with it.
- **xUnit, export over the real model:** `RaceExportSourceTests`, `OwnPayloadReaderTests`,
  `PublicGhostProjectionTests` with `RecordingBuilder` / `VesselSnapshotBuilder` state in
  `RecordingStore` and the ledger (`[Collection("Sequential")]`, reset calls). They prove
  ERS / ELS are the only reads and the not-ready rules.
- **xUnit, base additions:** A1 collector, A2 row type switches and capture decision, stand
  query table, A3 codec / merge / split / clone, the debug-completion fact; plus
  byte-identity of a single-player tree and ledger with the feature compiled in.
- **Golden:** the `v3` folder through `FormatGoldenV3Tests` and the three gate tests;
  R3E-04's cells in R1-03's `RaceFormatPinTests`.
- **In-game (owner's PC):** one scene per category (OV-09), hosted by T-29's lanes:
  `RaceFacts` (FLIGHT, RACE-10: A2 and A3 capture, R3E-08, 09, 13),
  `RaceFactsTrackingStation` (TRACKSTATION, RACE-11: R3E-13's terminate) and `RaceExchange`
  (SPACECENTER, RACE-9: R3E-01's folder cell, R3E-24). They prove the stock events behave
  as the VERIFY items assume.
- **Owner's PC, not automated:** S8 measurements and the worker over Dropbox, Google Drive and
  OneDrive placeholders (R3E-23); one two-machine session over a real sync client.
- **Harness:** no lane of its own; the whole-save isolation lane is T-27 (RACE-4), the
  two-player lanes are T-28 (RACE-6) and T-32 (RACE-7), driving two race saves through T-19's
  `RaceCommand action=sync|offline|online`; the in-game categories ride T-29's hosts.

## 7. Gates and registrations

- **Architecture map:** no new module; every file lands under R1-01's folder rules for
  `RaceCore`, `RaceExport`, `RaceWorld`, or matches an existing prefix
  (`ParsekConfigFactCapture.cs` -> Config; `EffectiveState.*.cs` -> Rewind;
  `FlightRecorder.*.cs` -> Recording; `GameActions/` and `Patches/` by folder). Re-pin knot,
  layer and facts pins only in a change that moves them.
- **Layers and forbidden edges:** `RaceExport` names only `RaceCore`, floor, Recording
  (paths, codecs, `Recording`), Rewind (`EffectiveState`), GameActions (`GameAction`,
  `CommittedMilestoneReach`), Missions (`MissionStore`, `VesselIdentity`); never Controllers
  (hence `EffectiveState.HasActiveReFlySession`). `RaceWorld` names no Recording type in this
  plan's files. Base files name nothing of the race (`FactCapture` is neutral).
- **15.4 grep gates (R1-06 creates them):** no `*Store` in `RaceCore`; no own-model writer in
  the race modules; no `RecordingStore`, recorder, pending slot or commit / discard service in
  race files.
- **Headless:** `RaceCore` stays leak-free; `headless_pins.toml` unchanged.
- **Uncalled-method ratchet (second-pass A1, third-pass A5):** every task lands only
  members its own production code (the driver's start, tick and log lines, the capture
  sites, the export) or its own in-game cell reaches; an xUnit test alone makes nothing
  live. A member with a later first caller is
  specified and lands there: `NextStamp` (R3E-04); `TestToolingUsed`,
  `NoteTestTooling` (R3E-08); `IRaceTransport`, the `Transport` slot, the streaming hashes
  and the shared layout (R3E-16); `ChangeToken` (R3E-17); the rival cache paths (R3E-18);
  `witness.cfg` (R3E-19); `RequestProof` (R3E-20); the driver's posting of
  `SyncResult.Messages` (R3E-22); the codec's `ToolsFromUT` / `ToolIds` view (R6-01);
  `RequestSyncNow` and `TickKind.SyncNow` (R4-09); the `VesselEnded` row builder (R3E-13).
  `RivalStandingVerdict` and `Standing` are fields only. R3E-01's `List`, `Exists`, `Move`,
  `Delete` and short id are live through its `RaceExchange` cell (ruling A5), and
  `RacePaths.LongestExpected*` through R1-10's `RaceFolderCheck` call site it updates.
- **Statics:** one new static type (`FactCapture`, `save` lifetime, cleared by the reset,
  written only by itself, restored from R3E-15's race facts child through R1-03's hooks).
  Everything else is instance state held by the session.
- **Background threads:** one allowlist entry in R3E-23, only with the owner's yes.
- **ERS / ELS gate:** no allowlist entry.
- **Save format:** no row and no bump here: R1-12 registers `race-link` and
  `race-exchange` and writes `v2`; R3E-04 adds golden `v3` at `race-exchange = 1` and cells
  in R1-03's `RaceFormatPinTests` (OV-15, OV-16); the `FLAGS` block, `vtype`, `TOOL` and
  `DEBUG_COMPLETION` rows are additive keys; A2 and A3 are additive (no bump); the
  post-change checklist for R3E-07 and R3E-12; R3E-15's `LoadReconcilePolicy` row for the
  race facts child (OV-28).
- **GUI gates:** no new surface. Screen messages: one site, the driver's (R3E-22), the one
  producer of race messages (OV-38); the heal message (R3E-20), the "left the race" message
  (R3E-22), the clash note (R3E-18) and R4's board messages are listed under it in
  `design-gui-inventory.md`.
- **Logs:** tags entered in R1-06's tag list (OV-57); `scripts/arch/log_tags.json` refreshed
  (report-only) when a task adds a tag.
- **Docs per commit:** `docs/dev/todo-and-known-bugs.md` entries for the gaps of section 9
  that stay open; CHANGELOG only when a race player can see the effect; `.claude/CLAUDE.md`
  (and `AGENTS.md`) only for the worker exception and a "Key source files" pointer to the
  exchange once R3E-04 lands.

## 8. Risks

- **Export churn from the epoch.** The commit flush keeps the epoch and the next save rewrites
  the `.prec` with new bytes (section 3.1), so a standing exported between the two would be
  replaced by one that re-uploads every touched trajectory. The plan treats
  `SidecarEpochAdvancePending` as not ready; the cost is that a commit tick in flight rarely
  exports until the next save. Noticed by the `notReady reason=epoch-pending` count.
- **Main-thread stall.** A cloud placeholder or an offline share can block one open for
  seconds (15.9). Mitigation: no shared-folder access in flight without the worker; S8
  numbers; R3E-23.
- **Privacy leak in a payload.** The `.prec` and the public ghost could carry a kerbal name or
  another private value. R3E-03 strips `crew`; VERIFY the rest by reading a real `.prec`
  event list and a real snapshot; a test pins the stripped key set.
- **False vessel ends.** Scene unload fires destroy events; R3E-13 hooks `Vessel.Die` and an
  in-game test leaves and re-enters flight expecting zero rows.
- **A missed ledger switch** for `VesselEnded` (eleven sites). The enumeration test reds.
- **ConfigNode text.** Vessel and mission names with `//`, braces or newlines may not round
  trip in a `.pstd`. A codec test with hostile names; such characters are escaped or the name
  is dropped with a log line.
- **Golden folder collision.** Settled by OV-15: `v2` is R1-12's and `v3` is R3E-04's; a
  third slice that writes a folder first must take the next number, and the coverage gate
  names any missing version.
- **Two machines on one player** fight through the heal; the fight stop of R3E-20 and the
  edge-case-3 warning make it visible.
- **Witness loss.** A deleted cache restarts the witness log (section 9 of the design); the
  receipts of other players still show disagreements.
- **Clock skew.** Stamps are max(now, previous + 1 s); a wrong clock only affects the end-date
  rule (R6) and the "arrived late" mark.

## 9. Corrections and gaps

1. **Ghost payload is always derived.** 4.3 never shares the roster, but a separate-mode ghost
   snapshot is a full vessel node whose parts carry `crew` values (`Recording.cs:433`,
   `CrewReservationManager.cs:53`). So every ghost payload is a stripped projection, not the
   save's bytes, and 6.3's "take each hash from the sidecar version" holds for `.prec` only.
2. **"Parts only" is undefined** (6.3). The plan strips `crew` and keeps parts, modules and
   resources; VERIFY against a real snapshot that nothing else private remains.
3. **`REC ... hash` is one field but a recording has two payload files**, and each needs a
   16-hex name and a SHA-256 proof. The plan writes `prec`, `precProof`, `ghost`,
   `ghostProof`, and `TREE { id hash proof }`.
4. **The chain needs `prevSeq`.** With pruning, `prevHash` alone cannot tell "history
   incomplete" (the named standing is gone) from "chain broken" (a different file has that
   seq). The plan adds `prevSeq`.
5. **`IRaceTransport` cannot list or hash** in 15.3. Copy-out needs the folder's highest seq,
   import needs the standings list, heal needs proofs of shared files. The plan adds
   `RequestListing` and `RequestProof`. Settled: R3E-04 declares the port in its `SyncPorts`
   bundle and R3E-16 implements it, folding R1-10's `IRaceSharedFolder` into it (E.1.10,
   OV-25).
6. **`IRacePayload`'s adapter is split.** 15.3 places it in `RaceWorld` "over the save's
   sidecar versions", but reading the save's sidecars is reading the own model, which only
   `RaceExport` may do (15.2), and `RaceWorld` may name Recording only in its loader. The own
   half is in `RaceExport`; the rival-cache half is R3RW's. Settled: the port is
   `IRacePayload`, its rival read half R3W's (OV-37).
7. **A3's date rule reaches back inside one recording.** "A recording of its tree that begins
   at or before that date carries a tool" marks a date even when the tool was first used
   after it, contradicting "a tool used later does not reach back" (7.12). The plan stores
   the first-use UT per tool so R4 can use "tool UT <= date, same tree". Settled: the wire
   form is `TOOL` children with `ut`, and the codec derives `ToolsFromUT` and `ToolIds`
   (OV-32, DC-079).
8. **"Test tooling ran, for good" has no home.** It is a save-level fact; the plan keeps it in
   `FactCapture`, persisted on R1's race scenario node, and also marks flights recorded while
   it ran. A load from before it ran loses it; acceptable for a disclosure fact. Settled:
   key `testTooling`, member `TestToolingUsed`, persisted by R3E-15 through R1-03's child
   hooks (OV-28, DC-008).
9. **A2 lives in the ledger.** 6.2 says the ledger types gain nothing; A2's home was left to
   this plan. A new additive `GameActionType` follows ELS (tombstoned with a superseded
   flight), deduplication and the committed-future rules for free, like `VesselRecovered`.
   It is an enum member, not a new field on existing rows.
10. **A2 after a rewind.** A `VesselEnded` row is a committed fact like a recovery row; after a
    rewind before it the vessel stands again locally, yet the standing still ends its
    leftover there. The plan accepts this; VERIFY what single player does with a recovery row
    past the rewind UT and match it.
11. **The design's "destroyed outside a recording" needs a real-destruction signal.** Destroy
    events also fire on scene unload and quit (`GhostTrackingStationPatch.cs:225`); the plan
    hooks `Vessel.Die` (VERIFY).
12. **Milestone rows tagged to a flight outside ERS** are not covered by edge case 12. The plan
    drops them (logged) rather than crediting a different flight; VERIFY whether rewind-retired
    recordings leave such rows.
13. **Temporary files in the shared folder.** `FileIOUtils` leaves `.tmp` and `.bak.<guid>`
    names (`FileIOUtils.cs:61`, `:548-627`) that sync clients upload; the strict grammar
    ignores them, which the design does not say.
14. **`FileIOUtils` cannot serve the worker.** It logs through `ParsekLog` and has no retry
    (`FileIOUtils.cs:256-627`); the worker keeps its own small copy routine.
15. **Edge case 3 versus self-heal.** Two machines writing one player would heal over each
    other forever; 7.15 says put back, edge case 3 says only warn. The plan stops after the
    second put-back of one file and warns (question 1).
16. **Unpublished standings colliding with an unknown same-seq file** (an own standing written
    offline while another machine published that number) is not covered. The plan renumbers
    the unpublished tail; it was never seen, so no receipt names it.
17. **Grace period** for pruning is unnamed in 6.3; the plan uses 3 days of real time.
18. **Export in flight** with the main-thread transport would touch the shared folder from a
    Unity callback; the plan writes the own mirror only and copies out after the flight.
19. **The commit tick has no event.** `MergeDialog.OnTreeCommitted` is a static delegate field
    (`MergeDialog.cs:73`) that `ParsekFlight` adds to (`ParsekFlight.cs:1194`); another adder
    would be a static written by another type. The plan polls the cached ERS / ELS instances.
20. **The Re-Fly check needs a Rewind-side read.** The marker accessor is in Controllers
    (`SessionSuppressionState.cs:50`); the plan adds `EffectiveState.HasActiveReFlySession`.
21. **`raceFormat` has two writers.** R1's `race.cfg` carries `raceFormat` too; there is one
    `SaveFormat` row and one golden folder. The plan registers the row in R3E-04 with
    `race.cfg`, `player.cfg` and standings in the folder; R1 should not register it first.
    Settled the other way: R1-12 registers both rows at 1 and writes `v2`; R3E-04 registers
    nothing, bumps nothing and adds `v3` with a standing, its payload and an own mirror
    (OV-15, DC-122).
22. **Tree payload shape** is not in the design; R3E-02's `RACE_TREE` key list must be agreed
    with R3RW's loader, including `sidecarEpoch`, which the `.prec` reader checks. Settled:
    the tree is exported in the production `RecordingTree` node shape through the production
    writer, with the terminal and endpoint keys, on one key list agreed with R3W-03 in
    R3E-02's pull request (OV-35).

## 10. Questions for the owner

1. **The same player plays one race on two computers at once.** Each game puts back the files
   the other wrote, so they keep overwriting each other. Options: (a) keep putting files back
   every time (as 7.15 says); (b) put back twice, then stop and say on screen "your race
   folder is being written from two computers". Recommendation: (b); it ends the fight and
   tells the player the cause.
2. **The background copy worker (15.9).** Shared folders on cloud drives can freeze the game
   for seconds when a file is opened the first time. Options: (a) allow one background
   worker that copies files and checks them, an exception to the "no threads" rule;
   (b) keep everything on the main thread, with copying paused during flights and a possible
   freeze in the Space Center. Recommendation: (a) if spike S8 shows freezes longer than one
   second on any common sync client; otherwise (b). The plan builds (b) first either way.
