# Keep on-rails SOI traversals cohesive, so an interplanetary transfer replays re-aimed

**Base: `dres-lane`** (not `main` — the finding, the fixture and the lane this fixes all live on that unmerged branch).

Fixes `OPTIMIZER-SPLIT-DEFEATS-REAIM-CLASSIFIER`, found by `V9-dres-player-loop` on 2026-08-12. Plan and design decision committed first, verbatim, at `docs/dev/plans/optimizer-split-transfer-cohesion.md` (Design A of five candidates).

## What was wrong

A recorded Kerbin→Dres mission replayed FAITHFUL — it flew the original trajectory to where Dres used to be, instead of re-aiming at where it is. The classifier said why:

```
[ReaimDiag] member#1 segs=8  startBody=Kerbin supported=False
[ReaimDiag] member#2 segs=11 startBody=Sun    supported=False
    reason='no heliocentric (common-ancestor) leg recorded - never warped through
            the coast, or background; staying faithful'
[ReaimDiag] gatheredSegs=19 transferMemberSegs=0 plan.Supported=False
```

The reason is misleading: the recording *does* contain the whole traverse (B19's own `Kerbin to Sun` / `Sun to Dres` SOI tokens prove it). What it does not have is parking + coast + arrival inside **one member**, because the load-time optimizer split it.

**The split is one boundary, and it fires on a label rather than an event.** Measured through the real predicate on the committed fixture bytes:

| Boundary | Sections | Reason | Outcome |
|---|---|---|---|
| 1→2 @ ut 31.0 | SurfaceMobile/Kerbin → Atmospheric/Kerbin | `SurfaceInvolved` | splittable, rejected (min-half floor) |
| 3→4 @ ut 224.5 | Atmospheric → ExoBallistic (Kerbin) | `PersistedPhaseChange` | SPLIT (ordinary ascent split) |
| **28→29 @ ut 8,490,936.2** | **ExoPropulsive(ckpt)/Kerbin → ExoBallistic(ckpt)/Sun** | **`BodyChange`** | **SPLIT — the defect** |
| 45→46 @ ut 20,376,838.0 | ExoBallistic/Sun → ExoBallistic/Dres | `SuppressedExoCoastBodyChange` | suppressed |

Rule 3 requires *raw* `ExoBallistic` on both sides. Section 28 is labelled `ExoPropulsive` — but it is an on-rails `OrbitalCheckpoint` re-emission whose single ORBIT_SEGMENT payload is **byte-identical to its ballistic predecessor's** (same Kerbin escape hyperbola, ecc 2.3601122370442775). A stock vessel cannot thrust while packed. Eve's fixture escapes the same fate only because a ~30 s ballistic stub happened to be re-emitted between its burn and its SOI flip — recorder timing noise, not gameplay.

## The change

One predicate. `ShouldKeepCohesiveCrossBodyExoCoast` gains a second way to qualify:

- **(a)** both sides raw `ExoBallistic` — the original rule, untouched
- **(b)** both sides Exo class **and** both sections `OrbitalCheckpoint`-framed — an on-rails traversal

No schema change, no migration, no reason-enum change, no log-vocabulary change. Reason stays `SuppressedExoCoastBodyChange`.

**Deliberately preserved:** a genuine *physics-frame* burn straddling an SOI crossing still splits — the calibration row "SOI traversal while burning → split". Pinned from both sides (the pre-existing `Persistence_BodyChange_ExoPropulsiveCrossing_Splits`, which did not move, plus a new `E3_PhysicsFramedExoBodyChange` cell), and a third new cell pins **mixed framing** as still-splitting so the widening cannot leak into partially-packed shapes.

## Evidence

**Phase 0 pinned the measurement before anything changed** (`Source/Parsek.Tests/OptimizerTransferCohesionTests.cs`, 11 cells), through `RecordingOptimizer` itself rather than a mirror.

- **E1 confirmed the diagnosis** — the four boundaries above, and Eve with *zero* rule-4 body splits, which is why V8 was ENGAGED and V9 was not.
- **E2 was the stop gate**: the unsplit 19-segment chain classifies `Supported=true, target=Dres, departure 8,490,936.2, tof ≈11,885,902`. Had it declined, the fix direction would have pivoted to Design C. Its counterpart cell shows both split halves declining, the head with V9's exact in-game reason string.
- **E4 measured the blast radius** rather than assuming it: the Dres handoff was the **only** rule-4 body split in the entire committed fixture inventory before the fix, and there are **none** after. That before/after pair is the proof.

**In game** (`V9-dres-player-loop`, `_0241`/`_0244`, byte-identical):

```
ReaimDiag member#1 segs=19 startBody=Kerbin supported=True target=Dres
ReaimDiag gatheredSegs=19 transferMemberSegs=19 plan.Supported=True reason=''
          departUT=8490936 arrivalUT=20376838 tof=11885902 ancestor=Sun
Reaim     ENGAGED re-aim Kerbin->Dres via Sun
          cadence=22785806.611915223  synodic=11392903.305957612
          loiterCuts=1 cutSeconds=8436248 compressedSpan=11957134/20393382
```

`cadence ÷ synodic = exactly 2.0` (the faithful unit used its own span). The ~8.4M s LKO ejection-window wait is now compressed; it never was before. Recordings 7→6, members 3→2, largest recording 763→1,320 points, points total unchanged.

The in-game departure and tof match Phase 0's headless E2 prediction **to the digit**.

## Honest notes

- **A plan detail was wrong and is corrected in the code comments**: the plan said `FindSplitCandidatesForOptimizer` "accepts two" candidates in one call. It returns one — the consumer is a `while` loop that splits one per iteration and re-scans. Mirror artifact, not a diagnosis error.
- **The end-to-end 5→7→6 count is not asserted headlessly.** `SplitAtSection` calls `UnityEngine.Quaternion.Slerp`, which throws under xUnit. V9 is the in-game truth for that number, and it is armed there.
- **Already-split saves do not self-heal.** A save persisted under the old predicate keeps its extra recording; `CanAutoMerge` will not re-merge across the boundary and the supersede-row guard should not be weakened to force it. Noted in the CHANGELOG.
- **This cures a boundary class, not the classifier's fragility.** The preserved burn-split contract means the same FAITHFUL-with-a-misleading-reason shape can still occur. Filed as `REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS` with Design C's viability recorded, plus a cheap interim (make the decline *loud* when a sibling member in the same chain group has the leg the reason claims is missing).

## Regression

`dotnet test` **20,184 passed / 0 failed / 1 skipped**. Harness: lib 1,284 · missions 1,496 · provision 237, all OK.

Flown, all PASS with no movement: **V8** Eve (count 9, gated saveParse PASS) · **V8F** · **V8T** (9) · **V6M** Mun (9 — its "+1" split mechanism intact) · **V2** Duna. H34/H35 covered mechanically by the headless count cells and the E4 sweep.

## Follow-ups filed

- `REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS` (open question 2, accepted)
- `RECORDER-LABELS-ON-RAILS-CHECKPOINTS-EXOPROPULSIVE` (open question 3 — filed, deliberately not acted on: it cannot help recorded saves, and the misled consumer is fixed)
