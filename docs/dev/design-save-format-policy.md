# Parsek Save Format Policy - Design Document

*The binding rules for how Parsek versions, upgrades and keeps its persisted data from the 1.0
save format on.*

**Status:** BINDING. Owner ruling (V, 2026-11-04): the next release is the first stable save
format, the 1.0 baseline. Today's version numbers stay as the baseline ("any safe number"), and
the alpha-era migration and repair code is deleted ("clean, without old baggage"). Todo
`SAVE-FORMAT-1-0-FREEZE` (done). Code authority: `Source/Parsek/SaveFormat.cs`.

---

## 1. The 1.0 baseline

Every versioned on-disk format is one row of `SaveFormat.All`. `Baseline` is the value the 1.0
release writes and never changes; `Current` is the live constant this build writes (the row
references it, it is never copied), and only grows.

| Format (`SaveFormatInfo.Name`) | Key / header field | Baseline | Code constant |
|---|---|---|---|
| `save` (save-wide) | `PARSEK/parsekSaveFormat` | 1 | `SaveFormat.CurrentSaveFormat` |
| `recording-format` | `RECORDING/recordingFormatVersion` | 1 | `RecordingSchema.CurrentRecordingFormatVersion` |
| `recording-schema-generation` | `recordingSchemaGeneration` (recording node, PSK0 / PSN0 headers) | 4 | `RecordingSchema.CurrentRecordingSchemaGeneration` |
| `tree-format` | `RECORDING_TREE/treeFormatVersion` | 0 | `RecordingTree.CurrentTreeFormatVersion` |
| `trajectory-binary` | `.prec` PSK0 header `formatVersion` | 1 | `TrajectorySidecarBinary.CurrentBinaryVersion` |
| `snapshot-sidecar` | `.craft` PSN0 header `formatVersion` | 0 | `SnapshotSidecarCodec.CurrentFormatVersion` |
| `ledger` | `LEDGER/version` | 0 | `Ledger.CurrentLedgerVersion` |
| `game-state-store` | `PARSEK_GAME_STATE/version` | 1 | `GameStateStore.CurrentEventsVersion` |
| `milestones` | `PARSEK_MILESTONES/version` | 1 | `MilestoneStore.CurrentMilestonesVersion` |
| `mission-selection` | `MISSION/selectionSchemaGeneration` | 1 | `Mission.CurrentSelectionSchemaGeneration` |

`parsekSaveFormat` is new in 1.0. `ParsekScenario.OnSave` writes it, plus the informational
`parsekWrittenBy` (the assembly version; never read for a decision), through
`SaveFormat.StampScenarioNode`. It versions every store in the PARSEK scenario node that carries
no version of its own: rewind points, supersede relations, ledger tombstones, merge journal,
Re-Fly marker, supply routes and their event log, vessel names, the missions container, crew
replacements, group hierarchy, held windows, the stock pool reference and the other markers.

Not versioned: the `.pann` annotation cache, which is recomputed whenever it does not match.

## 2. The rules

1. **Every build loads every 1.0+ save.** No 1.0+ data is ever rejected for being older.
2. **Additive changes need no bump.** A new optional key, a new child node, a new enum member
   renames nothing and changes no layout; the reader maps an absent key to its default.
   Deleting a retired feature's keys, when the reader already maps their absence to the
   feature-off default, is not a shape change either: stop writing and reading them.
3. **A shape change bumps the format and ships an upgrade step.** A renamed or removed key, a
   changed meaning, a changed layout: bump that format's constant by one and register a
   `SaveFormatUpgradeStep` (`From = N`, rewriting a node of shape N into shape N + 1) in the
   step registry of `SaveFormat.cs` in the same commit. Steps chain, so a reader at N + 3 brings
   N data forward through three steps. A binary sidecar layout step is a decode branch kept in
   the reader, registered with a null node delegate.
4. **Every reader classifies its stored number through `SaveFormat.Classify`:**
   - `Current` - loads.
   - `Upgradable` (baseline <= stored < current) - the reader runs the steps on a copy of the
     node, then loads and writes current on the next save. Upgradable with a missing step is
     never read as current: a recording is rejected as `generation-older` (its committed tree
     kept inert) and a ledger is held like `Newer`.
   - `Newer` (stored > current) - held verbatim and written back, never read as current, never
     coerced, never wiped (section 4).
   - `Missing` / `PreBaseline` - pre-1.0 data, best effort (section 3).
5. **The save-wide stamp covers the unstamped stores.** A shape change to any store in section 1's
   list bumps `parsekSaveFormat` and registers a `save` step. A store that later needs its own
   number gets its own row.
6. **No new migration or repair code for pre-1.0 data.** A heal that also serves data the current
   build writes may stay (section 3).

## 3. Pre-1.0 saves: best effort

A handful of testers hold alpha saves. "Best effort" means:

- Data already stamped at the baseline (most recent alpha saves: recordings at format 1 /
  generation 4) loads as current. A save with no `parsekSaveFormat` logs one Info line
  (`pre-1.0 save (no parsekSaveFormat); loading best effort`) and loads; the next save stamps it.
- Recordings and sidecars stamped below the baseline are rejected
  (`generation-missing` / `generation-older` / `format-version-mismatch` / `magic-mismatch`),
  and a committed tree holding one is kept inert by `InertRecordingTrees`. A missing or
  pre-baseline `treeFormatVersion` loads as format 0.
- A ledger with no or a pre-baseline `version` starts empty (Info). Milestones and game-state
  events load every row they can read.
- Shapes only older builds wrote are no longer repaired: about 10,800 lines went, among them the
  old-save ledger event migration, broken-ledger recovery, the legacy part-purchase and
  facility-cost repairs, legacy action ids, contract deadline durations, the PRKB / PRKS magic
  probes, text-sidecar detection, `.pcrf` reaping, the epoch-0 bypass, the terminal-orbit
  back-fill, the double-cover retire, the legacy EVA rewind-point reap, the legacy retirement
  scrubs and the mission selection generation-0 upgrade. Legacy supply-route ledger rows
  (reserved types 23-30, 36) are kept verbatim as unreadable (`type-reserved`), never read.
- Kept, because they also serve data the current build writes: `RepairDuplicateRolloutActions`,
  `RepairMissingRecoveryDedupKeys`, the `CREW_REPLACEMENTS` slot fallback, the bare milestone-id
  fallback (committed fixtures carry them), `DropNonMonotonicTrajectoryPoints`,
  `SplitParentLinks.RepairStaleParents`, `RepairMissingContiguousChainPredecessors` and
  `MigrateKerbalAssignments`.

## 4. Downgrade: newer data is held

An older build opening a save from a newer one keeps what it cannot read and writes it back:

- **Recording trees.** A newer generation, format or `treeFormatVersion` (`tree-format-newer`):
  the committed tree is kept inert by `InertRecordingTrees` and re-emitted node-verbatim on every
  save, its recording ids counted as known.
- **Ledger.** Held verbatim (in-memory ledger empty) and written back unchanged to the save's
  `GAME_STATE` node and `ledger.pgld`, one Warn. While held, `Ledger.IsHeldFromNewerVersion`
  defers every KSP state patch, the committed-science cache rebuild and the warp-start facility
  patch, so the session keeps the save's funds, science and reputation.
- **Milestones.** A newer `version` is held with the scenario's `MILESTONE_STATE` nodes.
- **Game-state events.** A `GAME_STATE_EVENT` whose type this build does not know is kept raw and
  appended after the readable events on save; a non-current store `version` warns and loads every
  readable event.
- **Missions.** A newer `selectionSchemaGeneration` warns once per load and is written back as
  loaded; a `MISSION` node's unknown values and child nodes round-trip verbatim.
- **Save-wide stamp.** A newer `parsekSaveFormat` logs one Warn naming both numbers and the
  writer; the unstamped stores load best effort.
- **Additive enum members** read as unknown on an older build (for example a new `TerminalState`
  leaves `TerminalStateValue` null through the codec's `Enum.IsDefined` guard): degraded
  metadata, never corrupt data.

## 5. How to bump a format

1. Bump the format's constant (the right-hand column of section 1) by one, with a comment on the
   constant naming the shape change.
2. Write the upgrade step `N -> N + 1` and register it for that format in `SaveFormat.cs`. Unit-test
   it on a node of shape N.
3. Add the golden folder `Source/Parsek.Tests/Fixtures/FormatGolden/v<N+1>/` with the generator
   named in that tree's README, and its `formats.txt` and `checksums.txt`.
4. Keep every older `v*` folder unchanged: each must still load and upgrade to current.
5. Update the section 1 table's code column if a constant moved, and the CHANGELOG.

## 6. Tests that enforce it

- `SaveFormatTests.Registry_RowsReferenceTheLiveConstants_AndTheBaselineIsToday` pins every row
  to its live constant and every baseline to the section 1 value.
- `SaveFormatTests.Registry_EveryFormatAtBaseline_HasNoProductionSteps` asserts each format's
  registered step count equals `Current - Baseline`, so a bump without its step fails.
- `SaveFormatTests` also covers the classifier, the step chain, the stamp and the recording /
  tree / sidecar gates on Upgradable and Newer data; `SaveFormatHoldTests` and
  `SaveFormatLedgerHoldTests` the held stores.
- `FormatGoldenV1Tests` load the frozen 1.0 saves in `Fixtures/FormatGolden/v1/`.
- `SaveFormatGoldenGateTests` fail when a format is bumped without a new `v<N>` folder, or when a
  frozen golden file changes (its `checksums.txt`).

## 7. Known gaps

- While a ledger or milestones node is held from a newer version, rows made that session are not
  saved (one rate-limited Warn: `held-ledger-drops-session-rows` / `held-newer-drops-session`).
- `CrewReservationManager.ApplyToRoster` still runs under a held ledger, on the kerbal rows the
  load derives from the committed recordings.
- The stores under the save-wide stamp have no hold: a newer `parsekSaveFormat` only warns, and
  those stores are read best effort.
