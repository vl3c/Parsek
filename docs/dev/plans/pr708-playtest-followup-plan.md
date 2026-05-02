# PR 708 playtest follow-up plan

**Status:** draft for the next playtest log bundle.
**Branch:** `ghost-anchor-recording-chain-v11`.
**Scope:** remaining issues after Phases A-C of `ghost-anchor-recording-chain-plan.md`.
**Non-goals:** no legacy v7-v10 migration, no live-PID fallback for non-loop Relative playback, no Phase D behaviour deletion until the D.0 product gate is explicit.

---

## 1. First step after the next playtest

Collect a fresh bundle immediately after the test session:

```powershell
python scripts/collect-logs.py pr708-ghost-anchor-retest
```

Then inspect only evidence from that bundle before changing code. The first pass should answer these questions:

1. Did KSP load the intended PR 708 DLL?
   - Confirm the bundle git state points at `ghost-anchor-recording-chain-v11` head `20f43baa` or later.
   - Confirm the run contains v11 recording logs and new DAG-fence logs if candidates are skipped.

2. Did same-tree anchor cycles disappear?
   - Grep for `anchor-cycle-detected`.
   - Grep for `recording-anchor-dag-order-skip` and `bg-recording-anchor-dag-order-skip`.
   - Audit fresh `.prec.txt` sidecars for `Relative` sections and their `anchorRecordingId` values.

3. Are debris anchors semantically correct?
   - For each separation, list parent recording id, child/debris recording ids, `TreeOrder`, branch point UT, and each Relative section's `anchorRecordingId`.
   - Debris should prefer the parent or ancestor recording. It should not choose sibling debris merely because that sibling is closer.

4. Are the bad first frames from resolver failure or recorder boundary data?
   - Compare `[PlaybackTrace]` around each structural-event UT with `SeedRelativeBoundaryPoint` / `SeedBackgroundRelativeBoundaryPoint` logs.
   - Flag one-point sections, zero-point sections, and `seed-liveRootDist` spikes.

5. Is active Re-Fly inaccuracy still caused by display translation?
   - Grep for `TryGetReFlyTreeAnchorOffset`, Re-Fly tree offset logs, and Relative playback logs using `source=recorded`.
   - Compare live active vessel position, hidden recorded active pose, and visible sibling ghost pose over the same UT window.

6. Did terminal map/spawn fail again?
   - Grep `GhostMap`, `left-orbit-segments`, `PlaybackCompleted`, `deferred spawn`, `SpawnAtPosition`, `spawn-death`, `pressure`, and `ORBITING`.
   - Record the propagated altitude, body atmosphere depth, pressure if logged, terminal orbit periapsis, and current UT for every spawn attempt.

---

## 2. Fix track A - anchor selection semantics

The current pushed follow-up prevents same-tree cycles by requiring a same-tree anchor candidate to have an older `TreeOrder`. That is necessary but not sufficient. It can still let later debris choose earlier sibling debris.

Target rule:

1. Same-tree ancestor or parent anchors outrank same-tree sibling anchors.
2. Same replay point / same vessel lineage outranks generic nearby candidates.
3. Sealed recordings outrank mutable recordings.
4. Distance breaks ties only after stability and lineage.
5. If the only close candidates are sibling debris and the parent/ancestor is available, choose the parent/ancestor.
6. If no parent/ancestor is resolvable, prefer Absolute over a sibling anchor unless logs prove sibling anchoring is needed for a specific valid case.

Implementation shape:

- Extend `RecordingAnchorCandidate` or its rank key with an `AnchorLineageAffinity` enum:
  - `DirectParent`
  - `Ancestor`
  - `SameReplayPointOrSameVesselLineage`
  - `SameTreeSibling`
  - `OtherSameScope`
- Compute this from `RecordingTree` branch data, `ParentRecordingId` / continuation lineage fields, and `TreeOrder`.
- Apply the same rank in `FlightRecorder` and `BackgroundRecorder`.
- Keep the current DAG-order eligibility as a hard safety fence.
- Log the chosen rank and skipped higher-risk sibling candidates in one batch summary per selection pass.

Tests:

- Parent beats closer sibling.
- Ancestor beats closer sibling.
- Sibling is rejected or loses when parent is available.
- Different-tree candidate remains out of scope for v1.
- Deterministic tie break remains recording id / source / index.

Runtime gate:

- Fresh separation recording sidecars show debris Relative sections anchored to the parent/ancestor, or Absolute if no safe parent exists.
- No `anchor-cycle-detected` during watch playback.

---

## 3. Fix track B - separation boundary seeding and one-frame sections

The prior logs showed two jarring data shapes independent of cycles:

- Fixed narrow case: `logs/2026-05-02_1132_pr708-refly-long-init-behind/` showed a controlled child seed `1118.66m` behind the live root at separation. Controlled child recordings now replace that stale coalescer seed with a one-time live root-part seed when the direct seed-to-root distance or propagated residual exceeds the 50 m gate. This does not add ongoing relative-to-live-vessel playback.
- Remaining generic case: other child/debris boundary seeds can still be wrong if focus and anchor poses are sampled at different UTs.
- A parent absolute section with only one point for about half a second, causing visible freeze while the child moves quickly.

Target rule:

1. Boundary seeding must compare anchor and focus poses at the same UT.
2. Relative section entry must create a usable boundary pair, not a one-point section that freezes or hides.
3. A section with zero points is a bug unless it is an explicitly documented metadata-only seam.
4. If we cannot seed a Relative boundary at the same UT, force Absolute with a clear log reason.

Implementation shape:

- Audit `SeedRelativeBoundaryPoint`, `SeedBackgroundRelativeBoundaryPoint`, and section close/open order around structural events.
- Make the seed helper take an explicit `sampleUT` and resolve both focus and anchor pose at exactly that UT.
- Add a section finalization guard:
  - `Relative` sections with fewer than two usable points either merge into adjacent compatible sections or force a clean Absolute transition.
  - Zero-point sections are dropped with a Warn that includes recording id, section index, previous section, next section, startUT, and endUT.
- Add a `[PlaybackTrace]` diagnostic line or section-close log for short sections under one second.

Tests:

- Seed uses same UT for focus and anchor.
- Failed seed exits Relative to Absolute with the expected log.
- Zero-point section cleanup does not corrupt neighboring sections.
- One-point Relative section either gains a valid boundary point or is fenced to Absolute.

Runtime gate:

- In the separation window, `[PlaybackTrace] dM` stays continuous and no frame shows a kilometer-scale jump.
- Logs do not contain zero-point section warnings except in explicitly accepted seam cases.

---

## 4. Fix track C - active Re-Fly ghost alignment

The current architecture has two competing behaviours:

- Phases A-C place non-loop Relative ghosts from recorded anchor chains.
- The old active Re-Fly tree lock still applies a per-frame display translation:

```text
displayOffset(t) = liveActiveAnchorWorld(now) - recordedActiveWorld(playbackUT)
```

The 2026-05-02 narrow follow-up stores the selected slot root-part PID on the Re-Fly marker and resolves `liveActiveAnchorWorld` from that live part when available, falling back to the previous vessel-world position only for legacy or unresolved markers. This fixes the visible COM-initialization offset without changing the recorded-side math or removing the working Re-Fly tree translation.

If later logs show the ghost still moves up/down or oscillates after root-part anchoring, the remaining product choices are:

1. Finish Phase D as originally planned.
   - Remove the Re-Fly tree translation.
   - Ghosts play at original recorded coordinates.
   - The live Re-Fly vessel can visibly diverge from the ghosts.

2. Keep a Re-Fly comparison overlay, but stabilize it.
   - Keep recorded-chain playback as the source of truth.
   - Apply a display-only alignment transform to the active Re-Fly tree.
   - Do not use live vessel PIDs as Relative anchors.

If we choose option 2, replace the raw offset with a stable alignment model:

- Compute live-vs-recorded delta in a body-local trajectory frame, not raw world XYZ.
- Use a sliding window over the active vessel, for example 5-15 seconds of samples.
- Fit a low-frequency translation or time-shift instead of using the instantaneous frame delta.
- Decompose delta into along-track, radial, and normal components.
- Low-pass or clamp radial/normal components so orbital/body rotation does not become visible up/down ghost motion.
- Apply the same smooth display transform to every ghost in the active Re-Fly tree for that frame.
- Reset the filter on scene change, active marker change, body change, large UT jump, or structural event.

Tests:

- Pure math tests for the alignment filter:
  - constant offset passes through,
  - sinusoidal vertical noise is attenuated,
  - large discontinuity resets instead of smearing,
  - body change resets,
  - output is deterministic for the same sample window.
- Playback contract test that Relative resolver output is unchanged by the display alignment layer.

Runtime gate:

- Re-Fly a separated vessel while the sibling ghost is visible.
- The sibling ghost should not bob vertically from the old hidden trajectory.
- Logs must identify whether the session is using `recorded-coordinate` mode or `stabilized-refly-display-offset` mode.

Decision needed before implementation:

- Do we want Phase D recorded-coordinate behaviour now, or do we want the stabilized comparison overlay first?

---

## 5. Fix track D - terminal map/end-spawn safety

The prior logs showed map presence ending, then real-vessel spawn attempts that KSP destroyed on rails because propagated terminal orbit positions were inside unsafe atmosphere.

Target rule:

1. Never spawn an ORBITING/on-rails real vessel at an unsafe atmospheric altitude.
2. Do not enter a repeated spawn-death retry loop.
3. Do not remove the map/held representation before real spawn succeeds.
4. If a terminal orbit cannot be safely materialized at the current UT, defer with a clear reason.

Implementation shape:

- Add a pure spawn-safety decision helper:

```csharp
TerminalSpawnSafetyDecision DecideTerminalOrbitSpawnSafety(
    CelestialBodyInfo body,
    OrbitSnapshot terminalOrbit,
    double currentUT,
    double propagatedAltitude,
    double periapsisAltitude);
```

- Decisions:
  - `SpawnNow`
  - `DeferUntilSafe`
  - `CannotSpawnSafely`
- Inputs must be easy to unit-test without live KSP.
- Runtime path may add pressure and situation details to logs.
- If current propagated altitude is below `body.atmosphereDepth + safetyMargin`, defer instead of spawning.
- If periapsis is inside atmosphere, either find a safe future true-anomaly/UT or keep the map representation and log `CannotSpawnSafely`.
- Keep or restore `GhostMapPresence` while spawn is pending/deferred.

Tests:

- Safe propagated orbit spawns normally.
- Unsafe atmospheric propagated orbit defers before any real spawn call.
- Repeated unsafe updates do not spawn repeatedly.
- Map presence is retained while terminal spawn is pending.
- Unsafe periapsis produces a clear cannot/defer decision.

Runtime gate:

- A terminal orbiting recording remains visible on the map until real spawn succeeds.
- KSP.log has no immediate on-rails destruction after spawn.
- Logs include terminal spawn safety decision, altitude, atmosphere depth, periapsis, UT, and recording id.

---

## 6. Work order after new logs

Do not start by landing Phase D. Use the logs to pick the smallest next fix.

1. If `anchor-cycle-detected` still appears:
   - Fix track A first.
   - Do not chase Re-Fly alignment until cycles are gone.

2. If cycles are gone but first frames jump near separation:
   - Fix track B first.
   - This is recorder data shape, not playback resolver policy.

3. If watch playback is stable but active Re-Fly remains inaccurate:
   - Decide track C product behaviour.
   - Either land Phase D recorded-coordinate mode, or implement the stabilized display offset.

4. If map/end spawn fails again:
   - Fix track D independently.
   - It does not depend on the Relative resolver work.

5. If all four issues appear:
   - Land A and B together only if tests are tightly scoped and the diff stays small.
   - Land D separately.
   - Keep C separate because it is a product-behaviour choice.

---

## 7. Validation checklist

Headless:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~AnchorDetectorTests|FullyQualifiedName~RelativeAnchorResolverTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
```

Runtime:

- New recording, fresh KSP restart after build.
- Watch separation playback with `[PlaybackTrace]`.
- Re-Fly lower stage with upper-stage/debris ghost visible.
- Re-Fly upper stage with lower-stage/debris ghost visible.
- Map/KSC transition at terminal orbit playback completion.
- Tracking Station visibility before and after real spawn handoff.

Log gates:

- No non-loop live-PID Relative fallback.
- No `anchor-cycle-detected` for fresh v11 recordings.
- No missing `anchorRecordingId` on new v11 Relative sections.
- No zero-point section unless explicitly logged as a dropped seam.
- No on-rails atmospheric spawn death loop.
