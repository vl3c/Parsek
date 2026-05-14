# Recording/rendering v0 reset checklist

Date: 2026-05-11
Status: implementation in progress
Branch: `reset-recorder-renderer-v0`
Base: `claude/investigate-trajectory-logic-OtzVx` at `830f364d`

## Task surfaced

The todo item is `docs/dev/todo-and-known-bugs.md` -> "Open - reset recording/rendering schema versions to v0 and delete pre-release compatibility".

The work is Branch B from `docs/dev/plans/refly-cleanup-and-v0-reset.md`: make the current post-Phase-D recording/rendering contract the only supported schema, call it v0, and stop preserving the internal v1-v13 history. There are no public users yet, so older Parsek saves and sidecars should be refused or discarded clearly instead of migrated.

The important product decision is: compatibility is intentionally not a goal. A successful reset leaves old playtest recordings unloadable, with explicit WARN/INFO evidence and no partial recovery path.

## Working assumptions

- `RecordingStore.CurrentRecordingFormatVersion` becomes `0`.
- A separate strict schema discriminator is required because old internal records can also look like `recordingFormatVersion = 0`.
- Post-reset generation starts at `RecordingSchemaGeneration = 1`; readers require strict equality.
- Binary sidecars get new magic tags so old files fail before version interpretation.
- Text `.prec.txt` sidecar loading is removed; text emission can survive only as a debug mirror.
- Old files stay on disk when refused. The loader skips them, logs why, and keeps tables/trees coherent.
- Runtime KSP smoke tests are manual; do not close or restart KSP from the agent session.

## Initial checklist

- [x] Re-run the gate inventory from the new branch and record every `formatVersion >= N`, `RecordingFormatVersion`, `binaryVersion`, `sourceRecordingFormatVersion`, `PeerSourceFormatVersion`, tree/snapshot/pannotations/ledger version gate, and `anchorVesselId` serialization hit.
- [x] Classify each hit as one of: collapse to unconditional current behavior, delete legacy branch, rewrite as strict refusal, keep as a non-compatibility behavior constant, or negative-test fixture.
- [x] Choose the schema-compat plumbing shape before edits:
  - Chosen: promote probe results onto `Recording` transient load fields and reject at the tree/sidecar load boundaries.
- [x] Add post-reset discriminator constants and write stamping:
  - `CurrentRecordingFormatVersion = 0`.
  - `RecordingSchemaGeneration = 1` is stamped in recording metadata, trajectory sidecars, tree metadata, snapshots, and ledger data.
  - New binary magic tags replace pre-reset tags for `.prec`, pannotations/canonical pannotations, and snapshot sidecars.
- [x] Enforce that v0 tree metadata is only serialized with current sidecars:
  - save rewrites missing/stale sidecars before writing a tree node
  - save skips unsafe tree serialization if a rewrite leaves dirty/failed/non-current files
  - load drops non-synthetic recordings whose sidecar hydration fails
- [~] Add strict schema refusal at every load entry point:
  - [x] committed tree loading
  - [x] active tree restore
  - [x] trajectory sidecar probe/load
  - [x] snapshot sidecar load
  - [x] pannotations/co-bubble smoothing cache load through reset magic/version stamps
  - [~] ledger and ScenarioModule `.sfs` data that participates in recording playback
- [x] Make refusal logs explicit and searchable, with reason values such as `magic-mismatch`, `generation-missing`, `generation-older`, `generation-newer`, and `format-version-mismatch`.
- [x] Flip the actual version baseline:
  - collapse recording format constants to v0
  - delete v4-v13 feature-version constants that only gate old readers
  - delete the v4-v11 binary read/write ladder
  - delete legacy text sidecar load support
  - delete pre-v6 Relative interpretation paths from production playback
  - delete v7-v13 compatibility gates now represented by the current contract
- [ ] Remove no-longer-needed serialized compatibility fields and migration helpers:
  - `TrackSection.anchorVesselId`, if loop-anchor fallout confirms it is no longer needed
  - legacy merge-state migration counters/helpers
  - legacy group rename compatibility
  - legacy log prefix compatibility
  - `PRE_REFLY_ORIGINAL` silent-drop tolerance
- [~] Regenerate or rewrite test fixtures and generators:
  - [x] `RecordingBuilder`
  - [x] `RecordingStorageFixtures`
  - [x] `ScenarioWriter`
  - [x] synthetic/showcase recording injection outputs use v0/generation-1 for generated recordings
  - [blocked] `Source/Parsek.Tests/Fixtures/DefaultCareer/` remains pre-reset and is explicitly excluded by `InjectAllRecordings` until rebaked
  - [n/a] `VesselSnapshotBuilder` has no recording-format stamp to flip
- [~] Replace compatibility tests with v0/current-contract tests:
  - [x] rewrite `FormatVersionTests` into discriminator refusal tests
  - delete or rewrite legacy binary/text sidecar round trips
  - delete `LegacyTreeMigrationTests`
  - delete or rename `RecordingBuilderV6Tests`
  - update old Relative contract tests to current v0 semantics or remove them if their only value was migration
- [x] Add refusal tests for at least:
  - legacy binary magic (`magic-mismatch`)
  - missing generation on a default-0 legacy record (`generation-missing`)
  - future generation (`generation-newer`)
  - wrong current format value after reset (`format-version-mismatch`)
- [ ] Add v0 scenario playback coverage that checks `NonLoopLivePidGuard.LivePidLookupAttemptsForTesting == 0` once regenerated v0 watch/Re-Fly fixtures exist.
- [x] Set mod version to v0.9.2 in both `Parsek.version` and `AssemblyInfo.cs`.
- [~] Update docs in the same commit set:
  - [x] `CHANGELOG.md`
  - [x] `docs/dev/todo-and-known-bugs.md`
  - `.claude/CLAUDE.md` and `AGENTS.md` if the version/schema guidance changes
  - the relevant design plan notes if implementation deviates from this checklist
- [~] Validation gates:
  - [blocked] `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj`
    - local blocker: missing .NET Framework 4.7.2 reference assemblies (`MSB3644`)
  - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter InjectAllRecordings`
  - `scripts/grep-audit-non-loop-live-pid.ps1`
  - `scripts/grep-audit-ers-els.ps1`
  - [x] new version-literal grep gate: only v0 survives outside negative-test fixtures and the explicitly excluded `DefaultCareer` corpus
  - fresh v0 in-game smoke: Watch, active Re-Fly, map view, KSC ghost view, no `[ERROR]` lines in `KSP.log`

## First implementation pass

1. Produce the gate inventory and mark each line with its disposition.
2. Implement the discriminator plumbing with old readers still accepted.
3. Land the baseline flip and delete compatibility readers in one coherent pass.
4. Do the `.sfs` schema audit separately so scenario-state refusal can be reviewed on its own.
5. Regenerate fixtures and run the headless gates before any runtime smoke.
