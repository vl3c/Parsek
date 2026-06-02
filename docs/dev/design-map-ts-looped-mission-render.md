# Design: Map / Tracking-Station Rendering of Looped Missions

*Status: Step-3 design doc (gameplay model + rendering architecture contract). It
formalizes the shared model built in the map/TS rendering discussion. It does NOT
yet specify code structure — that is Step 4 (explore + plan). It does NOT change the
recording format, the re-aim solver, or the periodicity scheduler; it defines how the
map/Tracking-Station renderer must consume what those already produce. Builds on and
references `docs/dev/done/plans/reaim-interplanetary-transfers.md` (the deterministic
transfer), `docs/dev/design-mission-periodicity.md` (launch-window scheduling, with its
"replay as-is" decision now superseded — see that doc's banner), and
`docs/parsek-ghost-trajectory-rendering-design.md` (the existing rendering surfaces).*

---

## Problem

Rendering the exact recorded trajectory of a single mission in map view and the
Tracking Station already works. The pain — repeated bug rounds, the dedicated
`design-map-ts-render-tracer.md` observability layer, churn across ~6 Harmony patches
plus OnGUI passes plus a lifecycle tick — is concentrated in rendering **looped
missions**, which are the basis for supply routes.

The root cause, stated plainly: **a looped (re-aimed) mission is not a stored
trajectory — it is a chain re-assembled per launch window.** The transfer is
regenerated, loiter orbits are trimmed to a different count, and the arrival arc is
re-anchored to a new UT. The old map/TS code assumed one continuous recorded path, so
every heterogeneous-segment hand-off (recorded points → generated conic → recorded
points) and every re-anchored joint became a place where an icon jumped, a line
blinked, or two trajectories double-drew. The fix is to make the renderer's model
*match the gameplay model*: a variable-structure chain of typed segments joined at
typed seams.

## Terminology

- **Segment** — a contiguous stretch of a mission's path with a single render
  treatment. Two kinds by *provenance*:
  - **Recorded segment** — replayed exactly from recorded data (ascent, burns, in-SOI
    arcs, landing). Non-conic stretches are *recorded-point* segments.
  - **Generated segment** — computed deterministically, not backed by recorded points
    (the heliocentric transfer). Always a clean conic.
- **Sub-chain** — a maximal run of recorded segments anchored to one body's frame:
  the **departure sub-chain** (launch body) and the **arrival sub-chain** (destination
  body).
- **Joint** — the boundary between two segments. **Rigid** joints must connect
  cleanly (sub-chain-internal seams, the ascent↔orbit and orbit↔landing hand-offs).
  **Flexible** joints carry slack and a *tolerated* visible discontinuity (the two SOI
  boundary seams; the pre-launch point).
- **Treatment** — how a segment is drawn: **stock-conic** (a stock orbit object —
  proto-vessel icon + KSP orbit line) or **traced-path** (our drawn polyline of
  recorded points + marker/label icon).
- **Scheduler** — the existing re-aim + periodicity machinery that chooses *when*
  (launch UT, loiter counts, arrival window) so recorded arcs land on live bodies that
  ≈ match the recording. Out of scope to redesign here; the renderer consumes its output.
- **Cycle window** — `[launch UT … end UT]` of one relaunch of a looped mission, as
  scheduled. The renderer draws a looped mission *only* while the live clock is inside a
  cycle window.

## Mental Model

A looped mission's path is a **variable-structure chain**, assembled per cycle window
from the recorded segment classification (never a fixed template):

```
 ── DEPARTURE SUB-CHAIN ──────────►  ⟂  ──TRANSFER──  ⟂  ──── ARRIVAL SUB-CHAIN ─────►
 launch · ascent · [loiter] · eject     (generated)      SOI-entry · approach · [loiter] · land
 anchored to LAUNCH BODY frame          bridges the      anchored to DESTINATION BODY frame
                                        two SOI seams
   rigid segments, replayed exact       clean conic      rigid segments, replayed exact
        ▲ flexible: launch UT,                              ▲ flexible: arrival window
          departure loiter count                             (→ moon config), arrival loiter
        ⟂ flexible SOI exit                ⟂ flexible SOI entry
```

Three structural facts drive the whole design:

1. **The flexible SOI joints DECOUPLE the chain into independent rigid sub-chains.**
   Because the SOI joints carry "wait as long as needed" slack and the transfer is
   *generated* (its time-of-flight flexes to connect whatever departure/arrival pair the
   scheduler picks), the departure end and the arrival end align to their own bodies
   **independently** — the transfer + the two SOI seams absorb the join. This is why
   re-aim is necessary: a replay-as-is loop would have to satisfy launch-site rotation
   AND destination phase simultaneously through one launch UT and a fixed transfer (a
   joint resonance of incommensurate periods ≈ centuries). The flexible joint converts a
   multiplicative scheduling problem into two independent, frequently-recurring
   alignments.

2. **Variable structure.** Optional pieces drop out by mission: *direct departure* ⇒ no
   departure parking loiter; *direct landing / atmospheric slowdown* ⇒ no arrival loiter.
   A same-body mission (Kerbin→Mun) has no heliocentric transfer segment at all. The
   chain is read from each recording's segment classification, not stamped from a template.

3. **The renderer only ever answers "where is this ghost right now?"** A looped mission
   contributes nothing to the map until launch (see Scope). At any instant the ghost
   occupies exactly one segment; the renderer draws that segment's treatment with the
   icon riding its line. The SOI seams are moments in time where one anchored piece hands
   off to the next.

## Scope (v1)

- **In:** single hop, launch-SOI → one other SOI; destination is a **single-moon** body
  (Duna, Eve, …) or a same-body target (Mun, Minmus). Reliability over completeness.
- **Deferred:** Jool / multi-moon destinations (treated as a mini-system later), gravity
  assists, multi-hop chains, plane-aware / complex transfers, an end-to-end forward
  trajectory *preview* (drawing the whole chain ahead of the ghost — a cool but advanced
  feature), and erasing the SOI-boundary *direction* seam (see Edge cases).

## Responsibility split (load-bearing)

The single most important architectural line, because it keeps the renderer simple and
puts all the hard correctness in the already-working scheduler:

- **Scheduler owns *when*.** It picks launch UT, loiter counts, and the arrival window so
  that recorded arcs play against live bodies that ≈ match the recording — *including the
  high-visibility moon-flyby phases* (a recorded moon flyby is a transient SOI pass, i.e.
  a binding phase constraint on that moon, the same machinery that already makes Mun /
  Minmus loop timing work well).
- **Renderer owns *drawing the current segment*.** It draws the recorded arc relative to
  the destination body **at the bodies' live positions** and **never repositions bodies**.
  It trusts the schedule. If a moon isn't where it was recorded, that is a scheduler
  miss, not a renderer bug.

## The renderer contract (per frame, per ghost)

```
given the live clock UT inside an active cycle window:
  1. LOCATE   → which segment of this ghost's assembled chain contains UT?
  2. CLASSIFY → segment kind ⇒ treatment:
                  conic (loiter / transfer / arrival orbit)  → stock-conic
                  recorded non-conic (ascent / burn / approach / landing) → traced-path
  3. DRAW     → exactly one treatment is active this frame; the other stands down.
                place the icon at the interpolated point ON that segment's line.
```

Invariants (these make the historical bug family unrepresentable):

- **One treatment per ghost per frame.** Never both a stock conic and a traced path for
  the same ghost simultaneously (kills polyline/orbit double-draw).
- **Icon always on, always on its current segment's line.** Visibility has one authority;
  the icon is never toggled independently of its line (kills icon blink and icon-off-line
  drift). For traced-path segments the icon and the path are produced together, so they
  cannot disagree.
- **Stock wherever the segment is a conic.** Use a stock orbit object — the loiter, the
  transfer, and the arrival orbit are clean conics, so the stock orbit line gives the
  "looks perfect / looks stock" result for free, and the full conic *ahead* of the icon
  is the stock default (and wanted). Traced-path is the narrow compromise reserved for
  recorded non-conic stretches that KSP cannot draw.
- **Current segment only.** The renderer materializes only the segment under the clock —
  not the whole assembled chain. The next segment appears when the ghost crosses into it;
  there is no forward preview across seams (deferred).
- **Full traced path of the current segment** is shown while the ghost is on it (the
  player must see where the ghost is going within that segment). This matches current
  behavior.

## Behavior

Per segment kind, as the clock advances through a cycle window:

- **Ascent / atmospheric (recorded-point):** traced-path appears at launch; icon rides
  it from the surface up; full segment path visible.
- **Loiter / parking orbit (conic):** stock orbit object; full ellipse drawn; icon rides
  it. Loiter *count* is the scheduler's trim — the renderer just plays whatever orbits the
  assembled chain contains.
- **Ejection arc → SOI exit (recorded-point or conic, body-relative):** drawn relative to
  the launch body; ends at the SOI boundary.
- **Heliocentric transfer (generated conic):** appears when the ghost ejects; drawn as a
  single stock orbit object (heliocentric ellipse + SOI patch by stock default); icon
  rides it. This is the segment that "must look perfect" — it is pure math, so any
  imperfection is a bug.
- **SOI entry → approach (recorded-point/conic, destination-body-relative):** drawn
  relative to the destination body at live positions; moon flybys render correctly iff the
  scheduler matched the config.
- **Arrival loiter (conic):** as departure loiter; optional; trim is the scheduler's.
- **Landing (recorded-point):** traced-path; must replay exactly; rigid hand-off from the
  preceding orbit.

Seam transitions over time:

- **Rigid seams** (within a sub-chain, ascent↔orbit, orbit↔landing): the outgoing
  segment ends and the incoming begins at the same point/UT; the hand-off must be
  visually continuous (no jump, no blink).
- **Flexible SOI seams:** a *tolerated* discontinuity is expected and **not hidden** —
  off-camera at the SOI boundary, under high warp. The SOI-boundary *direction* mismatch
  (recorded exit direction vs heliocentric appearance) is explicitly accepted for v1.

## Edge cases

Each: scenario → expected behavior → [v1 / deferred].

1. **Moon flyby with mismatched config.** Recorded arc passes close to a moon, but the
   scheduler's window left the moon out of place → the vessel rides a hyperbola and
   appears to "teleport" past empty space / a misplaced moon. *Expected:* this is the
   highest-visibility failure and is a **scheduler** responsibility — the moon's phase is
   a binding constraint; render as-is at live positions and rely on the schedule. The
   renderer logs the recorded-vs-live moon offset for diagnosis but does not correct it.
   [v1: scheduler constraint already exists for Mun/Minmus; extend coverage as needed.]
2. **SOI-boundary direction seam.** Recorded ejection exits to the planet's right; the
   heliocentric transfer appears on the left. *Expected:* accepted, not corrected — nobody
   cross-references the two zoom levels. [Deferred: "which side gives" at the departure
   seam — re-pointing the recorded arc vs bending the transfer — only if ever needed.]
3. **Variable chain structure.** Direct departure (no parking loiter); direct landing (no
   arrival loiter); same-body mission (no transfer). *Expected:* the chain is assembled
   from the recorded classification; missing pieces simply don't exist as segments. [v1.]
4. **Loiter trim.** Recording had ~100 orbits; the assembled chain has 1–5. *Expected:*
   the renderer plays exactly the orbits present in the assembled chain; no special "this
   was compressed" indication. The cut is whole-period (position/velocity continuous), so
   no seam appears at the trim. [v1.]
5. **Pre-launch.** Before the scheduled launch UT, the mission renders nothing on the map.
   *Expected:* the wait is pure UI — a Warp-to-launch button (jumps the global clock to
   ~15 s before launch) and a Time-to-launch countdown in the Missions tab. No decorative
   preview. [v1.]
6. **End of cycle.** Landing completes / the cycle window ends. *Expected:* per the
   existing playback-end behavior (the renderer does not invent new end semantics here).
7. **Self-overlap (period < span).** Multiple staggered instances of one looped mission
   are live at once. *Expected:* each instance is an independent ghost with its own clock
   position on its own assembled chain; the per-frame contract applies per instance. The
   Space Center and Tracking Station render a single span instance (no overlap machinery
   there), per the mission-abstractions doc. [v1 within existing overlap caps.]
8. **Debris ride-along.** Debris of a looped member rides along on the same cadence.
   *Expected:* debris is its own ghost following the same per-frame contract; it is not a
   special seam. [v1.]
9. **Flight map-view vs Tracking Station.** The per-frame contract and treatments are
   identical across both scenes; only the scene adapter differs (camera, scene gate,
   proto-vessel lifecycle). Tracking Station is the historically weakest path
   (ProtoVessel map presence with no per-frame loop machinery) and is the largest gap.
   [v1: parity required.]
10. **Non-looped exact recordings.** A single mission's exact recorded playback must keep
    working unchanged — it is the degenerate case of the same contract (one sub-chain, no
    transfer, no trim). [v1: no regression.]

## What doesn't change

- The recording format, sidecars, and segment classification.
- The re-aim solver and the periodicity scheduler (the renderer consumes their output;
  it does not re-plan).
- Body positions — the renderer never moves a celestial body; moon-config correctness is
  the scheduler's.
- Ghost *interaction* (focus / target / Fly / re-fly) — already implemented and out of
  scope; the player watches looped ghosts, does not operate them.
- The existing flight-scene 3D ghost meshes/animations (this doc is map/TS only).

## Diagnostic logging

Every decision in the per-frame contract must be reconstructable from the log (the map/TS
render path is otherwise un-debuggable — see the render-tracer doc). At minimum:

- **Segment locate** (`VerboseRateLimited`, per ghost per cycle): chain assembled for the
  window (segment count + kinds + UT ranges), and the located segment for the current UT.
- **Treatment decision** (`VerboseRateLimited`, on change): which treatment is active and
  why (segment kind), and confirmation that the other stands down.
- **Seam transition** (`Info` on change): rigid vs flexible, outgoing→incoming segment,
  UT. For flexible SOI seams, log the tolerated discontinuity magnitude.
- **Moon-config diagnostic** (`Verbose`, on the arrival sub-chain): recorded-vs-live phase
  offset of any moon the arc passes close to, so a scheduler miss is visible in the log.
- **Decision-vs-truth reconciliation:** keep the render-tracer's actual-rendered-state
  probe as the permanent assertion that the drawn surfaces match the per-frame decision.

## Test plan

Pure / synthetic-recording testable (the heavy correctness):

- **Chain assembly from classification:** synthetic recordings producing each structure
  (full interplanetary, direct departure, direct landing, same-body) → assert the expected
  ordered segment list (kinds + UT ranges). Guards: variable-structure assembly.
- **Segment locate:** given an assembled chain and a sweep of UTs → assert the located
  segment and that exactly one is active per UT. Guards: double-draw / gaps at seams.
- **Treatment classification:** each segment kind → expected treatment. Guards: a conic
  drawn as a traced path or vice-versa.
- **Loiter-trim continuity:** an assembled chain with a trimmed loiter → assert no
  positional discontinuity at the cut. Guards: a seam appearing at a whole-period trim.
- **Log assertion tests:** capture the sink and assert the locate / treatment / seam /
  moon-config lines appear with the right fields. Guards: diagnostic coverage surviving
  refactor.
- **Non-looped regression:** an exact single recording renders as the degenerate
  single-sub-chain chain. Guards: regressing the already-working exact playback.

In-game (live KSP): scene parity (flight map ↔ Tracking Station), a real looped Mun
mission watched through a cycle, a moon-flyby arrival, and the high-warp SOI seam.

## References

- `docs/dev/done/plans/reaim-interplanetary-transfers.md` — the deterministic transfer
  (Lambert), per-window re-aim.
- `docs/dev/done/plans/reaim-loiter-compression.md` — whole-period loiter trimming.
- `docs/dev/design-mission-periodicity.md` — launch-window scheduling (`P`, constraints,
  next-window countdown); its "replay as-is, no re-aim" decision is superseded.
- `docs/dev/design-mission-abstractions.md` — Mission / selection / span-clock / overlap.
- `docs/parsek-ghost-trajectory-rendering-design.md` — existing rendering surfaces.
- `docs/dev/design-map-ts-render-tracer.md` — the observability layer; its
  decision-vs-truth probe becomes the permanent reconciliation assertion here.
