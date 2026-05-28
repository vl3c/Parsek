# Cross-parent / interplanetary body support (Phase 4)

Status: IMPLEMENTED (shipped as one PR, internal phases A/B/C). Built on top of the
merged zero-drift reschedule (`docs/dev/plans/zero-drift-reschedule.md`).

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
- 2026-05-28 SECOND clean-context Opus review folded (ship-as-one-PR decisions):
  - DROPPED the `enableCrossParentScheduling` feature flag entirely. It routed through
    `bodyInfo.EnableCrossParentScheduling`, a property that does not exist on `IBodyInfo`
    and is the wrong seam (geometry data, not settings). Shipping ON in a single PR makes
    the false-then-true staged gate unnecessary ceremony. Cross-parent is now always
    `Support.Supported`. `BuildSignature`'s transited-body digest already hashes the
    constraint inputs, so there is no caching hazard. (was §6 / C2 / B)
  - Launch-heliocentric Orbital uses the BODY-ONLY tolerance (`ToleranceSecondsFor` as-is)
    for v1. The `min(launchSOI/V, targetSOI/V)` refinement needs target identity threaded
    into a body-name-keyed tolerance function and is, by §11 Q6's own admission, a heuristic;
    deferred to a Phase-4c follow-up. (was §4.4 / C3)
  - ADDED the upper period cap (scope item 5, which the first draft wrongly dropped):
    a documented 50-(launch-body-year) cap, applied in the UI DISPLAY layer only, that
    tints the cell amber + shows "next launch in ~N y" while KEEPING the "Warp to..."
    button reachable (the schedule still resolves a finite UT). Never in the solver. (§5.5)
  - Emission keeps the existing DETERMINISTIC sorted iteration (start-UT then ordinal),
    not the raw-dict `foreach` the pseudocode showed; chain-walked intermediates are
    sorted too. (M1)
  - `RecommendedLookaheadMultiples` is computed AFTER `SelectAnchorConstraintIndex` off the
    ACTUAL selected anchor period. (M4)
  - The `BuildSignature` intermediate-body fold (old §6 point 3) is defensive-only and is
    NOT implemented: the existing digest already folds every body the recording physically
    transits (you cannot reach Ike without entering Duna's SOI), which is every body the
    chain walk emits a constraint for. (M3)
  - Log subsystem tag is `MissionPeriodicity` (the existing tag), not `[Periodicity]`. (N4)
  - `StockFake` extended to cover Eve, Gilly, Dres, Vall, Bop, Pol too (all four stock
    topological shapes, every reachable body). (J)
  - `SelectDominantConstraintIndex` gains an optional `launchBodyName` to prefer the
    cross-parent TARGET body over the launch body for the basis label (label-only; does
    not touch the duty-cycle `SelectAnchorConstraintIndex`, so the schedule is unchanged).
- 2026-05-28 THIRD clean-context Opus review folded (the plan's load-bearing error):
  - The first two drafts conflated the SYNODIC period (~2.1 yr, relative geometry, the
    re-aim quantity) with the true REPLAY-AS-IS recurrence (both planets back at their
    recorded ABSOLUTE positions), which for Kerbin -> Duna is ~1142 Kerbin years (~487k pad
    steps). The shipped look-ahead (`8 * longestPeriod / anchor` ~= 6.4k steps, ceiling 262k)
    stopped ~80x short of the true window, so cross-parent schedules silently returned a
    BOUNDED-BEST launch within ~15 years where the target is wherever it happens to be (the
    replayed transfer flies to empty space). See §4.2 / §4.3 (now corrected).
  - FIXED `RecommendedLookaheadMultiples` to size the horizon to the JOINT-coincidence
    expectation `coverage * product_j(P_j / (2*tol_j))` over the non-anchor constraints (in
    anchor steps; signature changed to take the periods + tolerances), raised
    `MaxLookaheadMultiples` 262144 -> 1048576 (~2450 Kerbin yr, spans every stock 2-3 body
    first window), `LookaheadCoverageFactor` 8 -> 3 (geometric-tail margin). Same-parent
    floors to 4096 (byte-identical). Now Kerbin -> Duna FINDS a true within-tolerance window
    (the transfer reaches Duna), centuries out, amber-capped + Warp-reachable.
  - Kept the locked "over-constrained configs are NEVER refused" decision: a config whose
    window is still beyond the (generous) horizon falls to bounded-best amber, NOT "not
    aligned" + Warp-disabled. The honesty comes from the horizon now finding the real window
    for every realistic case, not from refusing.
  - Signature-gated the Missions-tab `GetLoopUnitSet` rebuild (was per-frame) on
    `BuildSignature`, and skip the schedule constructor's eager 8-launch probe when the first
    launch is bounded-best-only, so the larger search is paid once per config change, not
    every frame.

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

### 4.2 Zero-drift schedule + the look-ahead horizon (CORRECTED after a third review)

`TryBuildRelaunchSchedule` walks k integer multiples of the anchor period (the launch pad)
and accepts the first k whose residuals fit every non-anchor constraint within its
tolerance, else the bounded-best k in the look-ahead window. The cross-parent path always
takes this zero-drift route (>= 2 distinct-period constraints once a heliocentric layer is
added). `MaxJointMultiples = 16` governs ONLY the fixed-cadence fallback and does not bite
cross-parent missions.

**The look-ahead horizon was the load-bearing mistake in the first two drafts (see §4.3).**
A faithful launch needs EVERY non-anchor constraint within tolerance at the SAME k. As k
scans, a constraint with period `P_j` (incommensurate with the anchor) lands within `tol_j`
for a fraction `~2*tol_j/P_j` of k's, so the EXPECTED number of anchor steps until ALL of
them coincide is:

```
E[k] ~= product_j ( P_j / (2 * tol_j) )    over the NON-anchor constraints
```

This is the rare ABSOLUTE joint coincidence, NOT "a few cycles of the longest period" and
NOT the synodic period. The corrected helper sizes the horizon to it:

```csharp
internal const double LookaheadCoverageFactor = 3.0;     // geometric-tail margin
internal const int MinLookaheadMultiples = ScheduleLookaheadMultiples; // 4096 floor
internal const int MaxLookaheadMultiples = 1048576;       // ~2450 Kerbin yr ceiling

internal static int RecommendedLookaheadMultiples(
    IReadOnlyList<double> otherPeriods, IReadOnlyList<double> otherTolerances)
{
    if (otherPeriods == null || otherPeriods.Count == 0) return MinLookaheadMultiples;
    double product = 1.0;
    for (int i = 0; i < otherPeriods.Count; i++)
    {
        double p = otherPeriods[i];
        double tol = (otherTolerances != null && i < otherTolerances.Count) ? otherTolerances[i] : 0.0;
        if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0 || tol <= 0.0) continue;
        double factor = p / (2.0 * tol);
        if (factor > 1.0) product *= factor;     // tol >= P/2 -> always satisfied -> no rarity
    }
    double recommended = LookaheadCoverageFactor * product;
    return clamp(recommended, MinLookaheadMultiples, MaxLookaheadMultiples);
}
```

The horizon is in anchor STEPS and is independent of the anchor period magnitude (the duty
cancels it). Worked numbers (stock, anchor = pad, tol = SoiRadius/OrbitalVelocity):

- Kerbin -> Duna: Duna `P/2tol ~= 1024`, Kerbin `~= 508`; product `~= 520k` anchor steps; the
  first true window is at k ~= 487,737 ~= **1142 Kerbin years**. With `LookaheadCoverageFactor=3`
  -> ~1.56M, clamped to the 1.05M ceiling, which still SPANS the 487k first window -> the
  schedule FINDS a true within-tolerance launch (the transfer reaches Duna).
- Kerbin -> Tylo: ~865k first window (~2026 Kerbin yr) -> within the 1.05M ceiling -> found.
- Kerbin -> Moho: ~722k -> found. Kerbin -> Eeloo: ~330k -> found.
- Kerbin -> Mun (same-parent): product `~= 16` -> floored to 4096 (byte-identical to today).

`MaxScheduleSteps = 8192` (the launch-cache cap) is unaffected. The k-walk is a single
`CircularPhaseError` per other-constraint per step; even a full 1.05M-step bounded-best scan
is a few ms, run only on a config rebuild (signature-gated in BOTH the scene drivers AND the
Missions UI - the UI's per-frame `GetLoopUnitSet` is now gated on `BuildSignature`, not just
the frame, so the large search is not paid every frame). The eager 8-launch interval probe in
the schedule constructor is SKIPPED when the first launch is bounded-best-only (no true window
in range), so an unreachable config costs ONE scan, not eight.

### 4.3 Why NOT the synodic period (the corrected category error)

An earlier draft claimed the cadence is "2x-3x the synodic period (~2.1 yr for Kerbin-Duna)".
That was a CATEGORY ERROR. The synodic period is when Kerbin and Duna return to the same
RELATIVE geometry (same phase angle) - which is what you use to fly a FRESH transfer (re-aim,
out of scope). REPLAY-AS-IS needs both planets back at their recorded ABSOLUTE inertial
positions (the recorded transfer ellipse is fixed in the Sun frame; the ghost leaves Kerbin's
recorded departure point and arrives at Duna's recorded encounter point). That absolute double
coincidence recurs on the order of `product(P/2tol)` ~ **centuries-to-millennia** (Kerbin-Duna
~1142 yr), NOT ~2.1 yr. The first two drafts sized the look-ahead (and the 50-year display cap)
against the synodic figure, so the shipped code would have stopped ~80x short of the true
window and silently returned a bounded-best launch where Duna is wherever it happens to be (the
transfer flies to empty space). The fix above sizes the horizon to the true coincidence so the
schedule finds a faithful (rare, Warp-reachable) window; the 50-year display cap then correctly
flags it as "very rare" (every real interplanetary window is well past 50 yr).

We still do not emit any `SynodicPeriod` quantity; the math is "emit the right constraints, run
the joint zero-drift solver" - just with the horizon sized to the absolute coincidence.

### 4.4 Tolerance for the LAUNCH body's heliocentric Orbital

**v1 decision (second review):** the new `Orbital(launchBody, around commonAncestor)`
constraint uses the BODY-ONLY tolerance, i.e. the existing `ToleranceSecondsFor`
(`SoiRadius(launchBody) / OrbitalVelocity(launchBody)`), with NO special-casing. The
`min(launchSOI/V, targetSOI/V)` refinement below is deferred to Phase 4c because the
per-constraint tolerance functions are keyed purely on body name + kind and have no way to
know "this Orbital is the launch heliocentric leg paired with target X" without a new
`PhaseConstraint` field; and the min-of-two value is, per §11 Q6, a heuristic, not derived
physics. The body-only launch tolerance (~9050 s for stock Kerbin) is a defensible
conservative-ish starting point. The rest of this section is retained as the Phase-4c
design for that refinement.

The min-of-two refinement (Phase 4c): `Orbital(launchBody, around commonAncestor)` would
use `min(SoiRadius(launchBody)/OrbitalVelocity(launchBody), SoiRadius(targetBody)/OrbitalVelocity(targetBody))`
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

### 5.5 Upper period cap (scope item 5)

A cross-parent schedule can legitimately resolve a first window years out (Kerbin -> Jool
moons run into multi-Kerbin-year synodics). That is physically correct and the schedule
still builds, but a live "T- 7y 12d" countdown is more confusing than useful, and a
pathological planet-pack period could push it absurdly far. So we apply a DISPLAY-LAYER cap:

- Threshold: `MaxDisplayableScheduleSeconds = CrossParentMaxRelaunchYears * launchBodyYear`,
  where `CrossParentMaxRelaunchYears = 50` and `launchBodyYear = OrbitPeriod(launchBody)`
  read live through the seam (planet-pack safe; a homeworld whose parent is the Sun has a
  real year). When the launch body has no orbit (it IS the root, degenerate), fall back to
  an absolute `50 * 9_203_545 s` stock-Kerbin-year constant so the cap is never NaN.
- When the engine's next relaunch (`NextRelaunchUT - now`) exceeds the cap, the "Time to
  launch" cell shows an amber "next launch ~N y" label (still the real number, just flagged
  as very rare) instead of a precise countdown. The amber reuses `LoopPeriodClampColor`.
- The "Warp to..." button STAYS ENABLED: the schedule resolved a finite future UT, so warp
  is the intended escape hatch for the long wait (task scope item 2 + 4). The cap is a
  readability signal, NOT a "not aligned" fallback. A config that genuinely cannot resolve a
  finite window (safety cap hit -> `NextLaunchAfter` returns NaN) still reads the existing
  "not aligned" with Warp disabled, unchanged.
- Pure helpers (unit-tested): `IsScheduleBeyondDisplayCap(nextRelaunchUT, nowUT, capSeconds)`
  and the cap derivation. Both live in `MissionsWindowUI` next to the other pure display
  helpers. `CrossParentMaxRelaunchYears = 50` is a documented tunable.

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

**No feature flag (second review decision).** The earlier draft gated the new emission on
`enableCrossParentScheduling` routed through `bodyInfo.EnableCrossParentScheduling`. That
property does not exist on `IBodyInfo`, and the seam is a celestial-geometry reader, not a
settings carrier. Since the feature ships ON in a single PR, the staged false-then-true gate
is unnecessary. **The flag is dropped.** `ExtractConstraints` always runs the new emission;
cross-parent is always `Support.Supported`. No `ParsekSettings` flag, no UI toggle, no
signature fold for it.

**`BuildSignature` is already sufficient.** `MissionLoopUnitBuilder.BuildSignature`
(via `AppendTransitedBodyDigest`) today folds the transited-body set, each body's
`OrbitPeriod`, `ReferenceBodyName`, `SoiRadius`, `OrbitalVelocity`. For cross-parent:

1. **Launch body's heliocentric data is already folded.** The digest scans `OrbitSegments`
   body names + `StartBodyName` + `SegmentBodyName`. The launch body is in the digest via
   `StartBodyName`, so its `OrbitPeriod` / `ReferenceBodyName` / `SoiRadius` /
   `OrbitalVelocity` ARE folded. No addition needed.
2. **Intermediate chain bodies are already folded for realistic recordings.** Under the
   replay-as-is contract you cannot reach Ike without physically entering Duna's SOI, so
   Duna already appears in the recording's `OrbitSegments` and thus in the digest. Every
   body the chain walk emits a constraint for is a body the recording transits, which the
   digest already captures. The "fold each chain-walked body" second pass the earlier draft
   proposed is therefore NOT implemented (it would only matter for a body on the chain that
   the recording never transits, which the replay-as-is contract makes impossible).

- Planet-pack robustness: tested by passing a fake `IBodyInfo` with
  synthetic chains (RSS-shaped, OPM-shaped). All numerics flow through the
  seam; no hardcoded body names anywhere.

---

## 7. Diagnostic logging

Subsystem tag `MissionPeriodicity` (the existing tag the module uses everywhere, e.g.
`MissionPeriodicity.cs:345` / `:1194`; NOT `[Periodicity]`). The existing summary line
includes `launchBody`, `support`, `constraints=N`; cross-parent missions now appear with
`support=Supported` and a higher constraint count. The per-constraint dump (`PhaseConstraint.ToString`,
used by `LogSummary`) already prints `same-parent` / `cross-parent` for Orbital constraints
(`MissionPeriodicity.cs:66-69`), which covers the direct-child vs cross-parent discriminator;
no change needed there.

Add a one-shot `Info` line when a cross-parent solve produces a window. Keep the existing
`key=value key=value` style (no parens, no mixed display formatting in the machine-readable
line):

```
[Parsek][INFO][MissionPeriodicity] CrossParent SOLVED tree=<tree-id> launch=Kerbin bodies=Duna|Kerbin
  cadenceSeconds=ABC firstWindowUT=DEF
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
- `Period["Moho"] = 2.22e6`, `Soi["Moho"] = 9.65e6`, `Velocity["Moho"] = 12393`,
  `Parent["Moho"] = "Sun"`.
- `Period["Eve"] = 5.66e6`, `Soi["Eve"] = 8.51e7`, `Velocity["Eve"] = 10811`,
  `Parent["Eve"] = "Sun"`.
- `Period["Gilly"] = 3.88e5` (around Eve), `Soi["Gilly"] = 1.26e5`,
  `Velocity["Gilly"] = 70`, `Parent["Gilly"] = "Eve"`.
- `Period["Dres"] = 4.73e7`, `Soi["Dres"] = 3.27e7`, `Velocity["Dres"] = 4630`,
  `Parent["Dres"] = "Sun"`.
- `Period["Vall"] = 1.05e5` (around Jool), `Soi["Vall"] = 2.41e6`,
  `Velocity["Vall"] = 2650`, `Parent["Vall"] = "Jool"`.
- `Period["Bop"] = 5.45e5` (around Jool), `Soi["Bop"] = 1.22e6`,
  `Velocity["Bop"] = 765`, `Parent["Bop"] = "Jool"`.
- `Period["Pol"] = 9.02e5` (around Jool), `Soi["Pol"] = 1.04e6`,
  `Velocity["Pol"] = 645`, `Parent["Pol"] = "Jool"`.
- Tidally-locked moons (`RotationPeriod == OrbitPeriod`): Mun, Tylo, Bop, Pol, Vall, Gilly
  in stock. Set `Rotation[b] = Period[b]` for those so the tidal-collapse path is exercised.
- `Period["Sun"] = 0` (root), `Parent["Sun"] = null`.

This covers all four stock topological shapes: sibling-of-Kerbin (Moho, Eve, Duna, Dres,
Jool, Eeloo), Eve-moon (Gilly), Duna-moon (Ike), Jool-moon (Laythe, Vall, Tylo, Bop, Pol),
plus the same-parent baselines (Mun, Minmus from Kerbin; Gilly from Eve).

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

Shipped as ONE PR (cross-parent on by default), built in three internal phases each with
its own clean-context review, mirroring the zero-drift workflow.

**Phase A -- Pure math + extractor (with intermediate review).**
- `AncestorChain` + `TryFindCommonAncestor` in `MissionPeriodicity.cs`.
- Extractor: the new emission rule from section 3.1 (deterministic sorted emission, M1).
- `Support.UnsupportedCrossParent` retired from the extractor's return paths (kept as an
  enum value for log/back-compat; never returned for a resolvable cross-parent body).
- `StockFake` fixture extension (§8.0) + tests from sections 8.1 and 8.2.
- Clean-context Opus math review before Phase B.

**Phase B -- Solver wiring + signature + in-game test.**
- `RecommendedLookaheadMultiples` helper, computed AFTER `SelectAnchorConstraintIndex`
  off the selected anchor period (M4), wired into `TryBuildRelaunchSchedule`.
- Confirm `Solve` / `TryBuildRelaunchSchedule` / `BuildSignature` pick up the new
  constraints with no further change (the transited-body digest already covers them).
- Diagnostic logging additions (the `CrossParent SOLVED` Info line).
- Tests from section 8.3 + the in-game canary (8.4).
- Intermediate review.

**Phase C -- UI + cap + docs.**
- UI period-basis label routing (the `SelectDominantConstraintIndex` target-body preference).
- The §5.5 upper period cap (amber "next launch ~N y", Warp stays enabled).
- Plan/CHANGELOG/todo updates.
- Final clean-context Opus review of the whole PR.

**Deferred to a Phase-4c FOLLOW-UP (out of this PR):** the §4.4 `min(launchSOI/V, targetSOI/V)`
launch-heliocentric tolerance refinement; the §4.1 Tylo Tight-mode anchor-on-launch-body
preference flag; `LookaheadCoverageFactor` tuning if a planet-pack deep chain hits the cap.
Open as follow-up issues from the playtest log. (Note: `MaxJointMultiples` is the FIXED-CADENCE
fallback constant and does not bite cross-parent missions; the zero-drift path uses
`ScheduleLookaheadMultiples` / `RecommendedLookaheadMultiples` instead.)

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
