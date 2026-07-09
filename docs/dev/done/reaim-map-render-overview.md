# Re-aim, map rendering, and cross-SOI trajectories: the whole board (2026-06-16)

*A cross-branch orientation document for the re-aim / mission-looping / map-render work, grounded in the
design docs it cites. Snapshot of live PR/branch state on 2026-06-16; committed on branch
`reaim-self-overlap-scheduling` (PR #1167). Update the PR/status references if branch states move.*

---

## 0. The one-paragraph "why" (gameplay)

Parsek records your flights and replays them as ghosts. The **Missions tab** lets you set a recorded
mission to **loop** so its ghost relaunches forever. That is trivial for a Mun hop, but it breaks for an
**interplanetary** mission: a Kerbin->Duna transfer is aimed at *where Duna was the day you recorded it*.
Replay it a year later and Duna has moved a third of the way around the Sun, so the faithful ghost flies
into empty space, the map line points at nothing, and the Missions tab says "not aligned." **Re-aim** is
the feature that fixes this: each time the looped ghost launches, it re-solves the transfer for *where
the target planet actually is that launch window*, so the looped mission keeps going to Duna every time
and the map / Tracking Station / Missions UI all show it correctly. Everything below is in service of
that single gameplay promise: **record an interplanetary mission once, loop it, and have it keep working
and rendering correctly forever.**

Re-aim only touches **looped, cross-parent (interplanetary) missions**. A one-shot (non-looped) flight is
replayed verbatim and never re-aimed. A same-parent loop (Kerbin->Mun, station resupply in Kerbin orbit)
is handled by a *different* mechanism (the loiter / phase-lock / station-alignment path), not heliocentric
re-aim - though it shares the same map-rendering pipeline (Section 5/6).

**This is all one roadmap.** The Missions looping feature is specified in `docs/dev/design-mission-abstractions.md`
(the Missions tab + loop-unit model), and the re-aim work is tracked as the **M-MIS-0..11 Missions
completion roadmap** (in `docs/dev/todo-and-known-bugs.md`). The streams below are roadmap milestones, not
loose fixes: Stream A = M-MIS-1/3, Stream C = M-MIS-2, the station-alignment half of Section 6 = M-MIS-4.

---

## 1. What "correct" requires - the four things re-aim must get right

A looped interplanetary mission has to nail four independent things. Almost every PR/branch below is
one of these four:

| Axis | The question | Gameplay symptom when wrong |
|---|---|---|
| **WHEN** (scheduling) | Relaunch at each real synodic window, pad-aligned | Ghost launches at the wrong time; transfer renders off the planet |
| **DEPARTURE** geometry | Leave the launch body correctly each window (incl. park-then-burn) | Transfer points at empty space; the parking orbit draws across the solar system |
| **ARRIVAL** geometry | Arrive at the destination at the recorded rotation/orbital phase | Looped lander touches down at the wrong spot; rendezvous misses the station |
| **RENDER** | Actually DRAW all of the above (orbit lines, icons, SOI handoffs) on the map/TS | Line dead-ends, kinks at SOI boundaries, icon teleports, wrong zoom/warp behavior |

The fourth (RENDER) is shared with *all* ghosts, not just re-aim - which is why the map-render bug cluster
(Section 5) is partly separate from re-aim but re-aim depends on it.

---

## 2. How re-aim works mechanically (so the PRs make sense)

The pipeline, in order, for one looped interplanetary mission:

1. **Classifier** (`ReaimClassifier`) - decides whether this recording is re-aimable (a single cross-parent
   hop with a recognizable transfer). If not, it stays **faithful** (verbatim replay) - "fail closed."
2. **Window planner** (`ReaimWindowPlanner`) - builds the synodic relaunch schedule: launch at
   `D_k = D0 + k * synodic`, pad-aligned to the launch body's rotation (`PadAlignLaunch`).
3. **Per-window resolver** (`ReaimPlaybackResolver`) - each window, re-solves the heliocentric transfer
   (a live Lambert solve via the owned `Reaim/UvLambert.cs` behind the `ITransferSolver` seam) aimed at
   where the target is *now*. The recorded time-of-flight is the *anchor* (step 0), then `ReaimTofSearch`
   widens the search with the target's eccentricity toward the geometric Hohmann time, so eccentric
   planets (Eeloo/Moho) still resolve - see `reaim-eccentric-tof-reliability.md` (M-MIS-3 stage B).
4. **Segment assembler** (`ReaimSegmentAssembler`) - splices that fresh transfer arc into the recorded
   launch-escape and destination-capture legs (which are body-relative and already follow their live body).
5. **Render** - the re-aimed segment list is drawn by the map/TS ghost presence (`GhostMapPresence`,
   one ProtoVessel + orbit line per ghost) and the flight engine (`GhostPlaybackEngine`, 3D meshes).

> The design's own decompositions (worth reading alongside this overview): the **S0-S4 segment model**
> and the launch/transfer/arrival leg structure in `docs/dev/done/plans/reaim-interplanetary-transfers.md`
> (the canonical re-aim design), and the **L0-L9 flexibility chain** in
> `docs/dev/plans/reaim-destination-arrival-alignment.md`. The four-axis table in Section 1 above is a
> teaching synthesis, not the design's vocabulary.

**Key invariant (everywhere):** recorded data is immutable. Re-aim is **in-memory, loop-only, on copied
structs** - it never rewrites a `.prec` / OrbitSegment on disk.

---

## 3. The five work streams (every PR/branch mapped)

### Stream A - Re-aim solver reliability (make the per-window solve succeed)
*Foundation. Without a correct solve, nothing else can render right. Largely DONE.*

| PR / branch | Status | What it does |
|---|---|---|
| #1140 near-180 handedness | merged | Fixes a handedness flip that made near-180-degree windows wrongly decline |
| #1148 eccentric-target tof (+#1141 plan) | merged | Widens the time-of-flight search with target eccentricity so Eeloo/Moho windows resolve |
| #1116 resolver reliability + E2E harness, #1122 band-edge case | merged | Deterministic pinned-UT tests + a feasibility sweep so reliability is measurable |
| `reaim-lambert-reliability`, `reaim-chain-synthesis` | planned / in-flight | Remaining hardening (conditional projection, flipped-bit re-solve, multi-leg chains) |

**Gameplay:** more launch windows produce a correct transfer instead of failing closed to a wrong-looking
faithful replay - especially eccentric planets and awkward (near-180) geometries.

**Design docs / milestone:** this is the **M-MIS-1** (resolver reliability) and **M-MIS-3** (eccentricity)
slices of the Missions completion roadmap. Specs: `docs/dev/plans/reaim-resolver-reliability.md` and
`docs/dev/plans/reaim-eccentric-tof-reliability.md`. The underlying per-window contract is the core re-aim
design `docs/dev/done/plans/reaim-interplanetary-transfers.md` (Phase 3c).

### Stream B - Re-aim DEPARTURE side (**my current work**)
*Make the launch side correct, including the very common "park in solar orbit, then burn" departure.*

| PR / branch | Status | What it does |
|---|---|---|
| **#1166** `reaim-heliocentric-parking-departure` (base: main) | **open** | Makes the classifier ENGAGE re-aim on a two-burn departure (escape into a co-orbital solar park, coast, then burn for the target). Before this, the most common real Kerbin->Duna mission *declined* re-aim entirely and looped broken. |
| **#1167** `reaim-self-overlap-scheduling` Increment 1 (base: #1166) | **open (just opened)** | Re-phases the recorded heliocentric PARK into the live frame so it renders near the launch body connected to the transfer (was drawing ~239 deg across the solar system). Window 0 now fully correct. |
| Self-overlap **Increment 2** | **not started** | The full fix for the deeper problem: when the recorded mission's total span exceeds the launch->target synodic period (every real heliocentric-park Duna mission), the ghost must relaunch every synodic and *self-overlap* (~2 instances at once) so the transfer touches the live planet at EVERY window, not just the first. Requires building a new per-cycle, body-crossing renderer on the shared map path (see Section 7). |

**Gameplay:** a Kerbin->Duna (or any planet->planet) mission that parks in solar orbit before the transfer
burn - the normal way you fly one - can now be looped and render correctly on the departure side.

**Design docs:** #1166 = `docs/dev/design-reaim-heliocentric-parking-departure.md` (Approach B
"loiter-reuse + admissibility graft"); it lifts the deferral noted in the core design
`reaim-interplanetary-transfers.md` ("Lambert assumes r1 = launch body"). #1167 + Increment 2 =
`docs/dev/plans/reaim-self-overlap-scheduling-design.md` (§0 increment split, Change 5 park LAN re-phase,
§2.1-2.2 the overlap-presence blocker + the cycle==window identity). IMPORTANT framing: self-overlap is
NOT a re-aim novelty - it is the re-aim instance of Parsek's **existing loop-unit `OverlapCadenceSeconds`
self-overlap model** (a looped mission whose period is shorter than its span relaunches and runs several
staggered instances at once), re-pointed at the synodic cadence. The same loop-unit model already drives
non-re-aim overlapping loops.

### Stream C - Re-aim ARRIVAL side
*Make the destination side correct: land at the recorded site / meet the live station each cycle.*

There are **two distinct arrival knobs** (do not conflate them):

| PR / branch | Status | What it does |
|---|---|---|
| **PR #1030** arrival HOLD ("Duna One") | **merged** | The *primary* knob: a continuous per-loop hold inserted at the heliocentric->capture boundary that shifts the deorbit *later* so the destination body's rotation (landing) / station phase (rendezvous) recurs to the recorded value at arrival. Rotation-only, fails closed. |
| **#1155** `reaim-dest-loiter-retimer` (M-MIS-2 **P4**, base: main) | **open** | The *complementary* knob: a pre-landing **loiter re-timer** that trims the recorded destination loiter (the `keepRevs` count, deorbit shifts *earlier*) for missions whose recorded arrival has a post-arrival loiter the hold alone can't cover. Scoped to the single-recording case. |
| #1139 Mun-station loiter knob, #1136 loop relative live anchor | merged | The same alignment idea for *destination-body* parking loops and station-anchored route deliveries (M-MIS-4) |

**Gameplay:** a looped lander keeps touching down at the same site; a looped rendezvous keeps meeting the
live station instead of arriving where it used to be. The arrival HOLD (PR #1030) shipped for the
no-loiter case; #1155 (P4) extends it to recordings that loiter at the destination before landing. **#1155
was blocked by #1166** - the arrival knobs cannot fire until re-aim actually engages, which #1166 unblocked.

**Design docs / milestone:** all of arrival alignment is `docs/dev/plans/reaim-destination-arrival-alignment.md`
(the L0-L9 chain; the hold vs the loiter re-timer; the D8 dual-constraint fail-closed) and the station half
`docs/dev/plans/mission-station-arrival-hold.md` - the **M-MIS-2** roadmap slice.

### Stream D - The re-aim cross-SOI handoff seam (the "planet-to-moon transition trajectory" item)
*The handoff between the re-aimed heliocentric transfer and the recorded escape/capture legs, at the
Kerbin SOI exit and the Duna SOI entry. This is specific to the re-aim center-to-center SYNTHESIS - not a
generic SOI-render gap (a non-re-aimed trajectory does not have it).*

| PR / branch | Status | What it does |
|---|---|---|
| **#1165** `docs-reaim-seam-todo` (base: main, **docs only**) | **open** | DOCUMENTS the deferred re-aim cross-SOI seam (a known accepted limitation, no code). |
| #1156 loop-arc-segment-coalesce | merged | (Stream E, generic) Fixed the incoming SOI-approach line drawing one fragment at a time |
| `fix-soi-trajectory-seam-coverage` (#1153) | closed | An earlier SOI-seam attempt, superseded |

**What the seam actually is (corrected per `reaim-seam-investigation.md`, HIGH confidence):** re-aim
substitutes only the heliocentric coast with a fresh **center-to-center** Lambert (r1 = launch-body
*center*, r2 = target-body *center*) and renders it **full-span** (NaN render bounds), while the recorded
escape/capture hyperbolae are replayed verbatim ending at the **SOI shell**. So at each handoff the synth
arc sits at the body *center* while the recorded leg ends ~1 SOI radius away -> the ghost transform (and
therefore both its **map icon AND its orbit line**) **teleports ~1 SOI radius** at the Kerbin-exit and the
Duna-entry. The position (center-vs-SOI) gap is ~**96%** of the jump; orbit orientation/shape mismatch is
only ~**4%**. The often-quoted "~62 degrees" is an **artifact** of treating both endpoints as if they sat
on the SOI sphere (one is at the planet center), not a real velocity kink. The underlying solve is correct
and the icon does *eventually* arrive at the destination - but it jumps at the two handoffs rather than
flying a continuous encounter into the SOI.

**The deferred fix (large, sequenced last):** the core design's "option 3" - synthesize the WHOLE
patched-conic chain from one solve so the escape's SOI-exit STATE and the capture's SOI-entry STATE match
the heliocentric v1/v2, i.e. all three legs **meet at the same SOI-sphere position with continuous state
(position first, then velocity)**. Rigid-rotating the recorded transfer (the tempting shortcut) is rated
NOT VIABLE because it fixes only the ~4% orientation residual, not the dominant ~1-SOI-radius endpoint gap.
This is deliberately sequenced AFTER the in-flight re-aim branches land, to avoid stacking rewrites.

**Gameplay:** this is why a looped interplanetary ghost can *jump* at the planet SOI boundaries even when
it's aimed right. NOTE the scope: this seam is a **re-aim (cross-parent) artifact**. A **planet->moon**
transfer like Kerbin->Mun is *same-parent* and is NOT re-aimed (no center-to-center substitution), so it
does not have this seam - its cross-SOI *line drawing* is handled by the generic render pipeline (Stream E,
e.g. #1156), not by #1165's deferred fix.

**Design docs:** the deferred-seam entry in `docs/dev/todo-and-known-bugs.md` ("re-aim cross-SOI transfer
seam") + the full ranked-options investigation at the umbrella root `reaim-seam-investigation.md`; the
original deferral is in `docs/dev/done/plans/reaim-interplanetary-transfers.md` (the accepted SOI-edge seam
+ "option 3" deferred).

### Stream E - General map / Tracking-Station render pipeline
*The shared machinery every ghost (re-aim or not) draws through. Mostly DONE; it's the substrate re-aim
rendering sits on.*

| PR / branch | Status | What it does |
|---|---|---|
| #1144 zoom-out cull, #1145 warp icon-off-orbit, #1149 loop icon off orbit at warp, #1158 false-anomaly probe, #1150 over-suppression | merged | A cluster of fixes so ghost icons + orbit lines render in the right place at all zoom levels and time-warp rates |
| #1106 remove rollout-gate settings, the "MapRender Director" 8b-8f cutover | merged | The new single-owner render pipeline + the `MapRenderTrace`/`MapRenderProbe` observability that catches these bugs |

**Gameplay:** ghost map icons and orbit lines stay on their orbit at high warp and when you zoom out, and
don't double-draw or vanish. These bugs are *general* (they affect any looped ghost), which is why they're
their own stream and not "re-aim."

**Design docs:** the render pipeline + its observability are `docs/dev/design-map-ts-render-tracer.md`
(the `MapRenderTrace` / `MapRenderProbe` tooling) and the 8b-8f "MapRender Director" cutover notes in
`.claude/CLAUDE.md` (`GhostTrajectoryPolylineRenderer` / `GhostMapPresence`).

---

## 4. How it all relates - the dependency / sequencing picture

```
                         Stream A: solver reliability (foundation, ~done)
                                        |
                                        v
   Stream E: map render pipeline  -->  RE-AIM CORE (classify -> plan -> solve -> assemble -> render)
   (shared substrate, ~done)           /            |             \
                                      /             |              \
                          B: DEPARTURE      C: ARRIVAL       D: cross-SOI render
                          #1166 (engage)    #1155 (P4)       #1165 (deferred seam)
                          #1167 (park,me)   blocked-by-#1166
                          Incr.2 (overlap)
```

**Why this order:**
- **A and E first** because a correct solve and a working render substrate are prerequisites for
  everything. Both are largely merged.
- **#1166 (engage on park departures)** had to come next because the *most common* real interplanetary
  mission parks before burning, and re-aim was declining it - so neither the departure render NOR the
  arrival hold could even run on a realistic mission. #1166 is the keystone that unblocks B and C.
- **My self-overlap work (B: #1167 + Increment 2)** completes the departure-side *render* now that #1166
  engages. Increment 1 (the park re-phasing) is shippable and low-risk; Increment 2 (the every-window
  overlap) is the substantial build.
- **#1155 (C, arrival)** is orthogonal to departure and was explicitly blocked by #1166; it can proceed in
  parallel once #1166 lands.
- **#1165 (D, cross-SOI seam)** is deliberately LAST. The *solve* is correct and the icon ultimately
  reaches the destination, but the ghost transform (icon AND orbit line) teleports ~1 SOI radius at each
  handoff - a visible jump, not merely a line gap. Its fix is a large patched-conic rewrite that must
  sequence *after* the in-flight re-aim branches to avoid stacking rewrites on a moving base. Right now it
  is a *documented, accepted known limitation*, not active work.

---

## 5. Where my current branch sits (so it's unambiguous)

- I am on **`reaim-self-overlap-scheduling`** = **PR #1167**, stacked on #1166 (it needs #1166's
  "this departed from a heliocentric park" flag).
- I do **departure-side render only** (Stream B). I do **not** touch #1155 (arrival), #1165 (the cross-SOI
  seam), or the general render pipeline (Stream E) - those are other streams / sessions.
- Increment 1 (shipped in #1167): the park no longer teleports ~239 deg across the solar system; it renders
  near the launch body, connected to the escape and the transfer; the first synodic window is fully correct.
- Increment 2 (not started, pending your in-game validation of Increment 1): the full overlap build so the
  transfer touches the live planet at *every* window, with N correct per-cycle orbit lines/icons.

---

## 6. The clarification that probably resolves most of the confusion

Three things look similar on the map but are *different mechanisms*:

1. **Heliocentric re-aim** (Streams A/B/C/D): only for **cross-parent** loops - launch body and target orbit
   *different* parents (Kerbin and Duna both orbit the Sun, so the "common ancestor" is the Sun - that is
   the design's own term). This is where the Lambert solve, synodic windows, the park, and the re-aim SOI
   seam live. Specs: `reaim-interplanetary-transfers.md`.
2. **Same-parent loop scheduling** (phase-lock / zero-drift / station alignment): Kerbin->Mun, Kerbin-orbit
   station resupply - both endpoints share **Kerbin** as parent. No heliocentric solve; instead the loop
   relaunches **phase-locked** to the body's rotation / the station's orbit, via the **zero-drift relaunch
   schedule** (`MissionPeriodicity`) and, for stations, the **M-MIS-4** machinery: the `VesselOrbital`
   constraint (M4a) and the loiter knob (M4b). Specs: `design-mission-periodicity.md`,
   `mission-periodicity-phases.md`, `zero-drift-reschedule-hardening.md`, `design-mission-phasing-alignment.md`,
   `mission-vesselorbital-tier1.md`, `mission-loiter-knob.md`. (#1139, #1136 are this category.)
3. **Cross-SOI RENDER** - two sub-cases that the overview earlier blurred:
   - **(3a) Generic cross-SOI line drawing** (Stream E, shared): drawing *any* trajectory across an SOI
     boundary - interplanetary OR Kerbin->Mun. This is the general map pipeline (arc clipping, segment
     coalescing, the icon following across the handoff; e.g. #1156). A non-re-aimed trajectory uses only this.
   - **(3b) The re-aim center-to-center seam** (Stream D, #1165): the *specific* ~1-SOI-radius teleport that
     arises ONLY because re-aim splices a center-to-center Lambert onto SOI-shell recorded legs. It is a
     re-aim artifact, not a generic gap.

So "planet-to-moon transition trajectories" means category **3a** (the generic cross-SOI render). A
Kerbin->Mun mission uses category-2 scheduling + category-3a rendering and is **not** heliocentric-re-aimed
- so it does **not** have the #1165 (3b) seam. Only cross-parent re-aimed interplanetary loops do.

---

## 7. The one thing the design review changed (so you know the risk landscape)

The original framing of the self-overlap milestone assumed it could "reuse the existing self-overlap
machinery." Two clean-context adversarial reviews + a code self-check found that the per-instance overlap
map presence (the thing that draws N icons for N simultaneous loop instances) **has only ever rendered
same-body loops** (logistics routes, stations) - it has *no* per-frame body-transition refresh, so it
cannot render a ghost that crosses SOI boundaries (Kerbin->Sun->Duna) per instance. The full overlap build
(Increment 2) therefore requires *building* a new per-cycle, body-crossing renderer on the shared map path
- bigger and riskier than first assumed. That is why we split off Increment 1 (the safe park-rephasing
kernel) and gated the big build behind your in-game validation.

---

## 8. Open decisions / what's next

1. **Validate #1167 Increment 1 in-game** (re-fly s15 "Kerbal X #2"): confirm the park renders near live
   Kerbin connected to the transfer, window 0 touches the planet, and Duna One / Mun are unaffected. This
   confirms the park-rotation sign/magnitude before Increment 2 stacks on it.
2. **Then build Increment 2** (the overlap renderer) - or decide it's not worth the shared-map-path risk yet
   and ship Increment 1 as the bounded improvement.
3. **#1166 and #1167 land together** (1167 stacks on 1166); **#1155 (arrival)** lands independently;
   **#1165 (cross-SOI seam)** stays a documented deferral until the in-flight re-aim branches settle.
4. The **cross-SOI seam (#1165)** is the biggest remaining *render-fidelity* item - the one you'd notice as
   the re-aimed ghost (icon + line) *jumping ~1 SOI radius* at the Kerbin-exit and Duna-entry handoffs
   rather than flying a continuous encounter. It is intentionally not being worked now (deferred last).

---

## Quick reference - every open PR right now

| PR | Base <- Head | Stream | One-liner |
|---|---|---|---|
| #1167 | #1166 <- reaim-self-overlap-scheduling | B | Park re-phase (my Increment 1) |
| #1166 | main <- reaim-heliocentric-parking-departure | B | Engage re-aim on a two-burn park departure |
| #1165 | main <- docs-reaim-seam-todo | D | Docs only: record the deferred cross-SOI seam |
| #1155 | main <- reaim-dest-loiter-retimer | C | Arrival-side destination loiter trim (P4) |
| #1157/1159/1161/1162/1164 | refactor-5 stack | - | Unrelated route-code refactor slices |
