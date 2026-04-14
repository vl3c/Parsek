# Phase 11.5 Recording Storage Baseline

## Why This Exists

This note is the measurement and fixture baseline for the storage-focused half of Phase 11.5.
It captures what is expensive in the current on-disk format and defines the representative
recording corpus that guards the staged optimization plan.

Related plan:
- `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`

## Current Measurements

Measured from archived recordings collected before storage work started:

- total recording sidecar payload: about `54.6 MB`
- `.prec` trajectory and event sidecars: about `23.9 MB` (`43.7%`)
- `_ghost.craft` snapshots: about `20.6 MB` (`37.7%`)
- `_vessel.craft` snapshots: about `10.2 MB` (`18.6%`)
- sectioned `.prec` files duplicate point nodes by about `2.1x` on average
- about `73.5%` of `_vessel.craft` / `_ghost.craft` pairs are byte-identical

The two dominant wastes are:

1. duplicated trajectory data inside `.prec`
2. duplicated snapshot payload across `_vessel.craft` and `_ghost.craft`

## Post-Slice-3 Rebaseline

Measured from the current follow-up storage corpus in
`logs/2026-04-12_1549_storage-followup-playtest/parsek/Recordings/` after the section-authoritative
`v1` sidecar work and ghost snapshot alias mode landed:

- total recording sidecar payload: `5,609,867` bytes (`5.35 MiB`)
- `.prec` trajectory sidecars: `1,405,497` bytes across `68` files (`25.1%`)
- `_vessel.craft` snapshots: `1,542,466` bytes across `68` files (`27.5%`)
- `_ghost.craft` snapshots: `2,661,904` bytes across `59` files (`47.5%`)
- alias or vessel-only snapshot mode is active for `9` of `68` recordings in this corpus

This confirms two things:

1. slices 2-3 removed the measured low-risk duplication and are already shrinking new saves
2. the next structural win is still `.prec` encoding rather than more snapshot plumbing

Even after the `v1` / alias changes, `.prec` remains a large enough bucket to justify the binary
format work because it still stores verbose text scalars and repeated key names for every point,
event, and section boundary.

## Post-Slice-5 Format Status

Current-format recordings now write `.prec` sidecars as binary `v3`.

- `v3` keeps the exact scalar encoding from `v2`
- `v3` adds conservative sparse defaults for repeated per-point `bodyName`, `funds`, `science`,
  and `reputation`
- the sparse path only activates when it saves bytes for that specific point list
- this slice is still lossless: no quantization, no sampler changes, and no gameplay-facing data
  drops

The regression corpus now proves two size relations:

1. binary `v2` is smaller than equivalent text `v1`
2. sparse binary `v3` is smaller than equivalent legacy binary `v2` when those fields are stable

## Live `v3` Rebaseline

Measured from the live `v3` playtest bundles:

- `logs/2026-04-12_1857_phase-11-5-storage-followup-test-career/`
- `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`

`test career`:

- total recording sidecar payload: `736,965` bytes
- `.prec`: `255,726` bytes

`s4`:

- total recording sidecar payload: `1,071,161` bytes
- `.prec`: `26,894` bytes

Combined:

- total recording sidecar payload: `1,808,126` bytes (`1.72 MiB`)
- `.prec` trajectory sidecars: `282,620` bytes (`15.6%`)
- `_vessel.craft` snapshots: `370,479` bytes (`20.5%`)
- `_ghost.craft` snapshots: `1,155,027` bytes (`63.9%`)

This is the key Phase 11.5 storage outcome:

1. trajectory sidecars stopped being the dominant storage bucket
2. snapshot-side payload is now the next clear optimization target
3. the next PR should focus on `_ghost.craft` / `_vessel.craft` size rather than more speculative
   `.prec` work

## Current Format Pressure Points

The current `version = 0` `.prec` format is plain-text `ConfigNode` data.

- every `POINT` stores verbose scalar text values
- `TrackSections` serialize nested `POINT` / `ORBIT_SEGMENT` nodes again
- active recordings keep flat `Points` plus section-local frames, which becomes on-disk duplication
- snapshot sidecars are written independently even when their contents are the same

This means the first gains should come from removing structural duplication before changing
sampling behavior.

## Slice 1 Fixture Corpus

The baseline tests now use four representative fixtures under
`Source/Parsek.Tests/Generators/RecordingStorageFixtures.cs`.

1. `Atmospheric Active Multi-Section`
   Covers duplicated flat points plus boundary-seeded section frames, flag events, part events,
   segment events, altitude metadata, and an active ghost snapshot.
2. `Orbital Checkpoint Transition`
   Covers mixed sampled ascent points plus a checkpoint-backed orbital section and a vessel
   snapshot that is expected to mirror into `_ghost.craft` in the current writer.
3. `Mixed Active And Background`
   Covers `TrackSectionSource.Background`, sparse section-level metadata, separate vessel and
   ghost snapshots, and the non-spam logging path for non-default section sources.
4. `Optimizer Boundary Seed`
   Covers branch-like split boundaries where the flat point list stays unique while adjacent
   sections intentionally share the same seeded boundary frame.

## What The Tests Assert

`Source/Parsek.Tests/RecordingStorageRoundTripTests.cs` now checks:

- representative fixtures round-trip through `RecordingStore.SerializeTrajectoryInto` /
  `DeserializeTrajectoryFrom` without semantic drift
- `TrajectoryMath.ComputeStats` stays stable across the same round-trip
- `ScenarioWriter.WriteSidecarFiles` emits the expected `.prec`, `_vessel.craft`, and
  `_ghost.craft` sidecars for the corpus
- the background-section serialization path logs one section summary, not per-frame spam

## Guidance For Follow-On Slices

- Reuse this fixture corpus before adding new ad hoc storage tests.
- When a new format version lands, extend the same corpus to emit both legacy and new sidecars.
- Keep the fixtures focused on playback-relevant data: points, orbit checkpoints, events,
  section boundaries, and snapshot presence.
- Add in-game coverage only when the change depends on real KSP save/load lifecycle rather than
  pure file transforms.
