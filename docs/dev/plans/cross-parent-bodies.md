# Cross-parent / interplanetary body support (Phase 4)

Status: DRAFT (planning, not implementation). Built on top of the merged
zero-drift reschedule (`docs/dev/plans/zero-drift-reschedule.md`).

Branch: `periodicity-all-bodies` (off `main`; zero-drift merged in PR #964).

Revision history:
- 2026-05-28 initial draft.
- 2026-05-28 folded clean-context Opus review (2 CRITICAL + 6 MAJOR + 5 MINOR + 5 NIT):
  fixed `ScheduleToleranceSecondsFor` formula citations, re-derived the launch-body
  heliocentric tolerance physics, specified `PhaseOffsetSeconds` for the new emission,
  pinned the `enableCrossParentScheduling` gate location + `BuildSignature` folding,
  spelled out the dedup pseudocode, removed the `MaxJointMultiples` confusion, gave
  a concrete `RecommendedLookaheadMultiples` formula, added the `StockFake` fixture
  extension to the test plan, noted the `SelectDominantConstraintIndex` quirk for
  Moho-class targets, the Tylo anchor-switch case, log-format consistency, the
  round-trip recording regression case, and the in-game-canary scope.

Sibling references:
- `Source/Parsek/MissionPeriodicity.cs` (constraint extractor + solver + zero-drift schedule)
- `Source/Parsek/MissionLoopUnitBuilder.cs` (where schedule attaches to a `LoopUnit`)
- `docs/parsek-logistics-supply-routes-design.md` §8.1 (body-hierarchy walker sketch)
- `docs/dev/plans/zero-drift-reschedule.md` (the math + schedule the solver reuses)
- `docs/dev/todo-and-known-bugs.md` (the "TODO - Phase 4" line that this plan replaces)

---

## 0. The problem (recap, precise)

Today the constraint extractor (`MissionPeriodicity.ExtractConstraints`) treats
ANY transited body whose parent is not the launch body as
`Support.UnsupportedCrossParent`. The MissionsWindow shows such a mission as
"not aligned" and the loop falls back to the raw launch anchor: relaunches
happen, but the recorded transfer arc no longer reaches the target body
because the target is at a different heliocentric / parent-centric phase
each cycle.

Concrete cases that hit this:

- **Kerbin -> Duna** (Duna's parent = Sun, launch body Kerbin's parent = Sun,
  so Duna and Kerbin are siblings, not parent/child).
- **Kerbin -> Eve / Moho / Jool / Eeloo** (same sibling shape).
- **Kerbin -> Ike** (Ike's parent = Duna; chain Ike -> Duna -> Sun; Kerbin's
  parent = Sun; common parent = Sun, and there is an extra Duna-centric layer).
- **Kerbin -> Laythe / Tylo / Vall / Bop / Pol** (chain ... -> Jool -> Sun;
  same shape as Ike, with Jool as the intermediate).
- **Kerbin -> Gilly** (chain Gilly -> Eve -> Sun).
- **Kerbin -> Mun -> Duna** (gravity-assist: Mun's parent IS Kerbin, but Duna
  is sibling; the mission has both same-parent and cross-parent constraints
  in the same chain).

The "TODO - Phase 4" note in `todo-and-known-bugs.md` line 92 articulates the
target approach: emit heliocentric `Orbital` constraints for both endpoints
around the common parent and let `FindBestJointMultiple` find the multi-
period near-resonance. This document fills that in concretely.

NOT in scope:

- `UnsupportedRendezvous` (a Relative-frame section anchored to another
  vessel). That is a vessel-alignment problem, not a body-alignment one;
  the body-only solver cannot model it. Stays unsupported.
- Re-aim ("re-fly a fresh transfer" with corrected Δv): this plan stays in
  the locked **replay-as-is** model. We are computing when the live sky
  matches the recorded sky, NOT computing a new transfer.

---

## 1. The goal

For any mission whose recorded path crosses one or more SOIs, schedule
relaunches so that at each launch UT every body the recording transits is
back at its recorded phase within the body's own tolerance:

- The launch site faces its recorded inertial direction (`Rotation(launchBody)`,
  tolerance ~0.25 deg of the sidereal day; that one is non-negotiable).
- Each transited body the recording traverses is at its recorded phase
  around its parent, to within that body's SOI radius / mean orbital
  velocity (the same physics tolerance the zero-drift solver already uses
  for `Orbital(Mun)`).
- For a cross-parent target the launch body's OWN phase around the common
  parent must also match (a new constraint this plan introduces; today's
  extractor does not emit it).

Same-body and direct-child missions are byte-identical to today (the
extractor emits the same constraints, the solver chooses the same anchor,
the schedule looks the same).

Cross-parent missions transition from "not aligned" to "aligned with a
long cadence", typically multiple in-game years. The existing "Warp to..."
button is the user's escape hatch for the long wait; no new UI behavior is
required for v1 of this phase.

---

## 2. The body-hierarchy walk

Adapted from `docs/parsek-logistics-supply-routes-design.md` §8.1; here we
use it to FIND THE COMMON ANCESTOR for constraint emission, not to compute
a synodic period.

### 2.1 Pure helper

Add to `MissionPeriodicity.cs` (the periodicity module owns body-graph math
for the solver; the logistics module can call into it later if/when it
ships).

```csharp
/// <summary>
/// Walks the body-reference chain from <paramref name="bodyName"/> up to
/// the root (the Sun has no parent, so ReferenceBodyName(Sun) == null).
/// Returns the chain as a list ordered child-to-root, e.g. for Ike:
/// ["Ike", "Duna", "Sun"]. Returns an empty list when the body is null
/// or unknown. Pure; reads only IBodyInfo.ReferenceBodyName.
/// </summary>
internal static List<string> AncestorChain(string bodyName, IBodyInfo bodyInfo);

/// <summary>
/// Finds the deepest common ancestor of two bodies in the reference-body
/// graph. The result + each side's downward chain to that ancestor are
/// returned via out params:
///   - commonAncestor: e.g. "Sun" for Kerbin/Duna, "Kerbin" for Kerbin/Mun.
///   - launchToAncestor: chain from launchBody UP TO BUT NOT INCLUDING the
///     common ancestor, e.g. ["Kerbin"] for Kerbin/Duna, [] for Kerbin/Mun
///     (launch IS the ancestor).
///   - targetToAncestor: same shape for targetBody, e.g. ["Duna"] for
///     Kerbin/Duna, ["Ike", "Duna"] for Kerbin/Ike, ["Mun"] for Kerbin/Mun.
/// Returns false when the two chains are disconnected (planet-pack
/// pathology); callers treat that as no-lock + log.
/// </summary>
internal static bool TryFindCommonAncestor(
    string launchBody,
    string targetBody,
    IBodyInfo bodyInfo,
    out string commonAncestor,
    out List<string> launchToAncestor,
    out List<string> targetToAncestor);
```

The two helpers use slightly different conventions on purpose:

- `AncestorChain` INCLUDES the input body and INCLUDES the root, so `AncestorChain("Ike")
  == ["Ike", "Duna", "Sun"]`. Convenient when a caller wants the body's full reachable
  ancestry.
- `TryFindCommonAncestor` EXCLUDES the common ancestor from both `launchToAncestor` and
  `targetToAncestor`, INCLUDES each endpoint body itself. So for Kerbin/Duna the launch
  side is `["Kerbin"]` (Kerbin included, Sun excluded) and the target side is `["Duna"]`.
  Convenient when the caller wants to iterate "what bodies sit BETWEEN the endpoint and
  the common ancestor, INCLUSIVE of the endpoint".

Both contracts are unit-tested for the degenerate cases (launch == target -> ancestor =
both, launchToAnc = [], targetToAnc = []; either side reaches root before meeting ->
ancestor = root or false).

### 2.2 Worked examples (sanity)

| launch | target | common | launchToAncestor | targetToAncestor |
|--------|--------|--------|------------------|------------------|
| Kerbin | Mun    | Kerbin | []               | [Mun]            |
| Kerbin | Duna   | Sun    | [Kerbin]         | [Duna]           |
| Kerbin | Ike    | Sun    | [Kerbin]         | [Ike, Duna]      |
| Kerbin | Laythe | Sun    | [Kerbin]         | [Laythe, Jool]   |
| Kerbin | Gilly  | Sun    | [Kerbin]         | [Gilly, Eve]     |
| Eve    | Gilly  | Eve    | []               | [Gilly]          |

The `launchToAncestor` list is empty exactly when the launch body IS the
common ancestor (the same-parent case is `launchToAncestor == []` and
`targetToAncestor.Count == 1`).

---

## 3. Extending the constraint extractor

Today the extractor (`MissionPeriodicity.ExtractConstraints`, around lines
278-470) emits a single `Orbital(C)` per transited body C, with its period
read as `bodyInfo.OrbitPeriod(C)` (= C around C.parent). Then if C.parent !=
launchBody it sets `Support.UnsupportedCrossParent` and stops.

### 3.1 New rule (replaces rule 4)

For each transited body C the recording crosses (`OrbitSegment.bodyName !=
launchBody` OR `OrbitalCheckpoint.bodyName != launchBody`):

1. Resolve `TryFindCommonAncestor(launchBody, C, bodyInfo, out anc, out
   launchToAnc, out targetToAnc)`. If false, fall back to today's per-body
   `UnsupportedCrossParent` for THIS body (rare; planet-pack pathology) and
   keep going on the others.
2. For each body B in `targetToAnc` (so [C, ..., direct child of anc]), emit
   `Orbital(B, period = bodyInfo.OrbitPeriod(B))`. That period is already B
   around B.parent because `IBodyInfo.OrbitPeriod` is parent-centric.
3. For each body B in `launchToAnc` (typically [launchBody]), emit
   `Orbital(B, period = bodyInfo.OrbitPeriod(B), phaseOffset = earliestOrbitStartByBody[B] - ut0)`.
   **This is the new layer today's extractor never emits.** Skip the emission when
   `launchToAnc` is empty (same-parent case: launch IS the ancestor and there is no
   launch-side heliocentric layer to lock). The launch body is already in
   `earliestOrbitStartByBody` today (the existing rule-4 loop just skips it via the
   `body == launchBody` early-continue). Reusing that dict entry gives the launch-side
   emission the same `phaseOffset` shape as the existing target-side one:
   `earliestOrbitStartByBody[B] - ut0`. The offset cancels in `JointStepResidual` (per
   `zero-drift-reschedule.md` §2.1: phase offsets cancel), so this choice does NOT change
   the chosen k; it matters for the per-constraint log dump + the dominant-selection
   tie-break.
4. Deduplicate by body name. A simple `HashSet<string>` of already-emitted bodies (seeded
   with the existing target-side loop) guards the launch-side emission so a Kerbin -> Ike
   mission emits Duna once even though both target chains share it (here only the one
   chain), and a Kerbin -> Mun -> Duna gravity-assist mission emits Kerbin once even
   though both Mun and Duna chains traverse it (Mun -> Kerbin via direct child,
   Duna -> Sun via cross-parent). Body name suffices because `IBodyInfo.ReferenceBodyName`
   is a function of body name (a body only ever has one parent in the live graph).

   Concrete merge pseudocode (replaces the existing rule-4 loop):

   ```csharp
   // existing dict: earliestOrbitStartByBody, populated by the existing scan over
   // OrbitSegments + OrbitalCheckpoint.checkpoints + SegmentBodyName + StartBodyName.
   var emittedBodies = new HashSet<string>();

   // Target-side: emit Orbital(C) for every body C the recording transits, then walk
   // up C.parent chain to the common ancestor with launchBody (excluded), emitting an
   // Orbital(B) for every intermediate body B en route.
   foreach (var (body, earliestUT) in earliestOrbitStartByBody)
   {
       if (body == launchBody) continue; // existing rule-4 behavior
       if (!TryFindCommonAncestor(launchBody, body, bodyInfo,
               out _, out var launchToAnc, out var targetToAnc))
       {
           result.Support = Support.UnsupportedCrossParent; // per-body fallback
           continue;
       }
       foreach (var b in targetToAnc) // [body, ..., direct child of ancestor]
       {
           if (!emittedBodies.Add(b)) continue;
           double pUT = earliestOrbitStartByBody.TryGetValue(b, out var earliest)
               ? earliest
               : earliestUT;
           EmitOrbital(b, bodyInfo.OrbitPeriod(b), pUT - ut0);
       }
       // Launch-side: only emit once per cross-parent body C; subsequent C's that share
       // the same launchToAnc are deduped by emittedBodies.
       foreach (var b in launchToAnc) // typically [launchBody]; empty for same-parent
       {
           if (!emittedBodies.Add(b)) continue;
           double pUT = earliestOrbitStartByBody.TryGetValue(b, out var earliest)
               ? earliest
               : ut0;
           EmitOrbital(b, bodyInfo.OrbitPeriod(b), pUT - ut0);
       }
   }
   ```

   The `pUT - ut0` fallback when the dict misses (a transited body that appears only via
   chain traversal, not directly in `OrbitSegments`) uses `ut0` itself: the constraint
   gets a phase offset of 0, which makes the constraint "at the start of the mission"
   for log/UI display purposes. Mathematically still correct because the offset cancels.

`Support.UnsupportedCrossParent` is RETIRED. A multi-layer emission is just
"more constraints", and the existing joint best-fit handles it.

`Support.UnsupportedRendezvous` is unchanged (vessel anchor, not body).

### 3.2 What constraints a Kerbin -> Duna mission emits after the change

Recorded path: launch from KSC, ascend to Kerbin orbit, transfer-inject,
Kerbin SOI exit, heliocentric coast, Duna SOI entry, capture-burn, land.

Today (after this plan: the table changes):

| Today | After Phase 4 |
|---|---|
| `Rotation(Kerbin)` (pad, tol ~15s, 0.25 deg) | `Rotation(Kerbin)` (unchanged) |
| `Orbital(Duna, period=heliocentric)` flagged `RelativeToParent=true` | `Orbital(Duna, period=heliocentric)`, `RelativeToParent=true` (unchanged shape) |
| (none) | **NEW:** `Orbital(Kerbin, period=heliocentric)`, `RelativeToParent=true` |
| `Support = UnsupportedCrossParent` | `Support = Supported` |

If the mission also lands on Duna (a transited-body surface segment), the
existing `Rotation(Duna)` constraint joins the set, gated by the existing
"Landing-body alignment" A/B flag (`TransitedBodyRotationMode`
Drop/Loose/Tight): same code path, no new flag.

### 3.3 What constraints a Kerbin -> Ike mission emits

| Constraint | Period | Tolerance |
|---|---|---|
| `Rotation(Kerbin)` | sidereal day | 0.25 deg |
| `Orbital(Kerbin, around Sun)` | Kerbin's heliocentric year | `min(SoiRadius(Kerbin)/OrbitalVelocity(Kerbin), SoiRadius(Ike)/OrbitalVelocity(Ike))` -- the limiting SOI-reconnect window along the recorded arc (see §4.4) |
| `Orbital(Duna, around Sun)` | Duna's heliocentric year | `SoiRadius(Duna) / OrbitalVelocity(Duna)` -- target SOI / heliocentric velocity |
| `Orbital(Ike, around Duna)` | Ike's Duna-centric period | `SoiRadius(Ike) / OrbitalVelocity(Ike)` -- Ike's own SOI / Duna-centric orbital velocity |
| `Rotation(Ike)` (if landed) | Ike's sidereal day | gated by `TransitedBodyRotationMode` |

The tolerance for each Orbital is the existing `ScheduleToleranceSecondsFor`
formula: `SoiRadius(body) / OrbitalVelocity(body)` (one factor of SOI radius over the
orbital velocity, no factor of 2; verify against `MissionPeriodicity.cs:1162-1166`).
For stock Kerbin that gives ~84,000 km / ~9,285 m/s = ~9,050 s -- not the ~18,100 s an
earlier draft of this plan wrongly cited. That formula is already body-agnostic; no
change needed at the function call site -- just be precise in the test expectations
(see section 8.3).

### 3.4 Gravity assist (Kerbin -> Mun -> Duna)

The recording's `OrbitSegments` cross Kerbin -> Mun -> Kerbin (the assist) ->
Sun -> Duna. The body-set is {Mun, Duna} as transited targets; Kerbin is the
launch body. The two chains:

- Mun chain (target): common = Kerbin, targetToAnc = [Mun], launchToAnc = [].
- Duna chain (target): common = Sun, targetToAnc = [Duna], launchToAnc = [Kerbin].

Combined emission (deduped):

| Constraint | Period |
|---|---|
| `Rotation(Kerbin)` | ~6h |
| `Orbital(Mun, around Kerbin)` | ~6d |
| `Orbital(Kerbin, around Sun)` | Kerbin year |
| `Orbital(Duna, around Sun)` | Duna year |

Four constraints. If the assist mission ALSO lands on the Mun (a low-energy free-return
sometimes does), the existing surface-segment rule emits a fifth: `Rotation(Mun)` (tidally
locked to Mun-around-Kerbin period). That stays gated by `TransitedBodyRotationMode`
exactly like a same-parent Mun mission. Default Loose mode loosens it; Drop suppresses it.

The anchor selector picks the tightest duty cycle (Rotation(Kerbin), as today), and the
joint best-fit / zero-drift schedule walks k (integer multiples of `Rotation(Kerbin)`
period) looking for windows within tolerance of all the Orbital constraints
simultaneously. Cadence will be very long (Kerbin-Duna synodic dominates, ~2.1 Kerbin
years), but deterministic.

---

## 4. Solver / schedule changes

### 4.1 Anchor selection

`SelectAnchorConstraintIndex` is unchanged. It already picks the constraint
with the smallest duty cycle = tightest tolerance / period. For
cross-parent, that is still the launch-pad rotation (a quarter-degree
tolerance over a ~6h period is far tighter than a Duna-SOI tolerance over a
Duna year). The anchor stays on the pad; only the residual constraints get
longer.

Edge case worth flagging: for a Kerbin -> Tylo mission with the **Tight**
`TransitedBodyRotationMode`, Tylo's tidal-locked `Rotation(Tylo)` carries the
same 0.25 deg tolerance over a period (~3.5 Kerbin days) that yields the same
duty cycle `tol/period ~ 7e-4` as the launch-pad rotation (sidereal day at the
same 0.25 deg). The existing tie-break (shorter period wins, see
`MissionPeriodicity.cs:977`) selects Tylo as the anchor in that exact tie. The
launch pad is no longer pixel-perfect under that mode + that target. Loose mode
(default) drops Rotation(Tylo) to 5 deg, restoring pad dominance. Tight mode
playtest acceptance for Tylo-class targets needs the user to confirm the
anchor-on-Tylo behavior is acceptable; otherwise add an "anchor-on-launch-body"
preference flag to the tie-break.

### 4.2 Zero-drift schedule

`TryBuildRelaunchSchedule` is unchanged. Today it walks k integer multiples
of the anchor period and accepts the first k whose residuals fit every
non-anchor constraint within its tolerance. With more (and longer-period)
constraints, the accepted k just gets larger.

**Important distinction on which constant matters here.** The cross-parent path
takes the zero-drift / `TryBuildRelaunchSchedule` route (>= 2 distinct-period
constraints, by construction once we add a heliocentric layer). That path uses
`ScheduleLookaheadMultiples = 4096` to cap the k-walk. The OTHER constant,
`MaxJointMultiples = 16` (`MissionPeriodicity.cs:499`), governs ONLY the FIXED-CADENCE
fallback path's `FindBestJointMultiple`, which `TryBuildRelaunchSchedule` supersedes for
any drifting multi-constraint config. The MaxJointMultiples cap therefore does NOT bite
on cross-parent missions (they always route through the zero-drift path). An earlier
draft of this plan conflated the two; sections 8 + 9 below test and tune
`ScheduleLookaheadMultiples`, not `MaxJointMultiples`.

The relevant constants for cross-parent:

- `ScheduleLookaheadMultiples = 4096`. Each unit is one anchor period
  (sidereal day, ~6h Kerbin), so 4096 corresponds to ~7 Kerbin years of
  lookahead. The Kerbin-Duna synodic is ~2.1 Kerbin years, so 4096 covers
  several synodic windows -- enough for a typical Duna mission.
- `MaxScheduleSteps = 8192`. The cache cap on resolved launches. Unaffected
  by this change; only the COST of resolving each step grows.

The lookahead needs to grow with the longest constraint's period for deeper chains
(Kerbin -> Tylo / Bop / Pol; chain Tylo -> Jool -> Sun adds Jool's heliocentric
~36 Kerbin years). Add a pure helper:

```csharp
internal const double LookaheadCoverageFactor = 8.0; // need to cover ~8 longest-period
                                                       // cycles to be confident we find
                                                       // a within-tolerance window
internal const int MinLookaheadMultiples = 4096;       // floor: same as today

/// <summary>
/// Pick the k-walk horizon. The lookahead must cover several whole cycles of the
/// longest constraint (otherwise the brute-force walk hits the cap before the next
/// near-resonance opens). Floored at MinLookaheadMultiples so a same-body / single-
/// constraint config keeps today's behavior.
/// </summary>
internal static int RecommendedLookaheadMultiples(
    IReadOnlyList<PhaseConstraint> constraints, double anchorPeriod)
{
    if (constraints == null || constraints.Count == 0 || anchorPeriod <= 0.0)
        return MinLookaheadMultiples;
    double longest = 0.0;
    for (int i = 0; i < constraints.Count; i++)
        if (constraints[i].PeriodSeconds > longest)
            longest = constraints[i].PeriodSeconds;
    double recommended = LookaheadCoverageFactor * longest / anchorPeriod;
    int floored = (int)Math.Ceiling(recommended);
    return floored < MinLookaheadMultiples ? MinLookaheadMultiples : floored;
}
```

Worked sanity checks for the formula (anchor = stock Kerbin sidereal day ~21,549 s):

- Kerbin -> Duna: longest = Duna heliocentric ~1.74e7 s. recommended = 8 * 1.74e7 /
  21549 ~= 6,460. Floored at 4096 -> 6,460. Several synodic windows covered.
- Kerbin -> Eeloo: longest = Eeloo heliocentric ~3.37e7 s. recommended = 8 * 3.37e7 /
  21549 ~= 12,500. Past today's 4096; the new helper covers it.
- Kerbin -> Tylo (chain Tylo -> Jool -> Sun): longest = Jool heliocentric ~3.34e8 s.
  recommended = 8 * 3.34e8 / 21549 ~= 124,000. Big number. The k-walk in
  `TryBuildRelaunchSchedule` is microsecond-cheap per step, so 124k iterations is still
  well under a frame. Verified by the perf tests in section 8.3.
- Kerbin -> Mun (existing direct-child case): longest = Mun ~1.39e5 s. recommended =
  8 * 1.39e5 / 21549 ~= 52. Floored at 4096 -> 4096 (no regression).

The 8x coverage factor is a tunable constant; if a planet pack ships even longer-period
bodies and the walk still misses, raise it. Documented in the source.

### 4.3 Synodic vs. multi-period near-resonance

The naive "transfer window" formula `T_syn = |1/(1/T_K - 1/T_D)|` gives the
PRINCIPAL synodic period (~2.1y for Kerbin-Duna). It is the t for which
`Kerbin_phase(t) - Duna_phase(t)` returns to its starting value. With the
launch-pad rotation ALSO locked, only some of those synodic windows fall on
a pad-aligned k -- you may see cadences of 2x or 3x the synodic period in
practice. The joint best-fit picks whichever pad-aligned k passes every
tolerance; the cadence drops out naturally and is NOT a constant.

We do not emit a `SynodicPeriod` quantity anywhere; the math is "emit the
right constraints, run the joint solver". Same model as the existing Mun
mission, just with more (and longer) entries.

### 4.4 Tolerance for the LAUNCH body's heliocentric Orbital

The new constraint `Orbital(launchBody, around commonAncestor)` reuses
`ScheduleToleranceSecondsFor`, which returns `SoiRadius(body) / OrbitalVelocity(body)`
(see correction in §3.3: no factor of 2; the earlier draft was wrong).

**Which body's SOI?** The recorded interplanetary arc is heliocentric in the SUN's
frame. It leaves Kerbin's SOI on one side and arrives at the target's SOI on the other.
The arc actually RECONNECTS inside the *target* body's SOI (Duna, ~47.9 Mm), not
Kerbin's. So the physically-derived tolerance for "how much heliocentric phase error in
Kerbin still produces a Duna SOI capture along the recorded trajectory" should reference
the TARGET's SOI / TARGET's heliocentric velocity at intercept, NOT the launch body's
SOI. The earlier draft's "Kerbin SOI / Kerbin velocity" framing was wrong as derived
physics.

Practical choice for v1: take the MIN of `(SoiRadius(launchBody) / OrbitalVelocity(launchBody))`
and `(SoiRadius(targetBody) / OrbitalVelocity(targetBody))`, applied as the tolerance
on the launch-body heliocentric constraint. For stock Kerbin / Duna:
`min(84,000 km / 9285 m/s, 47.9 Mm / 7915 m/s) = min(9050 s, 6050 s) = 6050 s` (~1.7
hours of heliocentric drift). This is conservative -- it gives at least the smaller of
the two SOI-reconnect windows.

Implementation: extend `ScheduleToleranceSecondsFor` (or wrap it) with a new helper
`ToleranceSecondsForLaunchHeliocentric(launchBody, targetBody, bodyInfo)` that does the
min, then have the extractor emit the launch-body heliocentric Orbital with that
tolerance instead of the body-only one. New tests in section 8.3 pin the value.

A future refinement (out of scope for v1): take the WORST-CASE SOI over every transited
body on the chain, not just the launch/target pair. For Kerbin -> Tylo the chain is
Kerbin -> Sun -> Jool -> Tylo; the limiting SOI is Tylo's, not Jool's. The min-of-min
generalization is straightforward but adds search complexity; defer.

If a playtest shows this is still too generous or too tight, the loose/tight
analog of the existing A/B flag is a natural place to expose it. Out of
scope for v1 of this phase; default = the physics-derived MIN value.

---

## 5. UI impact

### 5.1 Period basis label

`BuildPeriodBasisLabel` (in MissionsWindowUI) today picks the dominant
constraint via `SelectDominantConstraintIndex` (longest period wins among Orbitals,
ties broken by index) and labels the period with its body, e.g. "~6h (Kerbin rot)" /
"~1.6d (Mun window)". For a Kerbin -> Duna mission the dominant Orbital is Duna's
heliocentric (longer period than Kerbin's heliocentric), so the label reads
"~Xy (Duna window)" -- correct.

Quirk for some short-target cases: a Kerbin -> Moho mission emits
{Rotation(Kerbin), Orbital(Moho ~2.2e6 s), Orbital(Kerbin ~9.2e6 s)}. Moho's
heliocentric period is SHORTER than Kerbin's heliocentric, so the existing dominant
selector picks Kerbin and the label reads "~Xy (Kerbin window)" -- which is technically
correct (the period IS Kerbin-year-bound) but misleading from a player perspective (the
player thinks of it as a "Moho mission" with a "Moho window"). Same shape for Eve in
some cases.

Fix proposal (small): in `SelectDominantConstraintIndex`, when two Orbitals are both
`RelativeToParent=true`, prefer the one whose `BodyName != launchBody` (the cross-parent
"target" half of the constraint pair). Only flips the label; the math (the picked k, the
resolved window) is identical because the constraint set is unchanged. Pure helper,
unit-testable. Add a `result.Constraints` walk + a `LaunchBodyName` comparison.

This is the same issue as Open Question 1 below; both refer here.

### 5.2 "Warp to..." button

Already in place from the zero-drift phase. The button fast-forwards 15s
before the next scheduled launch; for a Duna mission that "next" can be a
real year away. No behavior change; just exercises the existing path more.

### 5.3 "Time to launch" countdown

Already implemented. For a cross-parent mission it will read in years; the
formatter (`FormatCountdownCompact`) already handles long durations. No
change.

### 5.4 No new settings

The existing `TransitedBodyRotationMode` (Drop/Loose/Tight) automatically
covers a Duna landing, an Ike landing, etc. -- it gates `Rotation(B)` for any
non-launch body. Default `Loose` is the right starting point.

---

## 6. Backward-compatibility / gating (no regression)

- A same-body mission (no SOI crossing): no Orbital constraints emitted ->
  byte-identical to today.
- A same-parent (Kerbin -> Mun, Eve -> Gilly) mission: `launchToAnc` is empty,
  so step 3 of section 3.1 emits nothing. Only the target's `Orbital(B)`
  comes out -- byte-identical to today's same-parent case.
- A cross-parent mission: today this is `UnsupportedCrossParent` -> raw
  anchor / no-lock sentinel / "not aligned" UI. After this plan it becomes a
  multi-constraint zero-drift schedule. There is no migration concern
  because today's "not aligned" path didn't lay down any cached schedule.

**Feature flag gate location.** The new behavior is gated by a single
`enableCrossParentScheduling` flag in `ParsekSettings.cs` (boolean,
default **false** in Phase 4a, **true** in Phase 4b). The gate lives at exactly
ONE point inside `ExtractConstraints` (`MissionPeriodicity.cs`): the new emission
rule from §3.1 step 3 + step 4 wraps in
`if (bodyInfo.EnableCrossParentScheduling) { /* new emission */ }
 else { result.Support = Support.UnsupportedCrossParent; /* today's behavior */ }`.
Putting it in `ExtractConstraints` (not in `MissionLoopUnitBuilder`) means a flag
flip changes the EMITTED CONSTRAINT SET, which `BuildSignature` already hashes
(via the existing constraint digest), so the cached `LoopUnit` rebuilds correctly
when the flag flips at runtime.

**`BuildSignature` folding the flag.** `MissionLoopUnitBuilder.BuildSignature`
(via `AppendTransitedBodyDigest`) today folds the transited-body set, each body's
`OrbitPeriod`, `ReferenceBodyName`, `SoiRadius`, `OrbitalVelocity`. Two extensions
needed for cross-parent:

1. **Fold the flag itself.** Add `sb.Append("|crossParent=").Append(flag ? '1' : '0')`
   to the signature builder so a runtime flag toggle invalidates every cached LoopUnit
   immediately. Without this, the previously-emitted constraint set stays cached for
   the lifetime of the existing LoopUnit and the flag flip is silent.
2. **Fold the launch body's heliocentric data.** Today's digest scans the TRANSITED
   bodies only (via `OrbitSegments` body names + `StartBodyName`). The launch body is
   already in the digest via `StartBodyName`, so its `OrbitPeriod` and
   `ReferenceBodyName` ARE folded. Verified: no separate addition needed beyond the
   flag fold above. (If a future planet-pack swap changes Kerbin's heliocentric period
   without renaming "Kerbin", the digest captures it through `OrbitPeriod("Kerbin")`.)
3. **Planet-pack-adds-new-intermediate-body case.** If a planet pack inserts a new body
   in the chain (e.g., a Lagrangian barycenter between Earth and Sun), the digest only
   captures bodies that appear in `OrbitSegments` for THIS recording. A new
   intermediate that the recording never transits is not in the digest. For the
   cross-parent extractor's chain walk, the digest must additionally fold each
   chain-walked body (the `launchToAnc` + `targetToAnc` lists). Without this fold, the
   schedule could become stale across a planet-pack swap that introduced a new
   intermediate body. Add it via a second pass that walks
   `TryFindCommonAncestor(launchBody, body, ...)` for each transited body and appends
   intermediate bodies' `OrbitPeriod` + `ReferenceBodyName` to the digest.

- Planet-pack robustness: tested by passing a fake `IBodyInfo` with
  synthetic chains (RSS-shaped, OPM-shaped). All numerics flow through the
  seam; no hardcoded body names anywhere.

---

## 7. Diagnostic logging

Same `[Periodicity]` subsystem tag. The existing summary line includes
`launchBody`, `support`, `constraints=N`; cross-parent missions now appear
with `support=Supported` and a higher constraint count. Add to the per-
constraint dump (`LogSummary` builds it) the new `RelativeToParent=true`
discriminator so a log reader can tell at a glance whether the Orbital is
direct-child (Mun-around-Kerbin) or cross-parent (Duna-around-Sun).

Add a one-shot `Info` line when a cross-parent solve produces a window. Keep the
existing `[Periodicity]` ` key=value key=value` style (no parens, no mixed display
formatting in the machine-readable line):

```
[Periodicity] CrossParent SOLVED tree=<tree-id> launch=Kerbin bodies=Duna|Kerbin|Mun
  cadenceSeconds=ABC firstWindowUT=DEF firstWindowYearsFromNow=X.YZ
```

The `bodies=` field is a pipe-delimited list (parseable; matches existing
`AppendTransitedBodyDigest` style). Display annotations like "~Xy from now" go on a
SEPARATE Info line if useful at all (consumers parse the machine line, humans read the
display line). Matches the existing `PhaseLock APPLIED` / `Schedule built` style.

---

## 8. Test plan

All new tests pure (no FlightGlobals; use a hand-built fake `IBodyInfo`).
Three groups, plus an explicit test-fixture extension.

### 8.0 `StockFake` fixture extension

`MissionPeriodicityTests.StockFake()` today populates `Soi[]` + `Velocity[]` only for
Mun, Minmus, Duna. Cross-parent tests touch heliocentric data for Kerbin, Sun, and
deeper-chain bodies. Extend `StockFake` (or add a sibling `StockFakeCrossParent`) with:

- `Period["Kerbin"] = 9.2e6`, `Soi["Kerbin"] = 84_000_000`, `Velocity["Kerbin"] = 9285`,
  `Parent["Kerbin"] = "Sun"`.
- `Period["Duna"] = 1.74e7`, `Soi["Duna"] = 47_900_000`, `Velocity["Duna"] = 7915`,
  `Parent["Duna"] = "Sun"`.
- `Period["Ike"] = 6.5e4` (Ike around Duna), `Soi["Ike"] = 1_000_000`,
  `Velocity["Ike"] = 305`, `Parent["Ike"] = "Duna"`.
- `Period["Eeloo"] = 3.37e7`, `Soi["Eeloo"] = 1.19e8`, `Velocity["Eeloo"] = 4600`,
  `Parent["Eeloo"] = "Sun"`.
- `Period["Jool"] = 3.34e8`, `Soi["Jool"] = 2.46e9`, `Velocity["Jool"] = 4040`,
  `Parent["Jool"] = "Sun"`.
- `Period["Tylo"] = 2.11e5` (Tylo around Jool), `Soi["Tylo"] = 1.07e7`,
  `Velocity["Tylo"] = 2030`, `Parent["Tylo"] = "Jool"`.
- `Period["Moho"] = 2.22e6`, `Soi["Moho"] = 9.65e6`, `Velocity["Moho"] = 12,393`,
  `Parent["Moho"] = "Sun"`.
- `Period["Sun"] = 0` (root), `Parent["Sun"] = null`.

Without this fixture extension, `bodyInfo.SoiRadius("Kerbin")` /
`OrbitalVelocity("Kerbin")` return NaN and `ToleranceSecondsFor` silently falls back to
`c.PeriodSeconds * RotationToleranceFraction` (~6400 s for Kerbin's heliocentric year),
which is a meaningless number that happens to be roughly the right order of magnitude
for some cases. Tests asserting "within Kerbin-SOI tolerance" would PASS via that
fallback for the wrong reason. The plan must commit to this fixture extension as part of
Phase 4a; flagged here as a load-bearing test plan item, not an implementation detail.

### 8.1 Body-hierarchy walker

- `AncestorChain` for Sun, Kerbin, Mun, Duna, Ike, Laythe, Gilly.
- `TryFindCommonAncestor` for every pair in the section 2.2 table.
- Disconnected chains (a planet-pack with two unrelated roots): returns
  false, no exception.
- A body whose `ReferenceBodyName` returns null (the Sun): chain = ["Sun"];
  TryFind degenerate cases handled.

### 8.2 Extractor

- Kerbin -> Duna: emits the three constraints from section 3.2 in stable
  order; `Support = Supported`; no `UnsupportedCrossParent`.
- Kerbin -> Ike: emits the five constraints from section 3.3.
- Kerbin -> Mun -> Duna gravity assist: emits the four constraints from
  section 3.4, dedup verified (Kerbin emitted ONCE despite being in both
  Mun's launch-side and Duna's launch-side chains).
- Kerbin -> Mun (direct child, regression test): byte-identical to today.
- Kerbin orbit only (no SOI crossing): no Orbital emitted, byte-identical
  to today.
- Eve -> Gilly (launch body != Kerbin): emits Rotation(Eve) + Orbital(Gilly,
  around Eve); same-parent path.
- A rendezvous in the middle of a Duna mission still flips to
  `UnsupportedRendezvous` (this rule outranks; sanity check).
- **Round-trip Kerbin -> Duna -> Kerbin** (the last segment is a Kerbin landing): the
  existing surface-segment rule emits Rotation(Kerbin) at the EARLIEST surface start
  (collapsing two Kerbin surface arcs into one constraint, per `MissionPeriodicity.cs:380`
  "rotationBodiesSorted" loop), so the constraint set remains
  {Rotation(Kerbin), Orbital(Kerbin), Orbital(Duna)} -- same as a one-way Kerbin -> Duna
  mission. Regression test that the round-trip recording does NOT explode into duplicate
  Rotation(Kerbin) constraints or over-determine the schedule.
- **Feature flag off**: same extractor input that produces "Supported" with the flag on
  produces "UnsupportedCrossParent" with the flag off (Phase 4a regression gate).

### 8.3 Solver / schedule

- Kerbin -> Duna with stock periods: `TryBuildRelaunchSchedule` returns a
  schedule whose first window has Duna within Duna-SOI tolerance AND Kerbin
  within the `min(Kerbin-SOI, Duna-SOI)/velocity` tolerance from §4.4
  (~6050 s for stock); the second window is spaced by the resolved cadence;
  `MinIntervalSeconds >= span`.
- Cadence ordering: with the Loose A/B flag default the Duna-landing
  Rotation(Duna) is dropped to 5 deg and cadence is shorter than with
  Tight (0.25 deg); same monotonicity as the Mun case.
- `ScheduleLookaheadMultiples = 4096` is enough for Kerbin -> Duna (no clamp hit).
  For Kerbin -> Eeloo (longest constraint period ~3.37e7 s), the new
  `RecommendedLookaheadMultiples` returns ~12,500 (per §4.2 worked check), and the
  walk finds a within-tolerance window before the cap. Same for Kerbin -> Tylo
  (chain includes Jool's ~36-year heliocentric period; `RecommendedLookaheadMultiples`
  returns ~124,000 and the walk still completes in microseconds because the per-step
  cost is a CircularPhaseError check).
- `RecommendedLookaheadMultiples`: pin the worked-example values from §4.2 (Kerbin->Duna
  6,460 floor 4096 = 6460; Kerbin->Eeloo 12,500; Kerbin->Tylo 124,000; Kerbin->Mun 52
  floor 4096 = 4096). Unit-test as a pure function.

### 8.4 In-game canary

One in-game test in `RuntimeTests.cs`. The synthetic recording is hand-rolled directly
(NOT via `Source/Parsek.Tests/Generators/RecordingBuilder`, which is a Parsek.Tests
helper and does not link from the in-game project):

```csharp
[InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER)]
public IEnumerator CrossParent_KerbinToDuna_ExtractorResolvesToSupportedWithSchedule()
{
    // Synthesize a minimal Recording with three OrbitSegments crossing Kerbin -> Sun ->
    // Duna. Do not attempt to add it to RecordingStore; just feed it directly to
    // ExtractConstraints + TryBuildRelaunchSchedule via FlightGlobalsBodyInfo.
    var rec = new Recording { ... }; // body name strings only; no part snapshots needed
    rec.OrbitSegments = new List<OrbitSegment>
    {
        new OrbitSegment { bodyName = "Kerbin", startUT = 100, endUT = 5000, ... },
        new OrbitSegment { bodyName = "Sun",    startUT = 5000, endUT = 1_000_000, ... },
        new OrbitSegment { bodyName = "Duna",   startUT = 1_000_000, endUT = 1_005_000, ... },
    };
    var view = MakeMinimalViewForRec(rec);
    var compRoots = MakeMinimalCompRootsForRec(rec);
    var committed = new[] { rec };
    var excluded = new HashSet<string>();
    var result = MissionPeriodicity.ExtractConstraints(
        view, compRoots, committed, excluded, FlightGlobalsBodyInfo.Instance);
    InGameAssert.AreEqual(Support.Supported, result.Support, "cross-parent should resolve");
    InGameAssert.IsTrue(result.Constraints.Count >= 3, "expected >= 3 constraints");
    var schedule = MissionPeriodicity.TryBuildRelaunchSchedule(...);
    InGameAssert.IsTrue(schedule != null, "schedule must build");
    InGameAssert.IsTrue(double.IsFinite(schedule.FirstLaunchUT), "first window finite");
    yield break;
}
```

The minimal view / composition root helpers exist in the in-game test framework for
the existing periodicity tests (`InGameTests/...`); reuse them. This in-game test
validates wiring end-to-end against the live KSP body graph, including the
`FlightGlobals.Bodies` planet pack the user is running.

---

## 9. Phase breakdown (with review checkpoints)

The merged zero-drift PR did this in A/B/C/D phases with per-phase clean-
context Opus reviews. Same shape here.

**Phase 4a -- Pure math + extractor (one PR, behind a feature flag).**
- `AncestorChain` + `TryFindCommonAncestor` in `MissionPeriodicity.cs`.
- Extractor: the new emission rule from section 3.1.
- `Support.UnsupportedCrossParent` deprecated to a warning-only label
  (kept as an enum value for log compatibility; never returned by the
  extractor under the new path; old saves do not store it).
- Tests from sections 8.1 and 8.2.
- Feature flag `enableCrossParentScheduling` (settings file, default
  **false** for the first PR -- review-only).

**Phase 4b -- Solver wiring + UI (second PR).**
- `RecommendedLookaheadMultiples` helper + wire-up.
- Diagnostic logging additions.
- UI period-basis label routing (no new controls).
- Flip the feature flag default to **true**.
- Tests from sections 8.3 and 8.4.

**Phase 4c -- Hardening + edge cases (third PR, scope as discovered).**
- Whatever Phase 4b playtest surfaces: `LookaheadCoverageFactor` tuning if the cap
  bites for deep chains (note: `MaxJointMultiples` is the FIXED-CADENCE fallback
  constant and does not bite on cross-parent missions; the zero-drift path uses
  `ScheduleLookaheadMultiples` / `RecommendedLookaheadMultiples` instead),
  tolerance loosening if the §4.4 min-of-two-SOIs derivation is wrong in practice,
  Tylo-anchor-switch tie-break adjustment if §4.1's edge case bites a real player
  configuration, etc. Open as follow-up issues from the Phase 4b playtest log.

Each phase ends with a clean-context Opus review (math reviewer on Phase
4a; integration reviewer on Phase 4b). Same workflow as zero-drift.

---

## 10. What does NOT change

- Same-body and direct-child periodicity behavior (Mun, Minmus): byte-
  identical to today. The new path is gated entirely on the presence of a
  cross-parent body in `targetToAncestor`.
- The replay-as-is contract. We still REPLAY the recorded trajectory, never
  re-aim. The new scheduling math finds when the live sky matches; it does
  not compute new transfers.
- The Tracking Station / flight-map orbit-line + icon renderers. The recent
  body-frame continuity fix (`commit ca010093`) keeps the line continuously
  visible inside a body frame and blanks only at SOI / body changes; that
  is orthogonal to scheduling.
- The "Landing-body alignment" A/B flag (Drop/Loose/Tight). It already
  gates `Rotation(B)` for any non-launch body via `IsTransitedBodyRotation`;
  Duna landings and Ike landings flow through the same code path with no
  new code.
- The "Warp to..." button. It already targets the next scheduled relaunch;
  no new behavior.
- `UnsupportedRendezvous`. Still unsupported.
- The recording format. No new fields, no new serialization.

---

## 11. Open questions (decide before Phase 4a)

1. **For B's same-tonality label routing.** Same issue as §5.1's
   `SelectDominantConstraintIndex` quirk for Moho-class targets: when two Orbitals
   are both `RelativeToParent=true`, prefer the one whose `BodyName != launchBody`
   so the basis label reads "~Xy (Moho window)" instead of "~Xy (Kerbin window)".
   The fix is a small comparator tweak in `SelectDominantConstraintIndex`; PR-stage
   decision is whether to ship it bundled with Phase 4b's UI work or as a Phase 4c
   follow-up.

2. **Planet-pack swap detection.** A recording made in a stock save then loaded into
   an RSS save would have garbled heliocentric phase data. Today the periodicity
   solver assumes the recording's `ut0` matches the live solar system; cross-parent
   makes this more visible because the tolerance is tighter relative to the longer
   period. Mitigation: §6's `BuildSignature` folding extension (intermediate-body
   chain walk) catches the case where a planet pack introduces a new chain body.
   What it does NOT catch is a swap where stock body NAMES are reused for completely
   different orbits (an RSS "Kerbin" with Earth's heliocentric period). Pinned as
   out-of-scope for v1; document the limitation in the UI's "Landing-body alignment"
   tooltip.

3. **Continued-fraction acceleration?** The Phase 4 todo line mentions
   "continued-fraction / Stern-Brocot best-rational-approximation" as a
   future improvement to the brute-force k-walk. Not in scope for v1; the
   existing walk works at 4096 multiples in microseconds, and the new
   `RecommendedLookaheadMultiples` formula keeps the walk's COST microsecond-bounded
   even at 124k iterations for Kerbin -> Tylo. Keep as a future optimization if a
   planet-pack ships periods that push the per-frame budget.

4. **In-flight extraction during the recording itself?** The extractor
   reads completed `OrbitSegments` from a committed recording. A live (in-
   progress) recording does not yet have its full body-transit list, so
   the cross-parent path activates only AFTER the mission seals. This is
   the same as today's same-parent path; no change.

5. **§4.1 Tylo-anchor-switch acceptance.** Does the Tight-mode anchor switch from
   launch pad to Tylo (per §4.1) need an "anchor-on-launch-body" preference flag,
   or is it acceptable to playtest as-is? Pin before Phase 4b.

6. **§4.4 launch-body heliocentric tolerance.** The `min(launchSOI/V, targetSOI/V)`
   choice is a defensible heuristic, not derived physics. The fully derived value
   would require integrating the SOI sweep along the recorded heliocentric arc
   (~3 days of work, not justified for v1). Pin: ship the min-of-two heuristic,
   widen via Phase 4c if playtest shows the tolerance is wrong.

---

## 12. References

- `Source/Parsek/MissionPeriodicity.cs` (extractor + solver + zero-drift schedule)
  - `IBodyInfo` (lines 212-229)
  - `ExtractConstraints` rule 4 (lines 420-453)
  - `IsSameParentTarget` (lines 1374-1380)
  - `FindBestJointMultiple` / `JointStepResidual` (lines 700-738)
  - `SelectAnchorConstraintIndex` / `ScheduleToleranceSecondsFor` /
    `TryBuildRelaunchSchedule`
- `Source/Parsek/MissionLoopUnitBuilder.cs` (where the schedule attaches)
- `docs/dev/plans/zero-drift-reschedule.md` (the math + schedule contract)
- `docs/parsek-logistics-supply-routes-design.md` §8.1 (body-hierarchy walker sketch)
- `docs/dev/todo-and-known-bugs.md`:
  - line 92: the "TODO - Phase 4" entry this plan replaces.
  - line 80: Phase 1 wiring (single-constraint solver + `UnsupportedCrossParent`
    flag introduced).
  - line 88: the `TransitedBodyRotationMode` A/B flag (transfers to Duna /
    Ike landings naturally).
  - line 92's "warp-to-window quick-win" -> already done; pairs with this.
