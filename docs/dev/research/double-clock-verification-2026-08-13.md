# Double-clock verification (I6) - 2026-08-13

*Verification of the analysis's UNVERIFIED double-render claim
(`crosstree-dock-loop-coherence-analysis-2026-08-12.md` section 2(e), invariant I6),
required by `design-dock-event-graph.md` section 7.8 before the R6 advisory (section 7.7,
open question Q9) may ship.*

**Verdict: CONFIRMED (structurally). The R6 advisory SHIPS.**

**Method:** automated, in place of the manual playtest section 7.8 originally specified.
The owner directed the verification be automated; the deliverable is this note plus the
committed test that produced its numbers, rather than screenshots.

---

## 1. The claim under test

Invariant I6: *one physical assembly is never concurrently rendered at two different
replay times by two independent clocks.*

The analysis scored I6 "Violated (latent)" and flagged it UNVERIFIED: the member sets and
the enforcement sites were read, but no test or log pinned the collision. The mechanism it
asserted:

- Two disjoint-tree missions MAY loop concurrently. That is pinned behavior, not an
  oversight: `ClearLoopsConflictingWith` keys on SPANNED TREE SETS (own tree + INCLUDED
  foreign dock links), so with the partner-journey link off the sets `{ta}` and `{tb}` are
  disjoint and neither loop clears the other (pinned behavior #4 of
  `CrossTreeDockLoopUnitInGameTest`).
- Mission 1 (tree `ta`) replays the merged docked stretch `AB`. That recording's snapshot
  CONTAINS B's parts - it was captured after the couple.
- Mission 2 (tree `tb`) replays B's own recording `B0`, on its own span clock.
- Nothing couples the two clocks.

So B's matter can be told to render twice, at two different RECORDED times, in one frame.

## 2. What was verified, and how

`Source/Parsek.Tests/DoubleClockVerificationTests.cs`, three cells over the shared
`CrossTreeDockFixture` two-tree AB/CD shape:

```
tb: B0 [0 .. 100]                                   B's own solo flight
ta: A0 [0 .. 150] -> Dock(target = pid B) -> AB [150 .. 300]   B's parts are IN here
                  -> Undock -> { A1 [300 .. 400], B1 [300 .. 380] }
```

Mission 1 = `ta`'s mission (everything included, loop ON, enabled at UT 1000).
Mission 2 = `tb`'s mission (partner-journey link OFF, loop ON, enabled at UT 1037).
The two enable UTs are deliberately different: a player turns two loops on at two
different moments, and equal anchors would make the clocks agree at every cycle boundary.

Both loop units are built by the REAL `MissionLoopUnitBuilder.Build` over the committed
list spanning both trees (no hand-built units; `bodyInfo` null, so the units are faithful).

**The probe.** Sweep the shared wall clock from `max(anchor1, anchor2)` forward across two
full cadences of the longer unit, at a 1 s step, and at every step call the REAL pure
render decision `GhostPlaybackLogic.DecideUnitMemberRender` twice: once for
(unit 1, the `AB` docked-stretch member) and once for (unit 2, the `B0` member). Record
every wall UT where BOTH return `Render`, and the gap between the two units' `spanLoopUT`s
there. `DecideUnitMemberRender` is pure, so a fine step costs nothing and no sampling
artefact can step over a collision.

**The control.** The same fixture and the same sweep with mission 2's loop OFF. A
non-looping mission builds no unit at all, so `B0` has no clock and no render decision:
the collision count must be zero while the `AB` stretch keeps replaying (proving the sweep
is live, not vacuous).

## 3. Measured result

Units as built:

| unit | tree | span | cadence | phase anchor | member window under test |
|---|---|---|---|---|---|
| u1 | ta | [0.0, 400.0] | 400.0 s | 1000.0 | AB = [150.0, 300.0] |
| u2 | tb | [0.0, 100.0] | 100.0 s | 1037.0 | B0 = [0.0, 100.0] |

Probe over `sweep = [1037.0, 1837.0]`, step 1.0 s, 801 samples:

```
collisions        = 302   (of 801 swept wall UTs - 37.7%)
divergedCollisions= 302   (every single one: the clocks are NEVER in agreement there)
minDivergence     = 137.000 s
maxDivergence     = 237.000 s
firstCollisionUT  = 1150.0
```

Control (mission 2's loop OFF): 1 unit built, no unit for `B0`, `collisions = 0`, and the
`AB` stretch still renders across the sweep.

Reading the numbers:

- **The collision is not a grazing coincidence.** More than a third of the swept wall
  clock has both members rendering. It is the steady state whenever the shorter loop is
  inside `B0`'s window and the longer one is inside `AB`'s.
- **Divergence is bounded below by fixture geometry.** The two recorded windows cannot
  take the same value: the closest they can come is `B0`'s end (100) against `AB`'s start
  (150) = 50 s; the widest is `B0`'s start (0) against `AB`'s end (300) = 300 s. That
  bound holds for ANY pair of loop-enable UTs, which is why the test asserts it separately
  from the exact numbers.
- **The exact set {137, 237} is a fixture property, not a property of the defect.** The
  two cadences are commensurate here (400 = 4 x 100), so the phase difference is
  quantized to two values; the 37 s offset is the gap between the two loop-enable UTs.
  Incommensurate spans would smear the divergence across the whole 50-300 s range. The
  numbers are pinned in the test anyway, so a change in the clock arithmetic surfaces as
  a diff rather than as a still-green test.
- **The enforcement provably does not couple the two missions.** With the link off,
  `ClearLoopsConflictingWith(mission 1)` reports `clearedSameTree = 0` and
  `clearedCrossTree = 0`, and mission 2 stays looping. The concurrency is the pinned
  behavior working, not a leak.

## 4. Epistemic boundary (what this does NOT prove)

This pins the **structural** double render: two independent clocks concurrently instruct
the same physical matter to render at two different recorded times. That is the I6
statement, at the exact seam the flight engine drives per member per frame.

It does **not** measure the **visual** severity - two ghosts of the same vessel visible
together in map or flight view, possibly kilometres apart. That follows from the
structural result (the two `spanLoopUT`s index different points of the recorded
trajectories, and those trajectories are what the ghost positioner samples), but
"follows" is an inference: it also depends on both ghosts being spawned, in range, and not
suppressed by an unrelated gate. Confirming it is an observation task, collected
opportunistically in ordinary play exactly like the other M-MIS-8 per-scene visuals
(design 16.2 gives the seam markers no in-game cell for the same reason).

The advisory's wording was chosen to survive that boundary honestly: it says ghosts *may*
appear twice, not that they will.

## 5. Q9 decision

**Q9 (advisory gate): the R6 advisory SHIPS.** The gate was "ships only if the playtest
confirms a player-visible double render; if the collision is not visible in practice, we
document the finding and drop the advisory." The automated verification confirms the
collision exists and is common (37.7% of swept wall time on this fixture) with the two
clocks always diverged. What remains unconfirmed is only the visual magnitude, which the
advisory's own wording does not overclaim.

Shipped shape (design 7.7, unchanged by this verification):

- `MissionStore.TryDescribeDoubleClockAdvisory(enabling, allMissions, graph)` - fires iff
  the enabling mission's tree is TRANSITIVELY dock-connected (breadth-first over
  `DockEventGraph.DockConnectedTreePairs`) to the tree of another mission whose
  `LoopPlayback` is on. Null graph returns null (degradation). Same-tree looping siblings
  are not reported: `SetLoopEnabled` cleared them one line earlier.
- Text: `'<other mission>' loops the same docked flight - ghosts may appear twice`, with
  `(+N more)` after the first name when several connected missions loop.
- One `ScreenMessage` from the Missions-tab loop toggle on ENABLE only, after the enable
  succeeds (so the sentence reads the post-clear store state), plus one Info audit line:
  `[Mission] double-clock advisory: mission='<enabling>' connectedLooping='<other>' trees=<a>-><b>`.
- **NO hard enforcement.** Extending `ClearLoopsConflictingWith` to graph-connected trees
  would switch off a loop the player just asked for and would regress pinned behavior #4
  for every harmless case - two missions that merely touched once and never overlap in
  wall time. The link-include path needs no advisory: including a partner-journey link
  calls `ClearLoopsConflictingWith`, which turns the foreign tree's loop off, so the
  two-concurrent-loops state cannot survive it (verified by a source-text gate, not
  assumed).

## 6. Reproducing

```bash
cd Source/Parsek.Tests
dotnet test --filter "FullyQualifiedName~DoubleClockVerificationTests" \
  --logger "console;verbosity=detailed"
```

The probe cell writes the measured line and both units' parameters to the test log, so
every number in section 3 can be re-derived from a run without reading the source.
