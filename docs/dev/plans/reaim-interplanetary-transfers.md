# Re-aim interplanetary transfers (looped missions that actually repeat)

Status: DESIGN (review folded). Phase 1a (window math) + LCA salvage landed on the branch; Lambert +
Phase 2+ in progress.
Branch: `reaim-transfers` (off `origin/main`).
Supersedes the unmergeable faithful-cross-parent PR #968 (kept open as reference only; its
body-hierarchy walk is salvaged here, the rest is replaced).

Revision history:
- 2026-05-28 initial design, after a deep dive into stock KSP orbital math + the cloned mods.
- 2026-05-28 folded a clean-context design review (3 CRITICAL + 3 MAJOR + 4 MINOR), verified against
  decompiled stock source. Load-bearing corrections:
  - **S1 (ejection) is NOT a recorded segment.** OrbitSegments are captured only on-rails; the
    ejection burn is flown off-rails and recorded as Absolute Points. S1 is PURE SYNTHESIS from the
    recorded parking-orbit (end of S0) + the required departure velocity. (C1)
  - **The recorded heliocentric leg (S2) and target-arrival leg (S3) exist as OrbitSegments only if
    the player time-warped (on-rails) through them**; a background coast yields no segment. The
    classifier REQUIRES a Sun-bodied S2 + target-bodied S3 and BAILS to faithful/not-applied when S2
    is absent (never half-applies). (A, m4)
  - **`PatchedConics.CalculatePatch` target-encounter promotion is GATED** (approach-marker setting /
    background-thread / patch limit). Phase 2 must pin the `SolverParameters` and the in-game canary
    must assert the Duna encounter populates under DEFAULT settings, else it is a blocker. (C2)
  - **The `ReaimedTrajectory` adapter must be EAGER-cached per window** (synthesis at window-advance,
    getter is a pure field return) - `OrbitSegments` is read on the per-frame hot path, so a lazy
    getter would re-introduce the prior per-frame-solve bug. (C3)
  - **PartEvent / FX remap is a CORRECTNESS item, not cosmetic.** `PartEvent.ut` /
    `orbitSegmentStartUT` bind events to specific recorded segments; with S1/S2 re-synthesized those
    bindings break (event resolves against the wrong/out-of-range segment). The adapter must remap or
    suppress ejection-window PartEvents onto the synthesized spans. (M1)
  - **The synodic schedule PRODUCER is new work** - `TryBuildRelaunchSchedule` is anchor-period /
    tolerance based (the faithful model) and is NOT reused; only the UT-list CONSUMER (span clock) is
    shared. (M2)
  - **tof is solved per window, not transplanted.** Feeding the recorded tof to a different window's
    geometry can yield a wildly eccentric/retrograde conic; default to the Hohmann tof for the
    window's actual r1/r2, validate the solved transfer's energy/eccentricity, reject + step to the
    next window if degenerate. (M3)
  - **v1 stitching = anchor to the recorded SOI-EXIT STATE** (keep S0 + parking verbatim; at the
    recorded SOI-exit moment swap only the post-exit velocity to the Lambert v1), so the seam is a
    velocity discontinuity at the SOI edge (far from camera, expected at SOI crossings) instead of a
    plane-change kink in low orbit. (review recommendation)
  - Lambert port: Gooding.cs is ~577 lines + `V3`/`Statics` deps + the in-file XLAMB/TLAMB root-finder
    (MechJeb is upstream GitHub, NOT cloned here); license confirmed permissive. Given the size +
    dependency web, v1 hand-rolls a self-contained universal-variable (Bate-Mueller-White) Lambert we
    own, with the M3 energy/eccentricity validate-and-skip covering the robustness edges; Gooding stays
    a documented fallback if UV proves fragile in playtest. (m1)
  - LCA walk already salvaged into this branch (commit on `reaim-transfers`); KSPTrajectories
    CalculatePatch citation corrected to the decompiled stock source (the mod call passes
    targetBody=null). (m2, m3)

References (read before implementing):
- `docs/dev/plans/mission-periodicity-phases.md`, `docs/dev/plans/zero-drift-reschedule.md`,
  `docs/dev/plans/cross-parent-bodies.md` (the faithful-replay arc this replaces for cross-parent).
- `docs/dev/design-mission-periodicity.md` (the locked decisions, several of which this DOC
  deliberately RE-OPENS, see section 1).
- `docs/parsek-logistics-supply-routes-design.md` (the consumer: routes need a usable cadence).
- Deep-dive sources: MechJebLib `Gooding.cs` (Lambert), KerbalAlarmClock `Utilities.cs` /
  `TimeObjects.cs` (phase-angle), KSPTrajectories `MapOverlay.cs` (synthesized-line render
  technique), the stock `Orbit` / `PatchedConics` / `CelestialBody` APIs.

---

## 1. Why this exists (and what it re-opens)

The merged mission-periodicity feature relaunches a looped mission only at "faithful" windows
where the live sky matches the recording, REPLAYING the recorded trajectory verbatim. For a body
orbiting the launch body (Mun, Minmus) that works well: faithful windows recur every few days.

For an interplanetary target (Duna, Eve, Jool, ...), faithful replay is a dead end. Replaying the
exact recorded heliocentric transfer requires BOTH the launch body and the target back at their
recorded ABSOLUTE positions simultaneously - a coincidence that recurs roughly every **1142
Kerbin years** for Kerbin -> Duna (proven in the cross-parent review). PR #968 computed that window
correctly and flagged it amber, but a ~1142-year cadence is useless for actual play or for
logistics routes. The synodic period people think of (~2 years, "the real launch window") is only
the RELATIVE geometry recurrence - which is exactly what you use to fly a FRESH transfer, not to
replay a fixed one.

So this feature does the thing the periodicity design explicitly locked OUT: **re-aim**. Each loop,
instead of replaying the recorded inertial transfer, it RE-PLANS the heliocentric transfer to
intercept the target's CURRENT position (a Lambert solve), so the ghost reliably departs the launch
body and arrives at the destination every transfer window (~synodic cadence). This is the only model
that gives interplanetary looped missions (and logistics routes built on them) a usable cadence.

**Locked decisions re-opened (by product-owner direction):**
- "Replay as-is; do not re-aim" -> **re-opened.** Cross-parent missions now re-aim. Same-parent
  missions are UNCHANGED (still faithful replay).
- The "faithful-only, no decorative mode" stance -> refined to **auto-by-target** (below).

**Decisions locked for THIS work (product owner):**
1. **Activation = auto-by-target.** A mission whose target shares the launch body's parent only at
   the root (cross-parent: Duna/Eve/Jool/...) auto-uses re-aim. A same-parent target (Mun/Minmus;
   Gilly from Eve) keeps the existing faithful-replay periodicity. No user toggle in v1; the mode is
   implied by the destination (the same LCA test #968 already computes).
2. **First-PR scope = single-hop, orbital arrival.** Kerbin (or any launch body) -> one
   cross-parent target, ONE heliocentric transfer, arrival to the target's SOI / capture orbit.
   DEFERRED: gravity assists, multi-hop chains, aerocapture, and replaying a surface LANDING after
   arrival (orbital arrival only in v1; the recorded capture/orbit arc replays, a recorded descent
   does not yet re-stitch).
3. **#968 = abandoned**, not merged. Salvage only `AncestorChain` / `TryFindCommonAncestor` (the
   pure body-hierarchy walk) + the `IBodyInfo` seam; leave #968 open as reference until this lands,
   then close it.

---

## 2. The segment model (what makes re-aim tractable)

Re-aim is NOT "throw away the recording." A recorded interplanetary mission is a chain of phases
split by SOI. **Crucial recorder caveat (review C1/A/m4, verified against decompiled source):**
`OrbitSegment`s are captured only while the vessel is ON-RAILS (time-warp). So the recorded
KEPLERIAN inputs re-aim can read are the on-rails heliocentric coast (S2, Sun-bodied) and the
on-rails arrival (S3, target-bodied) - and ONLY if the player time-warped through them (a background
coast yields no segment at all). The powered ascent (S0) and ejection burn (S1) are flown OFF-rails
and recorded as Absolute `TrajectoryPoint`s, not OrbitSegments - so S1 is **not extractable**; it is
synthesized from scratch.

| Phase | Frame | Recorded as | Re-aim treatment |
|-----|-------|------------|------------------|
| **S0** | launch-body surface + inertial | Absolute points (off-rails) + parking-orbit OrbitSegment if warped | **REPLAY as-is**; the recorded parking orbit (+ its SOI-exit STATE) is the only input S1 needs |
| **S1** | launch-body inertial | NOT a recorded segment (off-rails burn) | **PURE SYNTHESIS** - no recorded counterpart; build the ejection so the post-SOI-exit state matches the Lambert v1 (see section 4) |
| **S2** | common-ancestor (Sun) inertial | Sun-bodied OrbitSegment (on-rails only) - **REQUIRED**; classifier bails to faithful if absent | **RE-SYNTHESIZE** (Lambert solve to the target's current position) - the core of re-aim |
| **S3** | target inertial | target-bodied OrbitSegment (on-rails only) | **REPLAY as-is** (target-relative, placed at the synthesized SOI-entry UT) |
| **S4** | target surface | Absolute/surface points | **DEFERRED** (v1 stops at orbital arrival; re-stitching a descent to a re-aimed arrival is Phase-later) |

So v1 keeps your real launch (S0) and your real arrival orbit (S3), and recomputes the connecting
transfer (S2, Lambert) + the ejection (S1, pure synthesis) for the chosen window. The destination
changes where it is each window; we rebuild the bridge to it. **The classifier requires a Sun-bodied
S2 OrbitSegment to engage re-aim at all; if the recording has none (never warped / background coast),
re-aim is NOT applied and the mission stays on the existing faithful path - never half-applied.**

Segment classification reuses #968's LCA walk: the launch body is the earliest recorded body; the
heliocentric leg is the segment(s) whose body is the lowest common ancestor of launch + target; the
target is the body entered after it. (Salvaged: `AncestorChain` / `TryFindCommonAncestor`.)

---

## 3. The pipeline

Per looped re-aim mission, per window:

```
1. WINDOW          next departure UT t_dep (synodic / phase-angle); tof = Hohmann time for THIS
                   window's r1/r2 (NOT the recorded tof, review M3); t_arr = t_dep + tof
2. ENDPOINTS       r1 = launchBody.orbit pos at t_dep ; r2 = target.orbit pos at t_arr   (heliocentric)
3. LAMBERT         v1 = UvLambert.Solve(mu_sun, r1, r2, tof, prograde) ; VALIDATE energy/ecc, else
                   refine t_arr / step to next window (review M3)
4. TRANSFER CONIC  Orbit S2 = UpdateFromStateVectors(swizzle(r1), swizzle(v1), Sun, t_dep)
5. SOI PATCH       CalculatePatch(S2, nextPatch, t_dep, SolverParameters{...}, target)
                   -> if ENCOUNTER: S2.EndUT = target-SOI-entry UT ; nextPatch = target hyperbola
                   -> else: step to next window (review C2: encounter promotion is gated; pin params)
6. EJECTION CONIC  v_inf = v1 - v_launchBody ; synthesize S1 so its launch-body SOI-exit STATE matches
                   v1 (anchor to the recorded SOI-exit position; do NOT plane-change in low orbit -
                   review stitching rec, section 4)
7. ASSEMBLE        synthesized segment list: [S0 recorded] [S1 ejection] [S2 transfer]
                   [S3 recorded arrival, re-anchored to S2.EndUT, target-relative]
8. PRESENT         wrap the Recording in a ReaimedTrajectory (IPlaybackTrajectory) whose
                   OrbitSegments = the assembled list; the engine renders it unchanged
```

### 3.1 What is stock, what we port, what we build

| Piece | Source | License | Notes |
|-------|--------|---------|-------|
| Planet position/velocity at UT (steps 2) | stock `CelestialBody.orbit.getRelativePositionAtUT` / `getOrbitalVelocityAtUT` | - | `.xzy` swizzle; `getTruePositionAtUT` for absolute |
| Lambert solve (step 3) | **hand-rolled universal-variable (Bate-Mueller-White) Lambert, OURS** | n/a (own code) | single-rev prograde; ~150 lines, pure, unit-testable vs textbook solutions; the M3 energy/eccentricity validate-and-skip covers the near-180/short-tof edges. (MechJebLib `Gooding.cs` is the permissive fallback - confirmed `Unlicense/CC0/MIT-0/MIT`, but it is ~577 lines + `V3`/`Statics` deps + the in-file XLAMB/TLAMB root-finder, and MechJeb is upstream GitHub, NOT cloned here - so UV is the smaller owned choice for v1.) |
| Build conic from state vector (step 4) | stock `Orbit.UpdateFromStateVectors(pos, vel, body, UT)` | - | swizzled inputs; pattern already in Parsek's `OrbitReseed.cs` |
| Heliocentric -> target SOI patch (step 5) | stock `PatchedConics.CalculatePatch(p, nextPatch, startEpoch, SolverParameters, targetBody)` | - | works on a STANDALONE synthetic Orbit (no vessel) - verified against decompiled stock source, NOT via KSPTrajectories (whose `CalculatePatch` call passes targetBody=null and walks the active vessel's nextPatch chain). Encounter promotion is GATED (review C2): pin `SolverParameters` + assert the encounter under default settings in the canary |
| Window / phase angle (step 1) | **port KAC** `CurrentPhaseAngle` (`Utilities.cs:344`) + Hohmann phase `180*(1-((a_o+a_t)/(2 a_t))^1.5)` (`TimeObjects.cs:1134`) + synodic-rate alignment time (`TimeObjects.cs:1164`) | MIT (copy w/ attribution) | closed-form, one Lambert call per window for the ghost |
| Render the synthesized conic (step 8) | EXISTING Parsek orbit-segment render path | - | `PositionGhostFromOrbit` (flight) + `GhostMapPresence.TryResolveOrbitSegmentWorldPosition` (TS/map) already build a KSP `Orbit` from segment elements and sample it; synthesized segments render for free |

The only NEW math we own: the window finder (small, KAC-derived, DONE - `Reaim/TransferWindowMath.cs`)
and the universal-variable Lambert solver. Everything else is stock or an existing Parsek path. The
render path is already exercised by computed segments (`PatchedConicSnapshot` / `BallisticExtrapolator`
emit `isPredicted` segments through the same path).

### 3.2 The injection seam: `ReaimedTrajectory` (EAGER-cached per window)

The engine reads trajectory data ONLY through `IPlaybackTrajectory` (the engine never sees
`Recording` directly). So re-aim needs **no engine change**: a thin adapter

```
sealed class ReaimedTrajectory : IPlaybackTrajectory
    // built per WINDOW: the ctor/factory runs the synthesis ONCE and stores the assembled
    // OrbitSegments list + the remapped PartEvents list; getters are PURE field returns.
    // delegates every other property to the wrapped Recording.
```

**Eager, not lazy (review C3).** `IPlaybackTrajectory.OrbitSegments` is read on the per-frame hot
path (the positioning loops iterate it every frame for every active ghost). The synthesis (Lambert +
`CalculatePatch`) therefore runs EAGERLY at window-advance, inside the adapter's construction, keyed
by the window UT; the `OrbitSegments` getter returns the stored list with zero computation. A lazy
getter would re-introduce the exact per-frame-solve bug the prior periodicity plan shipped. Add a
log/test assertion that no solve happens inside the getter.

**The adapter also overrides `PartEvents` (review M1), not just `OrbitSegments`.** `PartEvent.ut` /
`orbitSegmentStartUT` bind each event to a specific recorded segment; with S1/S2 re-synthesized those
bindings dangle (the event would resolve against the wrong segment or an out-of-range UT - a
correctness break, not cosmetic). v1: the adapter SUPPRESSES (or clamps to the synthesized span)
PartEvents that fell in the recorded ejection/transfer window, and keeps the ascent (S0) and arrival
(S3) events with their recorded bindings intact. Re-timing ejection FX onto the synthesized S1 is a
deferred polish.

Per-window immutability keeps the shared `Recording` untouched and is unit-testable in isolation.
(Rejected: mutating the Recording's segment list per loop - unsafe under concurrent loops; cloning the
Recording per cycle - GC-heavy.)

---

## 4. The hard part: stitching (S0->S1 and S2->S3)

This is where re-aim earns its complexity; the doc commits to v1 choices and flags the seams.

- **S0/S1 -> S2: anchor to the recorded SOI-EXIT STATE (review recommendation).** Do NOT re-derive the
  ejection from the parking-orbit plane (that produces a visible plane-change KINK in low orbit, right
  where the player is watching). Instead: keep S0 + the recorded parking orbit VERBATIM, and at the
  recorded launch-body SOI-exit moment, replace only the post-exit VELOCITY (direction + magnitude) so
  the heliocentric state vector matches the Lambert `v1`. S1 (the ejection) is synthesized so its
  SOI-exit state matches that v1 (the recorded ejection arc minimally rotated toward v1). The seam is
  then a velocity discontinuity AT THE SOI EDGE - far from the camera, expected at any SOI crossing,
  and the same seam class the existing faithful landing-handoff already tolerates - instead of a
  plane-change kink in low orbit. (Deferred polish: rotate the parking-orbit LAN to contain v_inf and
  re-time the ascent, erasing the seam entirely.)
  - **Phase 3 implementation note (review #4):** the classifier surfaces only the parking orbit's
    KEPLER elements (an `OrbitSegment`), not a state vector. Recover the SOI-exit state by sampling
    that `ParkingOrbit` segment at its `endUT` (Kepler->state is deterministic via `Orbit`); verify
    against a real recording that the parking-segment `endUT` actually coincides with the launch-body
    SOI-exit moment (it is the last launch-body segment before the heliocentric leg, so it should).
- **S2 -> S3 (transfer SOI entry -> recorded arrival).** `CalculatePatch` gives the exact target-SOI
  entry UT + the target-relative hyperbola (`nextPatch`). The recorded arrival arc S3 is re-anchored
  so its start coincides with that UT, target-relative. The recorded capture orbit's plane/periapsis
  won't exactly equal the synthesized hyperbola's -> a small discontinuity at the FAR-planet SOI seam.
  **v1 choice:** snap S3 to the SOI-entry UT and accept the seam (momentary, far from the camera).
  Deferred: blend the arrival hyperbola into the recorded capture.
- **tof is solved, not transplanted (review M3).** Use the Hohmann tof for the WINDOW's actual r1/r2
  geometry as the default Lambert time-of-flight (recorded tof only as a bounded nudge); after the
  Lambert solve, VALIDATE the resulting conic's energy/eccentricity (reject absurd / retrograde /
  hyperbolic-when-it-should-be-elliptic results). A rejected or no-encounter window steps to the next
  window (logged), never renders a miss.
- **No-encounter windows (review C2).** `CalculatePatch` only PROMOTES the target encounter under
  certain conditions (approach-marker setting / background-thread / patch limit), so the
  `SolverParameters` must be set deliberately and the Phase-2 canary MUST assert the encounter
  populates (`closestEncounterBody == target`, `nextPatch.referenceBody == target`) under DEFAULT
  game settings - if it does not, that is a blocker to resolve before wiring. A window whose transfer
  yields no encounter within a bounded `t_arr` refinement is skipped (logged).

These seams are the honest cost of re-aiming a RECORDED mission rather than re-flying it live. The doc
states them so a reviewer/playtester judges them deliberately, not as bugs.

---

## 5. Scheduling + loop integration

- **Cadence = synodic window.** The relaunch schedule is the sequence of departure windows from the
  phase-angle finder (~every synodic period; Kerbin->Duna ~2.1 Kerbin years), throttled UP to the
  player's requested loop period.
- **The schedule PRODUCER is new work (review M2).** The existing `TryBuildRelaunchSchedule` /
  `PeriodicitySolution` is built entirely around an anchor period + per-constraint tolerances +
  `WithinTolerance` - the FAITHFUL phase-lock model. Re-aim has no anchor period and no tolerance
  concept; its UTs come from the synodic/phase-angle solve. So re-aim needs a SEPARATE producer that
  emits departure UTs. Only the downstream CONSUMER is shared: a lazily-extended list of relaunch UTs
  feeding the existing `LoopUnit` span clock. Do not imply `TryBuildRelaunchSchedule` is reused; it
  is not.
- **Hook point.** `MissionLoopUnitBuilder.TryBuildMissionUnit` already branches on the extracted
  constraints. Add: if the config is cross-parent (LCA != launch body) AND a Sun-bodied S2 segment
  exists (section 2), build the re-aim synodic schedule + attach a `ReaimedTrajectory` factory to the
  `LoopUnit`, instead of the faithful zero-drift schedule. A cross-parent config with NO usable S2
  (never warped / background coast) falls back to the existing faithful path (which will read amber /
  rare - acceptable, never garbage). Same-parent path is untouched.

- **Engine seam (mapped 2026-05-29).** The engine rebuilds its per-member `IPlaybackTrajectory` list
  every frame from the committed recordings at `ParsekFlight.cs:18341-18343` (no immutability barrier),
  and samples each member at `loopUT in [spanStart, spanEnd]` (`GhostPlaybackLogic.TryComputeSpanLoopUT`,
  `loopUT = spanStart + phaseWithinSpan`). So re-aim substitutes a per-window `ReaimedTrajectory` for a
  re-aim member at that list-build point, CACHED by the active window index (from the schedule's
  `TryResolveActiveLaunch`) so the Lambert/CalculatePatch synthesis runs only on window-advance, not
  per frame (review C3). The assembled segments are RECORDED-SPAN-relative (not absolute - see
  `ReaimSegmentAssembler`); only the transfer's inertial orientation varies per window. `GhostMapPresence`
  (`:3627`) + the new `GhostTrajectoryPolylineRenderer`/`GhostOrbitLinePatch` (#970) read
  `traj.OrbitSegments`, so the re-aimed transfer renders on the map/TS for free. Remaining wiring:
  the re-aim synodic schedule producer (new, NOT `TryBuildRelaunchSchedule`), the re-aim descriptor on
  the `LoopUnit`, and the per-window-cached substitution - this is the Phase-3c step that ends in the
  in-game playtest.
- **Per-window trajectory.** The `LoopUnit` carries a re-aim descriptor (launch body, target,
  recorded parking/arrival segments). Per loop instance, the engine asks for the trajectory at the
  active window; the adapter computes (and caches) the synthesized segments for that window. Recompute
  only when the window advances (cheap: one Lambert + one CalculatePatch per window).

---

## 6. Logistics integration (the point of all this)

A logistics route consumes "this mission departs at UT_d and delivers at UT_a, repeatably." Re-aim
produces exactly that: the window schedule gives departure UTs (~synodic cadence) and each window's
`CalculatePatch` gives the arrival UT. A route hangs its resource transfer on the arrival UT of each
re-aimed cycle. This is why faithful replay was a non-starter for logistics (a 1142-year cadence
delivers nothing) and re-aim is required. The route layer reads the re-aim schedule the same way it
would read any relaunch schedule; no special coupling.

---

## 7. UI

- **Auto-by-target**, so no new control. A cross-parent looped mission's period cell now shows the
  synodic cadence ("~2.1 yr (Duna transfer)") instead of the faithful-replay amber/"not aligned"; the
  TTL counts down to the next departure window; "Warp to..." jumps to it (now a useful ~2-year jump,
  not centuries).
- The period-cell basis label says "transfer" (re-aim) vs "window" (faithful) so the two modes read
  distinctly.
- A small honesty marker (tooltip): "replays your ascent and arrival; the interplanetary transfer is
  re-planned each window to reach the target" so the player understands the ghost's transfer differs
  from the recorded one.

---

## 8. Faithfulness contract (state it plainly)

For a re-aim mission, the ghost's **ejection + heliocentric transfer are RECOMPUTED**, so they differ
from your recorded flight (different ejection direction/time, possibly a different transfer shape).
Your **ascent and arrival arcs are still your recorded flight**. This is the deliberate trade: a
useful repeating cadence in exchange for a re-planned (not byte-faithful) transfer leg. Same-parent
missions remain byte-faithful (unchanged).

**PartEvent / FX remap is a CORRECTNESS requirement, not just cosmetics (review M1).** `PartEvent.ut`
and `orbitSegmentStartUT` bind each recorded event to a specific recorded segment + UT. With S1/S2
re-synthesized, an ejection-window event's recorded binding dangles - it would resolve against the
wrong synthesized segment or an out-of-range UT (a broken position lookup, not merely a plume pointing
the wrong way). So the `ReaimedTrajectory` adapter MUST handle the ejection/transfer-window PartEvents:
v1 SUPPRESSES them (or clamps them to the synthesized S1/S2 span) while keeping the S0 ascent and S3
arrival events with their recorded bindings intact. This must be covered by an adapter test (no event
binds to a UT outside the assembled segment list). Re-timing ejection FX onto S1 is a deferred polish.

---

## 9. Data model / persistence

Fully DERIVED, nothing new persisted (mirrors the periodicity work): the window schedule and the
synthesized segments recompute from the recording's parking/arrival segments + the live bodies +
`BuildSignature` (which already folds the transited-body set + their live periods/SOI). No recording-
format change. The re-aim descriptor on the `LoopUnit` is rebuilt on signature change.

---

## 10. Test strategy

- **Lambert (pure xUnit):** the ported Gooding solver against known textbook solutions (e.g. an
  Earth->Mars case with published v1/v2; a 90-degree and a near-180-degree transfer; a hyperbolic
  short-tof case). Round-trip: feed the solved v1 back, propagate, confirm it reaches r2 within
  tolerance. Degenerate guards (near-180, near-zero tof).
- **Window finder (pure):** synodic period + Hohmann phase against KAC's formulas; next-window UT from
  a known phase; stock Kerbin/Duna sanity (~2.1 yr).
- **Segment classification (pure):** reuse #968's LCA-walk tests; the S0/S1/S2/S3 split from a
  synthetic Kerbin->Sun->Duna OrbitSegment chain.
- **Trajectory assembly (pure):** given a synthetic recording + a fake body system, the assembled
  segment list is [recorded ascent, synthesized ejection, synthesized transfer, re-anchored arrival]
  with contiguous UTs and the transfer conic actually reaching the target SOI.
- **`ReaimedTrajectory` adapter (pure):** delegates every IPlaybackTrajectory property to the
  Recording except OrbitSegments; the override is the assembled list.
- **In-game canary (`RuntimeTests`):** a synthetic Kerbin->Duna recording, re-aimed against the LIVE
  body graph: assert a transfer Orbit is built, `CalculatePatch` returns an ENCOUNTER with Duna, the
  arrival UT is finite, and the assembled segments render (a ghost position resolves at a mid-transfer
  UT). This exercises the stock-API seam end to end.

The math (Lambert, phase angle, assembly) is fully unit-testable off Unity via the `IBodyInfo` seam +
hand-built recordings; only the live render is playtest-verified.

---

## 11. Phasing (each phase ends with a clean-context review)

- **Phase 1 - Lambert + window (pure).** Port `Gooding.cs` (+ root-finder) under a permissive header
  with attribution; port the KAC phase-angle/synodic math. Full unit tests vs known solutions. No
  wiring. Clean math review.
- **Phase 2 - transfer synthesis (pure + stock).** Segment classification (salvaged LCA walk), the
  endpoints -> Lambert -> `UpdateFromStateVectors` -> `CalculatePatch` pipeline producing the transfer
  + ejection OrbitSegments + arrival UT. Pure where possible; the stock-API calls behind a thin seam.
  Tests + the in-game canary. Review.
- **Phase 3 - assembly + adapter + render.** `ReaimedTrajectory`, the assembled segment list, wire
  into `MissionLoopUnitBuilder` for cross-parent missions (auto-by-target), render through the
  existing path. In-game playtest. Review.
- **Phase 4 - schedule + UI.** Synodic relaunch schedule (reuse the schedule shape), TTL / period cell
  / Warp / basis label / honesty tooltip. Tests + playtest. Review.
- **Phase 5 - hardening + logistics hook.** The stitching seams' playtest tuning, the logistics
  schedule consumer, edge cases. Final whole-PR review.

(Single PR if it stays manageable; otherwise Phase 1-2 as a "math + synthesis" PR and Phase 3-5 as a
"wiring + UI" PR. Decide after Phase 2.)

---

## 12. Open questions (resolve during implementation)

1. **tof choice.** Use the Hohmann ideal `t_transfer` for the first cut, or the RECORDED transfer
   duration (keeps the ghost's flight time like your real mission)? Recorded duration is more
   "your mission" but may not be near-optimal; Hohmann is cleaner. Lean recorded-duration with a
   Hohmann fallback.
2. **Ejection plane.** v1 ignores the recorded parking-orbit plane (clean ejection in the transfer
   plane). If the dogleg looks bad in playtest, rotate the parking orbit's LAN (re-times the ascent).
3. **Arrival seam blending** (S2->S3) - accept the snap for v1, blend later.
4. **Departure body not the homeworld** (a mission launched from, say, Duna to Jool) - the model is
   body-agnostic (LCA walk handles it), but confirm with a non-Kerbin-launch test.
5. **Multi-rev / no-encounter windows** - bounded refinement of `t_arr`; skip-and-log if unsolvable.
6. **Porkchop dV refinement** - v1 uses the closed-form Hohmann window (one Lambert call). A dV-grid
   porkchop (off the hot path) is a later option only if "your transfer looks too unoptimized" in
   playtest.

## 13. Deferred (NOT in this PR)

Gravity assists, multi-hop chains, aerocapture, replaying a target LANDING after re-aimed arrival
(S4), the porkchop dV-grid, and the parking-orbit-LAN re-timing polish. Each is a follow-up once the
single-hop orbital-arrival core is proven in playtest.

## 14. What does NOT change

- Same-parent (Mun/Minmus) looped missions: byte-identical faithful replay, untouched.
- Non-looping ghosts, per-recording auto-loop: untouched.
- The recording format, the playback engine (re-aim plugs in via the `IPlaybackTrajectory` adapter),
  the orbit-segment renderer, the loop framework + span clock (re-aim reuses them).
- The replay-as-is contract for everything EXCEPT a cross-parent looped mission's transfer leg.
