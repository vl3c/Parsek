# Phase 11.5 Recording Storage Baseline

## Why This Exists

This note is the measurement and fixture baseline for the storage-focused half of Phase 11.5.
It captures what is expensive in the current on-disk format and defines the representative
recording corpus that guards the staged optimization plan.

Related plan:
- `docs/dev/plans/phase-11-5-recording-storage-optimization.md`

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
