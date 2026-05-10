# Research: Meaningful-action gate on `RecordingOptimizer` env-class splits

> **Conclusion reverted (post-implementation).** The PartEvent-window gate sketched here was implemented in PR #625 and merged, then reverted. Reason: the gate suppressed pure Atmo↔Exo and Approach↔Exo crossings without nearby PartEvents, which broke the per-phase loop split for two extremely common gameplay phases (passive deorbit reentries; staged ascents that coast through 70 km between first-stage shutdown and circularization burn). The architectural premise — that env-class boundaries are "geometric noise that needs gameplay-event corroboration" — inverts the design intent of `SplitEnvironmentClass` (see `parsek-flight-recorder-design.md` §9A.5: per-phase chain segments exist *to enable individual loop toggles*, so the boundary IS the gameplay split from the player's perspective).
>
> The "S5 one-way vs oscillating" discriminator dropped in §5 was the right idea and was dropped for the wrong reason: the optimizer scans the whole recording's `TrackSections` list before any split happens, so the "composing across already-split recordings" concern doesn't apply at scan time. A persistence-based discriminator (suppress an Atmo↔Exo* boundary iff the vessel returns to the previous env class within ~60-120 s) handles real ascents/reentries (no return → split) and grazing passes (returns within seconds → no split) without needing to gate on PartEvents at all.
>
> The Producer C no-payload boundary seam (`BackgroundRecorder.FlushLoadedStateForOnRailsTransition`) needs a separate signal at the producer (e.g. an `IsBoundarySeam` flag on the emitted `TrackSection`), not a generic post-hoc gate.
>
> Kept as historical record of what was considered and shipped on this attempt. The persistence-based redesign described in `docs/dev/plans/optimizer-persistence-split.md` superseded this note's PartEvent-window approach and shipped (Phase 1: data model + binary v8 bump; Phase 2: predicate; Phase 3: integration tests + closeout). See `docs/dev/todo-and-known-bugs.md` #632 for the closeout summary of what changed and the player-visible effects.
>
> **2026-05-10 update:** #547 narrowed the later body-change rule: coasting ExoBallistic SOI transitions now stay one cohesive recording and use multi-body display labels; ExoPropulsive SOI boundaries, non-Exo body changes, and environment-class changes still split.

---

Status: investigation only — no code change in this PR. Implementation would follow as a separate plan PR.

Companion to `extending-rewind-to-stable-leaves.md` §2.1 / §S16. That note flagged the eccentric-orbit chain-explosion symptom; this note is the broader principled redesign.

---

## 1. TL;DR

`RecordingOptimizer.FindSplitCandidatesForOptimizer` splits a committed recording at every TrackSection boundary where `SplitEnvironmentClass(env)` differs, plus body changes except coasting ExoBallistic SOI transitions (#251/#547). ExoBallistic coast body changes were later narrowed by #547 so transfer coasts stay cohesive. The class boundary is purely geometric: it fires whenever a vessel crosses the 70 km atmosphere line, the airless-body approach line, etc. — regardless of whether anything *actually happened* at the crossing.

The original proposal: keep the body-change rule (#251) untouched, keep splits at boundaries that involve `Surface*` or `Approach`, but require a small **meaningful-action signal** for the otherwise-noisy `Atmospheric ↔ Exo*` pair. The signal is an existing `PartEvent` (thrust, decoupling, parachute, gear, thermal animation, etc.) within a window around the crossing UT. Pure geometric crossings — periapsis grazing on a stable orbit, on-rails passes — produce no nearby PartEvents and therefore no split.

Backwards compatibility is essentially free: the merge gate (`CanAutoMerge`) requires equal `SegmentPhase`, and existing already-split chain segments carry distinct phases ("atmo" vs "exo"), so a stricter split rule cannot retroactively re-merge legacy chains. The change applies forward-only to recordings that have not yet been optimizer-split.

---

## 2. Current rule (recap)

`RecordingOptimizer.cs:178-230` — `FindSplitCandidates` and `FindSplitCandidatesForOptimizer` are identical except for the ghost-trigger gate. Both walk consecutive TrackSection pairs and emit `(recIdx, sectionIdx)` when:

```
envChanged  := SplitEnvironmentClass(prev.environment) != SplitEnvironmentClass(next.environment)
bodyChanged := SectionBodyChanged(prev, next)        // #251

if (envChanged || bodyChanged) && CanAutoSplitIgnoringGhostTriggers(rec, s)
    candidates.Add(...)
```

`SplitEnvironmentClass` ([RecordingOptimizer.cs:850-862](../../../Source/Parsek/RecordingOptimizer.cs)):

| `SegmentEnvironment` | class |
| --- | --- |
| `Atmospheric` | 0 |
| `ExoPropulsive`, `ExoBallistic` | 1 |
| `SurfaceMobile`, `SurfaceStationary` | 2 |
| `Approach` | 3 |

The intent (per `parsek-flight-recorder-design.md` §9A.5) is to break a multi-environment monolithic recording into per-phase chain segments so each phase gets its own loop toggle in the recordings table. That intent is sound for *real* phase changes (ascent, reentry, landing). It is wrong for *geometric* crossings that happen because the trajectory's shape happens to intersect the threshold.

---

## 3. Where env-class transitions actually come from

Two recorder pathways write env-tagged TrackSections:

**A. `FlightRecorder.UpdateEnvironmentTracking` ([FlightRecorder.cs:5640](../../../Source/Parsek/FlightRecorder.cs))** — runs every physics frame for the focused vessel. `EnvironmentDetector.Classify` is fed through `EnvironmentHysteresis` (debounce). On a confirmed transition, it closes the current section and opens a new one tagged with `TrackSectionSource.Active`.

**B. `BackgroundRecorder.OnBackgroundPhysicsFrame` ([BackgroundRecorder.cs:1450-1476](../../../Source/Parsek/BackgroundRecorder.cs))** — runs for every loaded-but-not-active vessel in the physics bubble (`bgVessel.loaded && !bgVessel.packed`). Same hysteresis pattern; sections tagged `TrackSectionSource.Background`.

**C. Loaded → on-rails transition with no-payload boundary section.** When a background-loaded vessel transitions to on-rails AND the env class differs across the boundary AND the new on-rails state will not produce a playable payload, `FlushLoadedStateForOnRailsTransition` ([BackgroundRecorder.cs:2985-3018](../../../Source/Parsek/BackgroundRecorder.cs)) calls `ShouldPersistNoPayloadOnRailsBoundaryTrackSection` ([BackgroundRecorder.cs:2352-2362](../../../Source/Parsek/BackgroundRecorder.cs)) and emits a single-frame TrackSection at the boundary. That section is `source = Background`, `referenceFrame = Absolute`, env = the *next* env on the on-rails side. The previous Background/Absolute section ends with the *previous* env. Net effect: two adjacent Background/Absolute sections with differing env class and *no PartEvent at the seam*. **This is the one path the meaningful-action gate has to handle deliberately** — the source/reference-frame backstop (S2/S4 below) does not catch it because both sides are Background/Absolute, not Checkpoint or OrbitalCheckpoint, and there is no nearby thrust/decouple/parachute event because the seam is created at the *transition*, not at any in-flight action.

Two pathways do **not** open env-tagged TrackSections:

**D. Background on-rails (`BackgroundOnRailsState`)** — for packed/unloaded background vessels, the recorder accumulates `OrbitSegment` entries on the recording's flat `OrbitSegments` list. No `TrackSection`s are opened by this path. `UpdateOnRails` ([BackgroundRecorder.cs:1348](../../../Source/Parsek/BackgroundRecorder.cs)) only refreshes `ExplicitEndUT`. So a probe that is *purely* on-rails for hundreds of orbits produces zero env-tagged sections, regardless of how many times its trajectory crosses the atmosphere line.

**E. Active-vessel pack transition** — when a focused vessel goes on rails, `FlightRecorder` opens a single `OrbitalCheckpoint` TrackSection ([FlightRecorder.cs:6268-6269](../../../Source/Parsek/FlightRecorder.cs)) and stamps it with the env classified at the moment of packing. While packed, no further env transitions are written. The whole on-rails coast is one section.

**Conclusion.** The S16 "100 orbit chain explosion" cannot fire from a *purely* on-rails recording in the current code — there is no producer of multiple env-tagged sections. It can fire only when the vessel transits the loaded physics bubble across multiple periapsis passes (e.g. another mission flying nearby, or the player briefly switching to it). The atmo-grazing concern is therefore narrower than §2.1 framed it, but the *principle* (geometric crossings ≠ gameplay events) still applies, and there is a real cluster of focused-flight scenarios where it matters:

- A spent stage in an eccentric atmo-grazing orbit that the player keeps focus on for a few periapsis passes (waiting to deorbit).
- An aerobraking pass where the player pre-aligns the vessel and then lets atmo decelerate without thrust — the *single* atmo dip is meaningful, but if the entry/exit sit close to 70 km and hysteresis flips twice, two splits are created.
- A debris piece followed across the bubble while it tumbles through a glancing reentry that bounces back to space.

These are not common but they are not zero, and the redesign is cheap.

---

## 4. Catalogue: 12 ordered (from, to) class pairs

| from → to | Real (split-worthy) trigger | Passive / spurious trigger | Discriminator |
| --- | --- | --- | --- |
| Atmo (0) → Exo (1) | Ascent — engines burning through 70 km | On-rails periapsis-pass-out; focused atmo-graze with no thrust | **Thrust event near boundary**; or `prev.source != Checkpoint` and an active engine state at the crossing UT |
| Exo (1) → Atmo (0) | Reentry — heat builds, parachutes, decoupling, suicide burn | On-rails periapsis-pass-in; focused glancing pass | **Any of**: thrust event, thermal-animation event, decouple/jettison/parachute, gear-deploy, near boundary |
| Atmo (0) → Surface (2) | Touchdown — wheels-down or splashdown after atmo descent | (none — Surface is set by `situation == LANDED/SPLASHED`, not by altitude crossing) | **Always meaningful** — keep current behavior |
| Surface (2) → Atmo (0) | Take-off — runway/water launch | EVA jetpack hop on Kerbin (already gated by `ShouldKeepContinuousEvaAtmoSurfaceTogether`) | **Always meaningful** — keep current behavior, EVA gate already in place |
| Atmo (0) → Approach (3) | (impossible — Approach is airless-body-only) | (impossible) | n/a — defensive: keep splitting |
| Approach (3) → Atmo (0) | (impossible — Approach requires `!hasAtmosphere`) | (impossible) | n/a — defensive: keep splitting |
| Exo (1) → Surface (2) | Vacuum landing burn ends with touchdown on airless body | (none — Surface only via `situation`) | **Always meaningful** — keep current behavior |
| Surface (2) → Exo (1) | Take-off from airless body | Bouncy EVA hop (debounce already handles) | **Always meaningful** — keep current behavior |
| Exo (1) → Approach (3) | Vacuum descent crossing the airless approach altitude | Highly eccentric flyby just dipping below approach altitude (rare) | **Always meaningful** — keep current behavior (see resolution below) |
| Approach (3) → Exo (1) | Take-off ascent past approach altitude | Same flyby case in reverse | **Always meaningful** — keep current behavior |
| Surface (2) → Approach (3) | Take-off on airless body before reaching escape (debounce already covers near-surface jitter) | EVA jetpack on Mun | **Keep current** — Surface↔Approach already debounced (`ApproachDebounceSeconds = 3.0`, see [EnvironmentDetector.cs:243-245](../../../Source/Parsek/EnvironmentDetector.cs)) |
| Approach (3) → Surface (2) | Vacuum landing — passes through approach zone before touchdown | EVA jetpack settle | **Keep current** |

**Aggregating into three buckets:**

1. **Body change (#251)** — orthogonal, always meaningful in this historical proposal. Later #547 narrowed coasting ExoBallistic body changes to stay cohesive.
2. **Boundary involves Surface (class 2) or Approach (class 3)** — always meaningful. Surface boundaries are gated by `Vessel.Situations` flags + debounce. Approach boundaries get the same default-allow treatment in v1: the airless-flyby noise case (§4 row 9) is rare in practice and a single false-positive split per flyby is mild, while a passive-flyby gate would require yet more signal-engineering. **Decision: Approach↔Exo is *not* gated; treat any class-3 boundary as always meaningful, matching the helper in §5.** §8 open Q3 records this as a v1.1 candidate to revisit if playtests show false positives.
3. **Pure Atmospheric ↔ Exo pair** — the entire noise cluster sits here. Gate behind a meaningful-action signal.

---

## 5. The discriminator

Candidate signals, ranked by reliability and ease of implementation:

**S1. PartEvent within a window of the split UT.** The optimizer already has `Recording.PartEvents` in hand and walks them in `IsInertPartEventForTailTrim` etc. The boundary is meaningful if any PartEvent satisfies `|evt.ut - splitUT| <= W` and `evt.eventType ∈ MeaningfulSet`, where `splitUT = next.startUT` — the same UT that `RecordingOptimizer.SplitAtSection` ([RecordingOptimizer.cs:367](../../../Source/Parsek/RecordingOptimizer.cs)) actually cuts at. Adjacent sections can have a small gap or overlap (the recorder closes/opens at slightly different UTs across some seam paths, and `TrimOverlappingSectionFrames` reconciles after merges), so anchoring the gate to `prev.endUT` instead of the real cut UT can accept or suppress the wrong boundary. Pass `splitUT` in explicitly:

```
MeaningfulSet = {
    EngineIgnited, EngineShutdown,                    // burn boundaries
    EngineThrottle      (with value > 0),             // active throttling
    RCSActivated, RCSStopped,                         // RCS burn boundaries
    RCSThrottle         (with value > 0),             // active RCS throttling
    Decoupled, Destroyed,                             // staging / breakup
    ShroudJettisoned, FairingJettisoned,              // ascent shroud
    ParachuteSemiDeployed, ParachuteDeployed,         // reentry
    ParachuteCut, ParachuteDestroyed,                 // dynamic chute event
    GearDeployed, GearRetracted,                      // landing prep
    ThermalAnimationHot, ThermalAnimationMedium       // reentry heat
}
```

RCS events are necessary because `EnvironmentDetector.Classify` ([EnvironmentDetector.cs:96-99](../../../Source/Parsek/EnvironmentDetector.cs)) sets `ExoPropulsive` only when an `ModuleEngines` engine has `finalThrust > 0`. RCS thrust is tracked via separate dictionaries (`activeRcsKeys`/`lastRcsThrottle`, see CLAUDE.md "Engine key encoding") and does **not** flip the env from `ExoBallistic` to `ExoPropulsive`. Without RCS in the set, an RCS-only deorbit kick that crosses 70 km would have `ExoBallistic → Atmospheric` on both sides, S3 would not short-circuit, and the gate would suppress the split despite a recorded RCS event at the crossing UT.

`W = 5.0 s` is a reasonable starting point: physics-tick PartEvent timestamps and TrackSection boundary UTs are usually within milliseconds, so any honest reentry/ascent will land in the window. Tightening to 2 s also works but risks missing throttle-hold ascents where the throttle stays constant across the 70 km mark and the nearest engine event is the launch at T-0; in that case a thermal or decouple event normally lands closer.

This is the primary signal.

**S2. TrackSection source provenance.** `TrackSection.source` distinguishes `Active` / `Background` / `Checkpoint`. A boundary where both adjacent sections are `Checkpoint` is by construction on-rails (the section is an `OrbitalCheckpoint`-frame coast). A boundary where both are `Background` and `referenceFrame == OrbitalCheckpoint` is also on-rails. **However:** in the current pipeline, on-rails coasts are a *single* section (no env transitions while packed), so the case "two adjacent checkpoint sections with different env classes" should not arise from the recorder pathway. If it does (e.g. via test fixtures or a future feature), the boundary is by definition geometric and we can short-circuit `false`.

This is a defensive backstop, not the primary signal.

**S3. SegmentEnvironment refinement (Propulsive vs Ballistic).** `SplitEnvironmentClass` collapses `ExoPropulsive` and `ExoBallistic` into class 1. The *raw* enum still distinguishes them. An `Atmospheric → ExoPropulsive` boundary means the vessel was burning at the crossing — this is itself a strong meaningful-action signal that does not need a PartEvent. By symmetry, `ExoPropulsive → Atmospheric` is meaningful (atypical but real: suicide reentry burn). The ambiguous cases reduce to `Atmospheric ↔ ExoBallistic`, which is exactly where S1 must do the work.

**S4. Reference frame.** `OrbitalCheckpoint` reference frames are by definition on-rails Keplerian. If both sides of a boundary are `OrbitalCheckpoint`, the crossing is geometric. Today the recorder pathways don't produce this, but it's a clean defensive check.

**S5. One-way vs oscillating.** Tempting in principle ("a chain that crosses atmo back and forth N>2 times is suspect"), but composing it across already-split recordings is awkward — once split, each half sees only its own boundary count. **Drop.** The PartEvent gate handles all the realistic cases without needing to look at the chain's global shape.

**Composition.**

```
internal static bool IsMeaningfulSplitBoundary(
    Recording rec, TrackSection prev, TrackSection next, double splitUT)
{
    int prevClass = SplitEnvironmentClass(prev.environment);
    int nextClass = SplitEnvironmentClass(next.environment);

    // 5A. Surface (class 2) or Approach (class 3) involvement: always meaningful.
    if (prevClass == 2 || nextClass == 2) return true;
    if (prevClass == 3 || nextClass == 3) return true;

    // 5B. Pure Atmo ↔ Exo (the noise cluster).
    // S3: thrust-on at the crossing (ExoPropulsive on either side) is itself meaningful.
    if (prev.environment == SegmentEnvironment.ExoPropulsive
        || next.environment == SegmentEnvironment.ExoPropulsive)
        return true;

    // S2 / S4: defensive — two checkpoint frames with differing env class shouldn't
    // happen, but if they do the crossing is geometric.
    if (prev.referenceFrame == ReferenceFrame.OrbitalCheckpoint
        && next.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
        return false;

    // S1: meaningful PartEvent within ±W of the split UT (next.startUT — the UT
    // SplitAtSection actually cuts at; not prev.endUT, which can drift by gap/overlap).
    return HasMeaningfulPartEventNearUT(rec, splitUT, MeaningfulBoundaryWindowSeconds);
}
```

Wired into `FindSplitCandidatesForOptimizer`:

```
TrackSection prev = rec.TrackSections[s - 1];
TrackSection next = rec.TrackSections[s];
double splitUT    = next.startUT;   // matches RecordingOptimizer.SplitAtSection:367

bool envChanged   = SplitEnvironmentClass(prev) != SplitEnvironmentClass(next);
bool bodyChanged  = SectionBodyChanged(prev, next);   // #251 — historical proposal treated as always meaningful

bool meaningful   = bodyChanged
                 || (envChanged && IsMeaningfulSplitBoundary(rec, prev, next, splitUT));

if (!meaningful) continue;
if (CanAutoSplitIgnoringGhostTriggers(rec, s)) candidates.Add((i, s));
```

The body-change rule stays unconditional. The env-class rule gains the meaningfulness gate.

`MeaningfulBoundaryWindowSeconds` is a new constant on `RecordingOptimizer`. Start at 5.0 s; revisit if playtests show false negatives on slow ascents that throttle-hold across the crossing.

---

## 6. Backwards compatibility

### Forward-only application

The optimizer split pass runs in `RecordingStore.RunOptimizationPass` ([RecordingStore.cs:1970-1980](../../../Source/Parsek/RecordingStore.cs)) and is invoked from `ParsekScenario.OnLoad`, `ChainSegmentManager`, `MergeDialog`, and a few other commit sites ([grep audit above](#references)). On every load and every commit, the pass walks committed recordings looking for split candidates.

For a recording that was *already* split under the old rule, each half has fewer than two TrackSections of distinct class — usually exactly one section, since splits partition by section index. `FindSplitCandidatesForOptimizer` requires `rec.TrackSections.Count >= 2` and an env-class transition; an already-split half does not satisfy that on its own. **Already-split recordings are not re-evaluated by the new gate.**

### Could already-split chains get re-merged?

The merge pass uses `CanAutoMerge` ([RecordingOptimizer.cs:25-70](../../../Source/Parsek/RecordingOptimizer.cs)). The relevant gate:

```
bool samePhase = a.SegmentPhase == b.SegmentPhase;
if (samePhase) {
    if (a.SegmentBodyName != b.SegmentBodyName) return false;
} else if (!CanMergeContinuousEvaAtmoSurfaceBoundary(a, b)) {
    return false;
}
```

Existing chain segments produced by env-class splits carry distinct `SegmentPhase` tags ("atmo" / "exo" / "surface" / "approach", set in `SplitAtSection` via `EnvironmentToPhase`). Adjacent halves from a passive-atmo-grazing split would be tagged `atmo` and `exo` — different — so `samePhase` is false. The EVA atmo-surface fallback does not apply (these are atmo-exo, not atmo-surface, and these are not EVA recordings). **The merge gate forbids re-merging.** Legacy chain layouts are stable across the rule change.

### What the change *does* affect on load

A recording that was committed but *never* optimizer-split because of an unrelated guard (e.g., it had `ghostingTriggers` blocking the conservative `CanAutoSplit`, or the halves would have been < 5 s) will be re-evaluated under the new rule on the next load. If it has a passive atmo crossing it now stays whole instead of being split. This is the intended forward-only effect.

### Sidecar files / on-disk state

No schema change. `TrackSection.source`, `referenceFrame`, and `environment` are already serialized. `Recording.PartEvents` is already loaded. No format-version bump required.

---

## 7. Test plan

### Unit tests (xUnit, `Source/Parsek.Tests/RecordingOptimizerTests.cs`)

The existing fixtures in `RecordingOptimizerTests.cs:467-533` are the model. They use `MakeRecordingWithSections(startUT, midUT, endUT, env1, env2)` — extend that helper (or add a sibling) to also accept a `PartEvent[]` for the meaningful-action signal.

Tests to add under `#region FindSplitCandidatesForOptimizer` (the production split entry point). All event timestamps are expressed relative to `splitUT = next.startUT` (the same UT the implementation gates on); when the test fixture introduces a deliberate gap or overlap between `prev.endUT` and `next.startUT`, both timestamps must be set explicitly so the tests pin down the cut UT, not the prior section's tail.

1. **Atmo ↔ ExoBallistic, no PartEvents anywhere** → empty (the new noise-suppression case). Two variants: forward (atmo → exo) and reverse.
2. **Atmo ↔ ExoBallistic, with `EngineIgnited` within 2 s of `splitUT`** → split. Confirms thrust gates the split.
3. **Atmo ↔ ExoBallistic, with `EngineIgnited` 30 s before `splitUT` (outside window)** → empty. Confirms window bounds.
4. **Atmo ↔ ExoBallistic, with `Decoupled` event at `splitUT`** → split.
5. **Atmo ↔ ExoBallistic, with `ParachuteDeployed` event at `splitUT`** → split.
6. **Atmo ↔ ExoBallistic, with `ThermalAnimationHot` at `splitUT`** → split. Covers passive reentry without engine.
7. **Atmo → ExoPropulsive (S3 short-circuit)** → split, even without nearby PartEvents.
8. **ExoBallistic → ExoBallistic (no class change)** → empty (current behavior, regression check).
9. **Atmo ↔ Surface boundary** → split (Surface bucket bypasses the gate).
10. **ExoBallistic ↔ Surface boundary** → split.
11. **Body change with same env class on both sides** → historical proposal split; later #547 keeps coasting ExoBallistic body changes cohesive.
12. **Body change AND class change AND no PartEvents** → split (body-change / class-change short-circuits the env-meaningful gate).
13. **Multiple atmo↔exo oscillations in one recording, all without PartEvents** → empty. The N×splits-per-orbit eccentric case.
14. **Multiple atmo↔exo oscillations, with engine event on the *first* crossing only** → split exactly once (at the meaningful crossing). Confirms per-boundary independence.
15. **Both adjacent sections are `OrbitalCheckpoint` with differing env class** → empty (defensive S4 check).
16. **Atmo ↔ ExoBallistic, with `RCSActivated` and `RCSThrottle (value > 0.1)` within window** → split. Covers RCS-only deorbit kick (see §5 RCS-event note).
17. **Two `Background`/`Absolute` sections with differing env class, second is single-frame, no PartEvents at the seam** → empty. Covers Producer C (no-payload boundary section from `BackgroundRecorder.FlushLoadedStateForOnRailsTransition`).
18. **Gap larger than the window: `prev.endUT = next.startUT - (W + 2)` (`W = MeaningfulBoundaryWindowSeconds`), event placed exactly at `prev.endUT`, no event at `next.startUT`** → empty. Confirms the gate measures from `splitUT = next.startUT` and not from `prev.endUT`: under a `prev.endUT`-anchored implementation the event would be at offset 0 and the boundary would *split*; under the correct `splitUT`-anchored implementation the event is `W + 2` seconds away and the boundary is suppressed. Symmetric overlap variant: `prev.endUT = next.startUT + (W + 2)`, event at `prev.endUT`, no event at `next.startUT` → also empty. The offset must be strictly greater than `W` (use `W + 2` for a safe margin) — a 3-second offset would still be inside a 5-second window measured from `splitUT` and the test would not discriminate.

`MeaningfulBoundaryWindowSeconds` should be exposed `internal const` so tests can compute timestamps relative to `splitUT` without hard-coding the literal.

### Synthetic-data scenarios — xUnit, NOT in-game

All synthetic-chain scenarios go in `Source/Parsek.Tests/RecordingOptimizerTests.cs`. The in-game runtime suite cannot consume `RecordingBuilder` / `VesselSnapshotBuilder` — the generators live in `Source/Parsek.Tests/Generators` and the test project references the mod project, not the other way around (`Parsek.Tests.csproj` → `Parsek.csproj`, see `Parsek.csproj` references — no `Parsek.Tests` reference). Adding a reverse reference would pull xUnit and test-only types into the shipped DLL.

The scenarios below extend the existing `MakeRecordingWithSections` helper to also accept a `PartEvent[]` and assert on the chain after a full `RecordingStore.RunOptimizationPass()` round-trip (the helper class can use `[Collection("Sequential")]` since the pass touches `RecordingStore` static state):

- **Real ascent** — Surface → Atmospheric → ExoPropulsive → ExoBallistic with `EngineIgnited` at section 0 start and `EngineThrottle (value > 0)` near each crossing. Expect chain length ≥ 3 (surface→atmo, atmo→exo splits both fire; ExoPropulsive→ExoBallistic is intra-class and is *not* a split candidate).
- **Eccentric atmo-grazing (synthetic)** — single recording with N (≥ 4) atmo↔exo TrackSection pairs and zero PartEvents anywhere. Expect chain length 1 (no splits). Anchors the "100 orbit" symptom even if the natural recorder pathway never produces it in 1.0; the synthetic asserts the rule itself.
- **Real reentry** — ExoBallistic → Atmospheric → SurfaceMobile, with `ParachuteDeployed` and `ThermalAnimationHot` in the atmo segment near the entry boundary. Expect both the exo→atmo split and the atmo→surface split.
- **Vacuum landing burn** — ExoBallistic → ExoPropulsive → SurfaceStationary. Expect **exactly one** split, at the exo→surface boundary (class 1 → class 2). The internal ExoBallistic → ExoPropulsive boundary is intra-class (both class 1) and is *not* a split candidate under any version of the rule, current or proposed; this is unchanged from current behavior. Re-splitting engine on/off transitions would be a separate feature, intentionally out of scope.
- **RCS-only deorbit** — ExoBallistic → Atmospheric, with `RCSActivated` and `RCSThrottle (value > 0)` near the boundary, no engine events. Expect a split. Confirms RCS coverage in `MeaningfulSet`.
- **Surface take-off** — SurfaceStationary → Atmospheric (Kerbin) and SurfaceStationary → Approach (Mun) variants. Expect splits in both.
- **SOI traversal mid-coast** — Kerbin ExoBallistic → Mun ExoBallistic, no env-class change but body change. Expect split (#251).
- **Focused atmo-graze without intervention** — two TrackSections (Atmospheric / ExoBallistic) and no PartEvents in the boundary window. Expect chain length 1.
- **Loaded → on-rails no-payload boundary seam (Producer C)** — two adjacent `Background`/`Absolute` sections with differing env class, second section single-frame, no PartEvents at the seam. Expect chain length 1: the seam is a recorder bookkeeping artifact (vessel went on-rails at a moment that happened to coincide with an env-class boundary), not a gameplay phase change. Test fixture mirrors `BackgroundRecorder.FlushLoadedStateForOnRailsTransition`.

### One live in-game smoke test (`Source/Parsek/InGameTests/RuntimeTests.cs`)

A single `[InGameTest(Category = "Optimizer", Scene = GameScenes.FLIGHT)]` test that exercises the actual recorder pathway end-to-end: launch a stock craft, ascend through 70 km with engines firing, circularize, then call `RecordingStore.RunOptimizationPass()` and assert the chain has at least the expected surface→atmo and atmo→exo splits. The mod-side runtime can build a `Recording` directly via the public types in `Source/Parsek/` (Recording, TrackSection, PartEvent) without needing `RecordingBuilder`. This test guards against future regressions where a recorder change starts emitting boundary PartEvent UTs that fall outside the meaningful window.

### Regression coverage to keep

Every existing test in `#region FindSplitCandidates` and `#region FindSplitCandidatesForOptimizer` must still pass — the rule change should be additive (block previously-positive candidates) and should not invent new split-positive cases. If a current test asserts "atmo → exo splits" without any nearby PartEvents, that test is the eccentric case in disguise and needs to be updated to either supply PartEvents or assert the new behavior.

---

## 8. Open questions

1. **Window size.** 5 s is an educated starting point. The recorder writes PartEvents and TrackSection boundary UTs from the same physics tick stream, so they're usually milliseconds apart in practice. Playtest data — specifically a slow electric-engine ascent that holds throttle through 70 km — would tell us whether a wider window is needed. The cost of widening is a higher false-positive rate (splits triggered by an unrelated event minutes from the crossing); the cost of narrowing is the eccentric-orbit symptom recurring at slow ascents. 5 s is far on the conservative side of either failure mode.

2. **Thermal animation on the active vessel.** Heat events fire reliably on aerobraking and reentry, but the threshold for `ThermalAnimationHot` needs a quick check — if it only fires above a high threshold, glancing reentries (e.g. a single 65 km dip) might not produce the event and would be classed as passive. If that's the case, the fallback is to add a **velocity-direction signal** (the velocity vector pointed *into* the body at the crossing). That's a richer change because the optimizer doesn't currently consume velocity data; defer until playtest demands it.

3. **`Approach ↔ Exo` treatment.** Section 4 / §5 keep Approach↔Exo as always-meaningful (any class-3 boundary short-circuits to `true` in the helper). The alternative — applying the same meaningful-action gate as Atmo↔Exo — was considered and deferred. Rationale: passive low-flying flybys on airless bodies are rare, the failure mode (one extra split per flyby) is mild, and the helper stays simpler. If playtest ever shows a noisy case there, the gate composes naturally — drop the `prevClass == 3 || nextClass == 3` short-circuit and the boundary falls through to the PartEvent check.

4. **Where does the meaningful-action gate live?** The natural home is `RecordingOptimizer` itself (alongside `SplitEnvironmentClass`). Extracting to a separate file would be premature — the function is small and tightly coupled to the optimizer's responsibilities.

5. **Logging.** Per the project logging requirements: when the new gate suppresses a candidate, emit a `ParsekLog.Verbose("Optimizer", "Split suppressed (passive crossing): rec={recId} splitUT={splitUT} prev={prev.environment} next={next.environment}")`. When it accepts a previously-borderline candidate, log the discriminator that fired (thrust / decouple / thermal / body-change). Use `splitUT = next.startUT` in the log for both lines — same UT the gate measures against and the same UT `SplitAtSection` cuts at. The optimizer pass already logs split outcomes ([RecordingOptimizer.cs:600-604](../../../Source/Parsek/RecordingOptimizer.cs)); the new lines fit alongside.

---

## 9. References

- `Source/Parsek/RecordingOptimizer.cs` — current split rule (`FindSplitCandidates*`, `SplitEnvironmentClass`, `SectionBodyChanged`, `EnvironmentToPhase`).
- `Source/Parsek/TrackSection.cs` — `SegmentEnvironment`, `ReferenceFrame`, `TrackSectionSource` enums.
- `Source/Parsek/PartEvent.cs` — PartEventType enum. `MeaningfulSet` above is a subset.
- `Source/Parsek/EnvironmentDetector.cs` — pure classification + hysteresis. Already debounces fast oscillation; the optimizer gate adds a second filter for the case where hysteresis legitimately confirms a crossing but the crossing is still semantically passive.
- `Source/Parsek/FlightRecorder.cs:5640` — `UpdateEnvironmentTracking` (active-focus producer of env-class transitions).
- `Source/Parsek/BackgroundRecorder.cs:1450-1476` — loaded-physics-mode background producer.
- `Source/Parsek/BackgroundRecorder.cs:1348` — on-rails update loop (does *not* produce env transitions; clarifies why pure-on-rails atmo grazing cannot trigger the bug today).
- `Source/Parsek/RecordingStore.cs:1970-2080` — `RunOptimizationPass`, the only production caller of `FindSplitCandidatesForOptimizer`.
- `Source/Parsek.Tests/RecordingOptimizerTests.cs:465-535` — current `FindSplitCandidates` tests; pattern to extend.
- `docs/parsek-flight-recorder-design.md` §9A.5 — design intent for env-class splits (per-phase loop control).
- `docs/dev/research/extending-rewind-to-stable-leaves.md` §2.1, §S16 — original symptom report.
