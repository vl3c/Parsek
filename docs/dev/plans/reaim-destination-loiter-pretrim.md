# M-MIS-2 P4 — Destination-Loiter Pre-Landing Trim (the keepRevs re-timer)

Branch: `reaim-dest-loiter-retimer` (off `origin/main`). Companion to
`docs/dev/plans/reaim-destination-arrival-alignment.md` (the parent Phase-4 plan; this is its P4).

Status: PLAN, reviewed (1 architect + 3 adversarial clean-context lenses, all load-bearing claims
verified against source 2026-06-14). Two blockers (B1/B2) are GATING and need a Phase-0 probe on a
real captured-then-park-then-deorbit recording before the builder wiring (P4.2) is validated.

---

## 1. Summary

P4 removes the fail-closed refusal at `ArrivalHoldPlanner.cs:122-127` for the
**destination-parking-loiter landing** case (a recorded captured-then-deorbit interplanetary mission
that parks in destination orbit before deorbiting). Today such a mission gets **no** arrival
alignment because an entry-referenced hold cannot align the deorbit once a destination loiter cut
excises whole periods between SOI entry and deorbit. **Mechanism:** a new pure helper jointly picks
the destination run's `keepRevs` (the EARLIER-direction knob, excising whole parking revolutions) and
the continuous arrival hold `W` (the LATER-direction knob, already shipped), anchoring the alignment
at the recorded surface/deorbit UT instead of the SOI entry, then feeds the chosen destination cut
into the existing `loiterCuts` list and the chosen `W` through the existing `ArrivalHoldResult` /
per-loop machinery — **no new `LoopUnit` field, no clock change**. When the helper does not apply, the
builder is byte-identical to PR #1030.

## 2. The joint (keepRevs, W) solve

**Anchor at the deorbit.** `RecordedDestSurfaceUT` (= D) is the recorded surface-arrival UT for the
target body.

**Source of D (settled, verified):** D is already encoded in the mission's `DestRotation` constraint.
`MissionPeriodicity.cs:444-451` sets `PhaseOffsetSeconds = rb.Value - ut0`, where `rb.Value` is the
earliest rotation-constraining surface-section start UT for the target body (from
`ScanSurfaceSegmentsWithinWindow`, `:396-398`), and `ut0 == spanStartUT` is hard-asserted within 1.0s
at `MissionLoopUnitBuilder.cs:1097`. Therefore:

```
RecordedDestSurfaceUT = spanStartUT + destRotation.PhaseOffsetSeconds
```

where `destRotation` is the `Rotation` constraint whose `BodyName == plan.TargetBody` in
`extraction.Constraints` (already in scope at `MissionLoopUnitBuilder.cs:541`). **Do NOT re-call
`ScanSurfaceSegmentsWithinWindow`** — re-scanning a separately-built window can diverge from the
constraint the rest of the pipeline agreed on.

**The two knobs.**
- **keepRevs trim (EARLIER):** the destination cut excises `(WholeRevs − r)·T_loiter` from the run
  START (preserving the recorded exit phase, `ReaimLoiterCompressor.cs:184-192`), shifting the live
  deorbit earlier by whole loiter periods.
- **Continuous hold W (LATER):** `W ∈ [0, T_rot)`, the shipped `ComputeArrivalAlignHoldSeconds`
  forward minimal-shift.

**The trial map (verified against the shipped clock).** For candidate `r`, build
`trialCuts = launchSideCuts ∪ {destCut(r)}`, then:

```
liveSurface(r) = phaseAnchorUT + (CompressSpanUT(RecordedDestSurfaceUT, trialCuts) − spanStartUT)
W(r)          = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(RecordedDestSurfaceUT, liveSurface(r), T_rot)
```

The hold is still **inserted** at `recordedArrivalUT` (SOI entry); because
`RecordedDestSurfaceUT > recordedArrivalUT`, the deorbit lies past the insertion boundary and
`ApplyArrivalHoldToPhase` defers it by the full `W`. Insertion-point ≠ alignment-anchor is consistent.

**The objective (settled).** `W(r)` is a wrapped phase (sawtooth in `r`), not a monotone wait, so a
λ-weighted `cost(r)=W(r)+λ·trim` mis-ranks and is REJECTED. Because the continuous hold reaches any
phase in `[0,T_rot)`, the trim is **never required** for in-band alignment — it only shortens the live
wait. The honest objective:

```
choose r ∈ [1, min(WholeRevs, MaxKeepRevs)]   (MaxKeepRevs = 10)
minimizing W(r);  tie-break toward LEAST trim (largest r);
default r = 1 (today's cut) when no r strictly reduces W below W(1).
```

The search band is the **low end** (`r=1` = maximal trim, up to `~10` kept), matching parent-plan §4.

**Per-loop:** the chosen `W_0 = W(r)` flows unchanged through `ComputePerLoopArrivalHoldSeconds`
(`GhostPlaybackLogic.cs:7163`). The destination cut is constant across loops (lives in one cycle), so
no per-loop change is needed.

**Reused helpers, nothing re-derived:** `DetectRuns`, `LoopCut` construction, `CompressSpanUT`,
`TotalCutLength`, `ComputeArrivalAlignHoldSeconds`, `ComputePerLoopArrivalHoldSeconds`,
`ScheduleToleranceSecondsFor`.

## 3. Files touched + exact signature/struct changes

**New `Source/Parsek/Reaim/DestinationLoiterTrim.cs`** (pure static):

```csharp
internal struct DestinationLoiterTrimResult
{
    public bool   Applied;               // false => caller takes the existing ComputeArrivalHold path
    public int    DestinationKeepRevs;   // chosen keepRevs (>=1)
    public GhostPlaybackLogic.LoopCut DestinationCut; // the cut at chosen keepRevs
    public bool   HasDestinationCut;     // false when keepRevs == WholeRevs (no excision)
    public double HoldSeconds;           // W_0 (anchored at the deorbit)
    public double HoldAtUT;              // recordedArrivalUT (insertion boundary)
    public double AlignPeriodSeconds;    // T_rot
    public static DestinationLoiterTrimResult None => /* Applied=false, keepRevs=1, NaNs */;
}

internal static DestinationLoiterTrimResult SolveTrimAndHold(
    IReadOnlyList<ReaimLoiterCompressor.LoiterRun> allRuns,
    IReadOnlyList<GhostPlaybackLogic.LoopCut>      launchSideCuts,
    DestinationConstraintExtractor.DestinationConstraintSet destSet, // gating reuse
    PhaseConstraint destRotation,        // for D + ScheduleToleranceSecondsFor
    string  launchBodyName,              // for ScheduleToleranceSecondsFor
    string  targetBody,
    double  recordedArrivalUT,
    double  recordedDestSurfaceUT,       // = spanStartUT + destRotation.PhaseOffsetSeconds
    double  rotationPeriod,              // T_rot
    double  phaseAnchorUT,
    double  spanStartUT,
    double  spanSeconds,                 // for the TotalCutLength < span guard per candidate
    TransitedBodyRotationMode mode,
    int     maxKeepRevs,                 // = 10
    IBodyInfo bodyInfo);
```

`SolveTrimAndHold` receives `destRotation` (the `PhaseConstraint`) and `launchBodyName` so it can call
`MissionPeriodicity.ScheduleToleranceSecondsFor(c, bodyInfo, launchBodyName, mode)`.
`WithinTolerance`/`ResidualSeconds`/`AmberReason` are **removed from the result struct** (under the
continuous hold the rotation residual is always ~0, so these are vestigial and risk double-surfacing
the amber against `LogArrivalAmberTransition`). The amber stays owned solely by the `None`-fallback
`ComputeArrivalHold` path.

**Private selector** `TrySelectDestinationRun(allRuns, targetBody, recordedArrivalUT,
recordedDestSurfaceUT, out LoiterRun destRun)`: among `targetBody` runs with
`EndUT > recordedArrivalUT`, pick the run whose `EndUT` is closest to (≤, within epsilon of)
`recordedDestSurfaceUT` — the loiter immediately preceding the deorbit. NOT "bounding D inside
`[StartUT,EndUT]`" (the parking run ends at the deorbit, so D sits at/just past `EndUT`).

**Builder (`MissionLoopUnitBuilder.cs:450-476` and `:540-557`):**
- Compute `runs = ReaimLoiterCompressor.DetectRuns(transferSegments, bodyInfo.GravParameter)` **once**.
- **Hard construction rule (forbid the "full ComputeCuts then drop" path):** partition `runs` by
  **`run.EndUT <= recordedArrivalUT`** (launch-side) vs `run.EndUT > recordedArrivalUT`
  (destination-side). Build `launchSideCuts` from launch-side runs at `keepRevs=1` (byte-identical to
  today for those runs). Build the single destination cut from the selected destination run at the
  chosen `keepRevs`. **Never** derive launch-side cuts by filtering a full `ComputeCuts` on
  `LoopCut.EndUT` — a long launch-side run whose cut length overshoots `recordedArrivalUT` would be
  mis-binned and dropped, and an independently-emitted destination cut would then double-excise in
  `CompressSpanUT` (additive, no dedup). Partition on the **run**, not the cut.
- If `SolveTrimAndHold(...).Applied`: `loiterCuts = launchSideCuts ∪ {DestinationCut}` (sorted by
  `StartUT`, assert no two cuts share a `StartUT`); construct
  `arrivalHold = new ArrivalHoldResult{ Applied=true, HoldSeconds, HoldAtUT, AlignPeriodSeconds,
  IsStationHold=false, AlignAnchorPid=0, AmberReason=null }`.
- Else: today's path verbatim — `ComputeCuts(transferSegments, …)` (full) + `ComputeArrivalHold(...)`.
- `cutBeforeDeparture` (`:472-474`) may pass the full final `loiterCuts`: the destination cut starts
  after `recordedArrivalUT > RecordedDepartureUT`, so it contributes exactly zero to
  `CompressSpanUT(RecordedDepartureUT, …)`.
- **`TotalCutLength(trialCuts) < spanSeconds` is re-checked per candidate `r` inside
  `SolveTrimAndHold`**: a larger keepRevs only shortens the destination cut, so any candidate passing
  is monotonically safer than keepRevs=1; the helper rejects any `r` that fails and falls back toward
  keepRevs=1.

**No change to `ArrivalHoldResult`, `LoopUnit`, `GhostPlaybackLogic.cs`, `ReaimLoiterCompressor.cs`,
`DestinationArrivalSolver.cs` (stays UNWIRED), `MissionPeriodicity.cs`.**

## 4. Byte-identical-off / fail-closed gate enumeration

Master invariant: **`SolveTrimAndHold.Applied == false` ⇒ the builder is byte-identical to PR #1030.**

| Branch | How preserved |
|---|---|
| **No destination loiter** (Duna One; selector finds no run with `EndUT > recordedArrivalUT`) | `None` → existing `ComputeCuts` + `ComputeArrivalHold` verbatim. PR #1030 byte-identical. |
| **Destination loiter in a different member** (chain mission; `transferSegments` lacks it — BLOCKER B1) | `None` → verbatim path. Fail-closed-to-faithful, NOT a crash. |
| **Drop mode** | `None` immediately on `mode == Drop`. Verbatim path → destination cut stays keepRevs=1 via full `ComputeCuts`, `ComputeArrivalHold` refuses, `W=0`, clock byte-identical. Invariant: **Drop ⇒ no trim, no hold, identical to today.** |
| **Station hold (M4c Tier 2)** | `None` on `destSet.HasStation`. Verbatim `ComputeArrivalHold` handles the station path. |
| **Orbit-only, no station** (`!HasLandingRotation`) | `None`. `ComputeArrivalHold` returns `None`. |
| **Unsupported destination** (2+ moons, D8 duals) | `None` on `!destSet.Supported`. `ComputeArrivalHold` keeps its amber path. |
| **Degenerate** (`T_rot`/`T_loiter` NaN/≤0, `WholeRevs ≤ 1`) | `None`. |
| **Trial cut ≥ span** | Per-candidate guard inside the helper; falls back toward keepRevs=1. |
| **Same-parent / orbit-only / no-loiter** | Never enters the `if (!phaseLocked)` re-aim block; helper never called. |

## 5. Sub-step phasing (smallest reviewable commits; riskiest flagged)

- **P4.0 — Phase-0 probe (no code; GATING).** On a real captured-then-deorbit recording, read the
  `ReaimDiag` Verbose dump (`MissionLoopUnitBuilder.cs:387-413`) and confirm: (a) the destination
  parking loiter is in the classified `transferSegments` member, not a separate chain member
  (BLOCKER B1); (b) `DetectRuns` yields exactly one post-`recordedArrivalUT` destination run, and the
  multi-run tie-break; (c) `destRotation.PhaseOffsetSeconds` resolves a non-degenerate D for the
  target body (BLOCKER B2).
- **P4.1 — `DestinationLoiterTrim` pure helper + selector + tests (cases 1–9).** Math core, no wiring.
- **P4.2 — Builder wiring** (run partition by `run.EndUT`, branch the hold source, assemble final
  cuts) **+ gate/regression tests (cases 10–14). RISKIEST.**
- **P4.3 — Self-review + clean-context review** focused on the §4 gate table and the run-partition
  double-cut rule.

**Handoff:** P5 = render-thread canary (both map render paths inherit the trimmed cut + hold via the
shared clock). P6 = tooltip widening for the destination landing alignment mode.

## 6. Test list

**Pure xUnit `DestinationLoiterTrimTests.cs` (`IBodyInfo` fake + synthetic `LoiterRun`/`OrbitSegment`):**
1. Joint solve picks the `r` minimizing `W` for a long-low destination loiter (100 revs, `T_loiter ≪ T_rot`).
2. Short/absent loiter (1–2 rev or none) → keepRevs=1, hold carries the alignment; no spurious trim.
3. Destination cut excises from the run START, ends at the recorded run end:
   `DestinationCut.StartUT == run.StartUT`, `LengthSeconds == (WholeRevs − r)·T_loiter`.
4. Selector picks the **destination** run (post-`recordedArrivalUT`, `EndUT` nearest D), excluding a
   same-`targetBody` launch-side run ending before `recordedArrivalUT`.
5. Multi-run destination (capture-park + deorbit-prep-park) → selector picks the loiter immediately
   preceding D.
6. Drop mode → `None`, keepRevs stays 1, no hold.
7. `WholeRevs ≤ 1` and degenerate `T_rot`/`T_loiter` → `None`.
8. **Per-loop composition WITH the destination cut present:** drive `TryComputeSpanLoopUT` with
   `loiterCuts = launchSide ∪ {destCut}` and the chosen `W_0`; assert `loopUT == RecordedDestSurfaceUT`
   at the live UT for several N.
9. Trial-cut-exceeds-span guard → falls back toward keepRevs=1.

**Builder gate / source-text (`MissionLoopUnitBuilderTests.cs`, `ChainSaveLoadTests` pattern):**
10. Source-text gate: the builder calls `DestinationLoiterTrim.SolveTrimAndHold` after `PadAlignLaunch`
    and threads its cut into `loiterCuts`.
11. **Off byte-identical:** a no-destination-loiter (Duna One synthetic) mission produces the same
    `loiterCuts` + hold as the pre-P4 builder (regression fence). *(Requires a re-aim fixture; B3.)*
12. `cutBeforeDeparture` phase-anchor shift unaffected by the destination cut.
13. Straddle case: a launch-side run whose keepRevs=1 cut length overshoots `recordedArrivalUT` is
    still classified launch-side and kept.
14. No two cuts in the assembled `loiterCuts` share a `StartUT` (double-cut fence).

**Log assertions (`ParsekLog.TestSinkForTesting`, tag `Reaim`):**
15. `SolveTrimAndHold` emits one summary line: `dest-trim dest=… keepRevs=R/WholeRevs cutLen=… W0=… mode=…`.
16. The builder's existing `ARRIVAL HOLD` line (`:546-556`) is extended with `keepRevs=R/WholeRevs`.

## 7. BLOCKERS (must resolve before P4.2 coding)

**B1 — Does `transferSegments` actually contain the destination parking loiter? (GATING.)**
`transferSegments` is one classified member's `OrbitSegments` (`MissionLoopUnitBuilder.cs:372-378`).
The classifier accepts on launch-parking + heliocentric leg + first-target-body arrival leg
(`ReaimClassifier.cs:108-122`); it does **not** require the destination parking loiter to be in that
member, and interplanetary missions are "usually a CHAIN (launch leg / transfer leg / arrival leg /
debris as separate recordings)" (`:340-346`). If the destination parking is a different member's
recording, `DetectRuns(transferSegments)` won't see it and P4 silently no-ops on exactly the mission
it targets. **Resolution:** P4.0 probe on a real captured-then-park-then-deorbit recording. If in the
transfer member, scope P4 to that case. If in a separate member, extend run-gathering across all
member recordings — but do NOT build that until the probe proves it is needed. Fail-closed (`None`)
keeps it safe until then.

**B2 — Does `destRotation.PhaseOffsetSeconds` resolve a non-degenerate D for a vacuum-body deorbit?**
D depends on the target emitting a `DestRotation` constraint, which requires both a
rotation-constraining surface/`Approach` section AND an inertial-orbit segment of the target in the
included set (`MissionPeriodicity.cs:429-451`; `IsRotationConstrainingEnvironment` counts
`Atmospheric/SurfaceMobile/SurfaceStationary/Approach`). **Resolution:** P4.0 confirms `destRotation`
exists for the target and `spanStartUT + PhaseOffsetSeconds` lands inside the recorded in-SOI window.
If no `DestRotation` is emitted, `destSet.HasLandingRotation == false` and the helper returns `None`
(orbit-only path) — safe, but P4 then does nothing, which must be understood before claiming the
feature works.

**B3 — No existing builder-level re-aim test to anchor the byte-identical fence.**
`MissionLoopUnitBuilderTests.cs` has zero `keepRevs`/`ArrivalHold`/`loiterCuts` coverage. Cases
11/13/14 need a re-aim-classifiable synthetic mission. **Resolution:** add an explicit "build the
re-aim builder fixture" task at the head of P4.2, reusing `ReaimClassifier`/`ReaimLoiterCompressor`
test builders if they exist; budget it as real work. Fall back to the source-text gate (case 10) +
pure-helper `None`-path coverage if the fixture is too heavy, and document the gap.

## 8. OPEN PRODUCT DECISIONS (recommended defaults adopted unless overridden)

1. **Automatic vs a "trim repeated orbits" toggle.** **Default: automatic** (matches the
   fully-automatic launch-side keepRevs=1 and the no-toggle station-hold precedent at
   `ArrivalHoldPlanner.cs:96`); defer any UI to P6.
2. **keepRevs upper bound + loiter-EXTENSION.** Band `[1, min(WholeRevs, 10)]` (trim-only). **Default:
   trim-only (`keepRevs ≤ WholeRevs`) for P4**, extension deferred — the continuous hold already
   covers the LATER direction, so extension buys nothing for alignment.
3. **Destination-run selector among same-body runs.** **Default:** post-`recordedArrivalUT` run whose
   `EndUT` is nearest D (the loiter immediately before deorbit), pending P4.0 confirmation of whether
   capture-then-park records as one detected run or two (`sameOrbitRelThreshold` merge at
   `ReaimLoiterCompressor.cs:118-124`).

---

## Appendix — conflict resolutions taken during plan review

- **D-source:** reuse `destRotation.PhaseOffsetSeconds` (verified `ut0 == spanStartUT`, hard-asserted
  `:1097`) instead of a re-scan; deletes the `MissionPeriodicity` touch.
- **Objective:** minimize `W(r)`, keepRevs=1 default; the λ-weighted `cost(r)=W(r)+λ·trim` is rejected
  (`W(r)` is a wrapped sawtooth, a λ-sum mis-ranks).
- **Partition:** partition `runs` on `run.EndUT`, forbid the "full ComputeCuts then drop" path (the
  only construction that cannot double-cut or mis-bin a straddling run).
- **Result struct:** drop `WithinTolerance`/`AmberReason` (vestigial under the continuous hold; amber
  stays owned by the `None`-fallback path).
- **Tolerance signature:** add `destRotation` + `launchBodyName` so the helper can call
  `ScheduleToleranceSecondsFor`.
- **Band direction:** pinned to the low end `[1, min(WholeRevs,10)]`.
