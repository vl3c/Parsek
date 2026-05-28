# Re-aim interplanetary transfers (looped missions that actually repeat)

Status: DESIGN (research done, not yet implemented).
Branch: `reaim-transfers` (off `origin/main`).
Supersedes the unmergeable faithful-cross-parent PR #968 (kept open as reference only; its
body-hierarchy walk is salvaged here, the rest is replaced).

Revision history:
- 2026-05-28 initial design, after a deep dive into stock KSP orbital math + the cloned mods.

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

Re-aim is NOT "throw away the recording." A recorded interplanetary mission is a chain of segments
split by SOI (read off the `OrbitSegment.bodyName` transitions the recording already stores):

| Seg | Frame | What it is | Re-aim treatment |
|-----|-------|------------|------------------|
| **S0** | launch-body surface + inertial | ascent KSC -> parking orbit | **REPLAY as-is** (faithful at any time; keep the pad-rotation alignment so the ascent connects to the recorded parking orbit) |
| **S1** | launch-body inertial | ejection burn (parking orbit -> SOI exit hyperbola) | **RE-SYNTHESIZE** the ejection hyperbola to match the new transfer's required departure velocity |
| **S2** | common-ancestor (Sun) inertial | heliocentric transfer coast | **RE-SYNTHESIZE** (Lambert solve to the target's current position) - the core of re-aim |
| **S3** | target inertial | SOI capture / arrival orbit | **REPLAY as-is** (target-relative, faithful at any time, placed at the new arrival UT) |
| **S4** | target surface | landing / surface ops | **DEFERRED** (v1 stops at orbital arrival; re-stitching a descent to a re-aimed arrival is Phase-later) |

So v1 keeps your real launch (S0) and your real arrival orbit (S3), and recomputes only the
connecting ejection + heliocentric transfer (S1 + S2) for the chosen window. The destination changes
where it is each window; we rebuild the bridge to it.

Segment classification reuses #968's LCA walk: the launch body is the earliest recorded body; the
heliocentric leg is the segment(s) whose body is the lowest common ancestor of launch + target; the
target is the body entered after it. (Salvaged: `AncestorChain` / `TryFindCommonAncestor`.)

---

## 3. The pipeline

Per looped re-aim mission, per window:

```
1. WINDOW          next departure UT t_dep (synodic / phase-angle), arrival UT t_arr
2. ENDPOINTS       r1 = launchBody.orbit pos at t_dep ; r2 = target.orbit pos at t_arr   (heliocentric)
3. LAMBERT         v1 = Gooding.Solve(mu_sun, r1, v_launchBody, r2, tof=t_arr-t_dep, nrev=0)
4. TRANSFER CONIC  Orbit S2 = UpdateFromStateVectors(swizzle(r1), swizzle(v1), Sun, t_dep)
5. SOI PATCH       CalculatePatch(S2, nextPatch, t_dep, {FollowManeuvers=false}, target)
                   -> S2.EndUT = target-SOI-entry UT ; nextPatch = target-relative hyperbola
6. EJECTION CONIC  v_inf = v1 - v_launchBody ; build the launch-body-relative ejection hyperbola
                   S1 from the recorded parking orbit periapsis to v_inf
7. ASSEMBLE        synthesized segment list: [S0 recorded] [S1 ejection] [S2 transfer]
                   [S3 recorded arrival, re-anchored to S2.EndUT, target-relative]
8. PRESENT         wrap the Recording in a ReaimedTrajectory (IPlaybackTrajectory) whose
                   OrbitSegments = the assembled list; the engine renders it unchanged
```

### 3.1 What is stock, what we port, what we build

| Piece | Source | License | Notes |
|-------|--------|---------|-------|
| Planet position/velocity at UT (steps 2) | stock `CelestialBody.orbit.getRelativePositionAtUT` / `getOrbitalVelocityAtUT` | - | `.xzy` swizzle; `getTruePositionAtUT` for absolute |
| Lambert solve (step 3) | **port MechJebLib `Gooding.cs`** | Unlicense/CC0/MIT-0/MIT (permissive, NO GPL) | swap `V3`->`Vector3d`, bring the Householder root-finder; ~200 lines, pure, unit-testable |
| Build conic from state vector (step 4) | stock `Orbit.UpdateFromStateVectors(pos, vel, body, UT)` | - | swizzled inputs; pattern proven by KSPTrajectories `Trajectory.cs:478` |
| Heliocentric -> target SOI patch (step 5) | stock `PatchedConics.CalculatePatch` | - | KSPTrajectories drives it on a SYNTHETIC Orbit (no vessel), `Trajectory.cs:480` |
| Window / phase angle (step 1) | **port KAC** `CurrentPhaseAngle` (`Utilities.cs:344`) + Hohmann phase `180*(1-((a_o+a_t)/(2 a_t))^1.5)` (`TimeObjects.cs:1134`) + synodic-rate alignment time (`TimeObjects.cs:1164`) | MIT (copy w/ attribution) | closed-form, one Lambert call per window for the ghost |
| Render the synthesized conic (step 8) | EXISTING Parsek orbit-segment render path | - | `PositionGhostFromOrbit` (flight) + `GhostMapPresence.TryResolveOrbitSegmentWorldPosition` (TS/map) already build a KSP `Orbit` from segment elements and sample it; synthesized segments render for free |

The only NEW math we own: the window finder (small, KAC-derived) and the Lambert port. Everything
else is stock or an existing Parsek path. The render path is already exercised by computed segments
(`PatchedConicSnapshot` / `BallisticExtrapolator` emit `isPredicted` segments through the same path).

### 3.2 The injection seam: `ReaimedTrajectory`

The engine reads trajectory data ONLY through `IPlaybackTrajectory` (27 properties; the engine never
sees `Recording` directly). So re-aim needs **no engine change**: a thin adapter

```
sealed class ReaimedTrajectory : IPlaybackTrajectory
    // delegates every property to the wrapped Recording EXCEPT OrbitSegments,
    // which returns the per-window assembled list (S0 recorded + S1/S2 synthesized + S3 re-anchored)
```

is handed to the engine in place of the `Recording` for a re-aim loop instance. This keeps the
shared `Recording` immutable (no per-loop mutation), is unit-testable in isolation, and is the
seam the codebase already implies. (Rejected alternatives: mutating the Recording's segment list
per loop - unsafe under concurrent loops; cloning the Recording per cycle - GC-heavy.)

---

## 4. The hard part: stitching (S0->S1 and S2->S3)

This is where re-aim earns its complexity; the doc commits to v1 choices and flags the seams.

- **S0 -> S1 (parking orbit -> ejection).** The recorded parking orbit has a fixed inertial plane
  (inclination/LAN). The window's required ejection asymptote (`v_inf = v1 - v_launchBody`) generally
  does NOT lie in that plane, so a real mission would dogleg / plane-change. **v1 choice:** synthesize
  the ejection hyperbola in the plane that contains the parking-orbit periapsis radius and `v_inf`
  (a clean ejection at the recorded periapsis altitude), accepting that it may not match the recorded
  parking-orbit plane. Visual seam: the ghost may appear to change plane at ejection. Acceptable for a
  ghost; logged. (Deferred refinement: rotate the parking orbit's LAN to contain `v_inf`, which also
  re-times the ascent - a Phase-later polish.)
- **S2 -> S3 (transfer SOI entry -> recorded arrival).** `CalculatePatch` gives the exact target-SOI
  entry UT + the target-relative hyperbola (`nextPatch`). The recorded arrival arc S3 is re-anchored
  so its start coincides with that UT, target-relative. The recorded capture orbit's plane/periapsis
  won't exactly equal the synthesized hyperbola's, so there is a small discontinuity at the SOI seam.
  **v1 choice:** snap S3 to the SOI-entry UT and accept the seam (same class of seam the existing
  faithful-replay landing-handoff already tolerates via the A/B rotation flag). Deferred: blend the
  arrival hyperbola into the recorded capture.
- **No-encounter windows.** If `CalculatePatch` returns no ENCOUNTER (the chosen `tof` missed), the
  window finder refines `t_arr` (or steps to the next window). A window that cannot be made to
  encounter within a bounded search is skipped (logged), never rendered as a miss.

These seams are the honest cost of re-aiming a RECORDED mission rather than re-flying it live. The doc
states them so a reviewer/playtester judges them deliberately, not as bugs.

---

## 5. Scheduling + loop integration

- **Cadence = synodic window.** The relaunch schedule is the sequence of departure windows from the
  phase-angle finder (~every synodic period; Kerbin->Duna ~2.1 Kerbin years), throttled UP to the
  player's requested loop period exactly like the existing zero-drift schedule throttles. This reuses
  the `MissionRelaunchSchedule` shape (a lazily-extended list of relaunch UTs) - only the SOURCE of
  the UTs changes (synodic windows instead of the absolute-coincidence scan).
- **Hook point.** `MissionLoopUnitBuilder.TryBuildMissionUnit` already branches on the extracted
  constraints. Add: if the config is cross-parent (LCA != launch body), build a re-aim schedule +
  attach a `ReaimedTrajectory` factory to the `LoopUnit`, instead of the faithful zero-drift schedule.
  Same-parent path is untouched.
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
from your recorded flight (different ejection direction/time, possibly a different transfer shape, and
the recorded engine-burn FX during ejection will not line up with the new path). Your **ascent and
arrival arcs are still your recorded flight**. This is the deliberate trade: a useful repeating
cadence in exchange for a re-planned (not byte-faithful) transfer leg. Same-parent missions remain
byte-faithful (unchanged).

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
