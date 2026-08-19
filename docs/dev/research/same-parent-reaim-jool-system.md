# Same-Parent Re-Aim at the Planet-With-Moons Level (the Jool system)

STATUS: **V14M MEASURED AND ARMED, 2026-08-18/19 - section 7 spliced from a completed three-run
discipline, and one pre-registered prediction REFUTED.** The Duna->Ike splice is closed: reading
run `2026-08-18_2336` (PASS attempt 1) supplied every measured value below, armed re-flight
`2026-08-19_0001` (PASS attempt 1) re-confirmed them under gated expectations, and negative
control `2026-08-19_0003` proved the gate reds when inverted. Nothing in section 7 is pending.
The Jool-side numbers (the J1-J5 table) are still derived from committed repo constants or
computed from stock SMAs + `mu_Jool`; nothing Jool-specific is flight-measured. What IS measured is the
Duna->Ike analogue (`V14M-ike-player-loop` run `2026-08-18_2336`, PASS attempt 1), and it moved
this document in a direction worth reading before the rest:

> **An ORBIT-ROOTED recording emits NO `Rotation(launchBody)` constraint.** The V14M lane's
> `ExtractConstraints:` reads `members=1 launchBody=Duna support=Supported constraints=1
> [Orbital(Ike) same-parent ...]`. Prediction #2 below assumed a Rotation(Duna) alongside it, and
> predictions #5-#7 all hang off that assumption; all four are wrong. `method=single-orbital`, not
> `tidal-collapse`.
>
> This **strengthens** section 3.5 and section 6's first bullet rather than weakening them: the
> single-constraint road it predicts for a Jool-park single-moon subject is exactly the road the
> nearest real analogue actually took, and it took it for a MORE general reason than tidal locking
> - park-rooted subjects get there by construction, whatever the moon's rotation state.

Question (M-MIS-7, `docs/dev/todo-and-known-bugs.md` -> "M-MIS-7 - Intra-SOI re-aim and multi-hop
targets"): does Parsek need a re-aim-like mechanism at the planet-with-moons level for the Jool
system, or does phase-lock suffice?

Short answer: **needs a bounded extension** - see section 6. Phase-lock covers strictly more of
the Jool system than the M-MIS-7 framing assumes, the classifier covers more than the framing
assumes, and full same-parent Lambert re-aim is the wrong remedy for the part neither covers.

---

## 1. Scope and the three populations

"Jool system looping" is not one shape. It is three, and they route through three different
pieces of code. Getting this wrong is what makes the M-MIS-7 question look harder than it is.

| # | Shape | Earliest included body (`launchBody`) | Route taken today |
|---|---|---|---|
| P1 | Kerbin -> Jool -> moons (the "Jool-5") | Kerbin | cross-parent -> re-aim on the Kerbin->Jool leg + the M-MIS-6 arrival config hold on the moons |
| P2 | Jool-park -> moon(s) (a craft whose recording begins in Jool orbit) | Jool | same-parent -> phase-lock |
| P3 | moon -> moon (Laythe -> Tylo) | Laythe | cross-parent by `IsSameParentTarget` -> **re-aim, in the Jool frame** |

`launchBody` is the earliest included **surface** body, else the earliest included **orbit** body
(`MissionPeriodicity.cs:413-414`). P2/P3 therefore require a recording (or a trimmed mission
window) that actually begins inside the Jool system - an off-Kerbin launch, a stage born there, or
a mission whose included set is trimmed to the Jool phase. A Kerbin-launched Jool-5 is always P1.

## 2. What the code actually does - three corrections to the framing

### 2.1 Phase-lock does not use a synodic period anywhere

`ExtractConstraints` emits one `Orbital(C)` constraint per SOI-entered body with
`PeriodSeconds = bodyInfo.OrbitPeriod(C)` - the body's own orbit period about **its** parent, not
a synodic against the park (`MissionPeriodicity.cs:485-492`). The park orbit contributes **no
constraint at all**: rule 3 needs a surface segment to emit `Rotation`, and rule 4 explicitly
skips the launch body's own orbit segments (`MissionPeriodicity.cs:472-473`). The word "synodic"
appears in `MissionPeriodicity.cs` only in the `UnsupportedCrossParent` doc comments and the
decline text (`MissionPeriodicity.cs:497-499`); the only synodic arithmetic in the mission stack
is `Reaim/TransferWindowMath.SynodicPeriodSeconds` (`TransferWindowMath.cs:66`), consumed by
`ReaimWindowPlanner.Plan` (`ReaimWindowPlanner.cs:115`) - i.e. by the **re-aim** path only.

So "the phase-lock cadence is the park-vs-moon synodic period" is not the shipped model. The
shipped model is a **near-coincidence lattice**: an anchor constraint pinned exactly at
`k * T_anchor`, every other constraint required to fall within its own tolerance there
(`MissionPeriodicity.TryFindNextScheduleK`, `MissionPeriodicity.cs:1019-1100`).

This matters for the "unbounded wait" question - section 3.4.

### 2.2 The re-aim / phase-lock discriminator is `IsSameParentTarget`, not the classifier

The routing decision is made long before `ReaimClassifier` runs:

- `ExtractConstraints` sets `Support = UnsupportedCrossParent` when any SOI-entered body's parent
  is not the launch body (`MissionPeriodicity.cs:476`, `494-500`; predicate at
  `MissionPeriodicity.cs:1742-1748`).
- `Solve` returns the NoLock sentinel for any non-`Supported` support
  (`MissionPeriodicity.cs:648-655`), so `phaseLocked` stays false
  (`MissionLoopUnitBuilder.cs:382-389`).
- `ApplyReaim` runs **only** under `if (!phaseLocked)` (`MissionLoopUnitBuilder.cs:453`).

Consequence for P3: `IsSameParentTarget("Tylo", "Laythe")` is false (parent of Tylo is Jool, not
Laythe), so a Laythe->Tylo recording is `UnsupportedCrossParent`, is **not** phase-locked, and
**does** reach `ApplyReaim`. It is not the two-moving-endpoint scheduling problem the M-MIS-7
framing describes; it is already a re-aim candidate.

### 2.3 The classifier has no same-parent decline, and its single-hop guard does not fire on P3

Walking `ReaimClassifier.Classify` against a hypothetical Laythe->Tylo recording, guard by guard:

| Step | Site | Verdict for Laythe->Tylo |
|---|---|---|
| strict-ancestor "heliocentric" leg | `ReaimClassifier.cs:104-119` (removal at `:108`) | PASSES - `AncestorChain("Laythe")` minus Laythe = {Jool, Sun}; the Jool-centric coast matches. `commonAncestor = "Jool"`. |
| parking orbit before the leg | `ReaimClassifier.cs:125-139` | PASSES - the last Laythe orbit segment. |
| arrival leg | `ReaimClassifier.cs:143-155` | PASSES - first post-coast segment that is neither Jool nor Laythe = Tylo. |
| **single-hop guard** | `ReaimClassifier.cs:159-165` | **PASSES** - `ReferenceBodyName("Tylo") == "Jool" == commonAncestor`. The "Ike via Duna" example in that comment is about a *Kerbin*-launched recording; it does not describe a Jool-local hop. |
| cross-parent confirmation (LCA) | `ReaimClassifier.cs:167-174` | PASSES - LCA(Laythe, Tylo) = Jool = commonAncestor, and `launchToAnc.Count == 1 > 0`. |
| multi-hop guard | `ReaimClassifier.cs:176-182` | PASSES for a one-way hop; **DECLINES** for a tour that returns to Jool space afterwards ("more than one heliocentric leg"). |
| transfer-run > 1 rev | `ReaimClassifier.cs:243-252` | PASSES - a Laythe->Tylo Hohmann is ~61.8 ks against a 123.7 ks transfer-orbit period. |
| heliocentric-park / MCC predecessor | `ReaimClassifier.cs:254-276` | DECLINES if the craft loitered on a *different* Jool orbit before the burn. |

**So the classifier engages on a Laythe->Tylo recording today.** The only shapes it declines are
the multi-leg tour and the Jool-park-predecessor departure.

For P2 (Jool-park -> Laythe) the classifier declines - but on
`MissingHeliocentricLegReason` (`ReaimClassifier.cs:75-77`, fired at `:118-119`), because
`launchAncestors` for `launchBody = "Jool"` is `{Sun}` and no Sun-bodied segment exists. There is
**no same-parent decline in the classifier**; the exclusion is the strict-ancestor definition of
the transfer leg (the `launchAncestors.Remove(launchBody)` at `ReaimClassifier.cs:108`) plus the
`launchToAnc.Count == 0` clause at `:172`. Section 5.3 lists what those would have to become.

---

## 3. (a) Recurrence arithmetic for a Jool-centric park and the moons

### 3.1 Committed constants

Periods computed from the stock SMAs and `mu_Jool = 2.82528e14 m^3/s^2`; SOI radii and orbital
velocities are the repo's committed values (`Source/Parsek.Tests/MultiMoonAlignmentTests.cs:22-26`
for the periods, `:78-92` for SOI/velocity). Pol is not in the repo fixture and is derived here
(marked `derived`).

| Moon | SMA (m) | ecc | Period (s) | v_orb (m/s) | SOI (m) | tol = SOI/v (s) | duty = tol/P |
|---|---|---|---|---|---|---|---|
| Laythe | 27,184,000 | 0 | 52,980.9 | 3,223.8 | 3,723,645.8 | 1,155.0 | 0.021801 |
| Vall | 43,152,000 | 0 | 105,962.1 | 2,558.8 | 2,406,401.4 | 940.5 | 0.008875 |
| Tylo | 68,500,000 | 0 | 211,926.4 | 2,030.9 | 10,856,518.0 | 5,345.7 | 0.025224 |
| Bop | 128,500,000 | 0.235 | 544,507.4 | 1,482.8 | 1,221,060.9 | 823.5 | 0.001512 |
| Pol `derived` | 179,890,000 | 0.17085 | 901,902.6 | 1,253.2 | 1,042,138.9 | 831.6 | 0.000922 |
| **Gilly (Eve)** `flown` | **31,500,000** | **0.55** | **388,587.4** | **509.3** | **126,123.3** | **247.6** | **0.000637** |

The GILLY row is not a Jool moon and is carried here as the **CALIBRATION CASE for the Bop/Pol
rows**: it is the only ECCENTRIC moon anyone has actually flown a phase-locked loop subject on
(section 9), its duty cycle is TIGHTER than Pol's, and its e = 0.55 is 2.3x Bop's - so whatever
the eccentricity caveat below does to a conclusion, Gilly is where it does it hardest. Its
`v_orb` is the same circular `sqrt(mu_Eve/a)` the Bop/Pol rows use, with `mu_Eve` = 8.1717302e12;
section 9.2 gives the TRUE-speed figure at the flown intercept and the swing across the orbit.

The tolerance formula is `ToleranceSecondsFor` (`MissionPeriodicity.cs:1509`, Orbital branch at
`:1523-1530`) = `SoiRadius / OrbitalVelocity`. The four repo-committed tolerances reproduce
`docs/dev/design-mission-multimoon-alignment.md:157` exactly (1,155 / 940 / 5,346 / 824 s), so the
derivation above is calibrated.

Bop/Pol eccentricity note: `v_orb` is the circular `sqrt(mu/a)`, which is what the repo fixture
pins for Bop (1,482.8 = `sqrt(mu_Jool/1.285e8)`). For an e = 0.235 orbit the true speed swings
+/-24%, so Bop's and Pol's tolerances carry that uncertainty. `B22-jool-orbit.toml:187,195` records
Pol's e as 0.17085 (apoapsis 210,624,206.5 m) and warns explicitly not to treat it as decorative.
**THAT CAVEAT IS NO LONGER ONLY A CAVEAT** - section 9.2 measures it at Gilly, where the true
speed swings 274.4 -> 945.3 m/s (a factor of 3.44) and the tolerance therefore swings 460 -> 133 s
around the 247.6 s circular figure. The flown encounter landed at 630.0 m/s, i.e. tol = 200.2 s,
**19% TIGHTER than the circular row above**. So for an eccentric moon the table's `tol` column is
a mid-range figure and the operative tolerance is an encounter property, not a body property.

### 3.2 The 1:2:4 resonance, as arithmetic

`P_Vall - 2*P_Laythe = +0.3 s`, `P_Tylo - 4*P_Laythe = +2.8 s`, `2*P_Vall - P_Tylo = -2.2 s`.
Against tolerances of 1,155 / 940 / 5,346 s these are three to four orders of magnitude below the
band. The inner three are, for scheduling purposes, one constraint with three names.

Anchor selection (`SelectAnchorConstraintIndex`, `MissionPeriodicity.cs:1170-1195`) picks the
smallest duty: **Vall (0.008875)**, matching `design-mission-multimoon-alignment.md` D2. The
lattice scan then accepts **k = 2**:

- `T_config = 2 * P_Vall = 211,924.2 s` (~= one Tylo period)
- residual at k=2: Laythe 0.6 s (tol 1,155.0), Tylo 2.2 s (tol 5,345.7)

### 3.3 How long the lattice holds, and what breaks it

The launch-side schedule re-scans `k` from each relaunch (`TryFindNextScheduleK`,
`MissionPeriodicity.cs:1019`), so the residual at step `k` is the **absolute**
`max_j CircularPhaseError(k*T_anchor, P_j)` - it does not accumulate across relaunches. But it
does grow with `k`, because the per-step residuals are fixed:

| Set | Anchor | First in-tolerance k | Lattice period | Worst residual | In tolerance? |
|---|---|---|---|---|---|
| Laythe+Vall+Tylo | Vall (8.88e-3) | **2** | 211,924.2 s | 2.2 s | yes |
| Laythe+Tylo | Laythe (2.18e-2) | 4 | 211,923.6 s | 2.8 s | yes |
| + Bop | **Bop** (1.51e-3) | **137** | **74,597,514 s** | 579.0 s | yes |
| + Bop + Pol | **Pol** (9.22e-4) | none in 4,096 | - | 7,062.8 s (vs Pol tol 831.6) | **no -> bounded-best** |

Contiguous in-tolerance prefix for the inner three on the Vall lattice: **k <= 3,850**, i.e.
`3,850 * 105,962.1 = 4.0795e8 s = 44.3 Kerbin years` (Kerbin year 9,203,545 s). Laythe binds
(0.3 s of drift per Vall period against 1,155 s of tolerance); Tylo would sustain ~56 Kerbin
years. This independently reproduces the design doc's "~40 windows / ~44 Kerbin years"
(`design-mission-multimoon-alignment.md:36-48`) from a different direction (the launch-side
lattice rather than the arrival hold), which is a useful cross-check that both surfaces are
computing the same physics.

Two things break it, and both are arithmetic rather than policy:

1. **Adding Bop.** The anchor flips from Vall to Bop (Bop's duty 1.51e-3 is 5.9x tighter), and the
   first joint k is 137, giving a lattice period of **74,597,514 s = 8.1 Kerbin years**. For the
   *arrival hold* that is fatal in a second way: the hold must fit inside the loop's window
   spacing (D6 gate (ii), `design-mission-multimoon-alignment.md:96-99`), and the Kerbin->Jool
   synodic is **10,090,902 s** (`MultiMoonAlignmentTests.cs:27`). `74,597,514 / 10,090,902 =
   7.39` - the required hold is **7.4x longer than the entire window it would have to fit in**.
   That is why D4's fail-closed is right, and it is a factor-of-7 miss, not a marginal one.
2. **Adding Pol.** No `k` in the 4,096-multiple lookahead
   (`ScheduleLookaheadMultiples`, `MissionPeriodicity.cs:603`) satisfies all four others. The
   density estimate agrees: `1 / prod_j (2 * duty_j)` = **~8.47e6 anchor multiples**, three orders
   of magnitude past the lookahead. For the inner-three-plus-Bop set the same estimate is ~25,611
   (the scan's k=137 is a lucky early hit, not the typical spacing).

### 3.4 Can the schedule always be satisfied? Is the wait ever unbounded?

**Liveness: yes, always.** `TryFindNextScheduleK` returns `true` unconditionally once the anchor
period is sane; when no `k` in the window passes, it returns the **bounded-best** (min worst
residual) `k` with `withinTolerance = false` (`MissionPeriodicity.cs:1093-1099`).
`MissionRelaunchSchedule.FoldLaunchTolerance` (`MissionPeriodicity.cs:2738`) then latches
`AllLaunchesWithinTolerance` false one-way, and `PhaseLock APPLIED` reports
`scheduleWithinTol=no scheduleWorstResidual=<n>` (`MissionLoopUnitBuilder.cs:1406-1420`).
`Solve` separately Warns that the fixed-cadence residual exceeds tolerance
(`MissionPeriodicity.cs:1568-1573`). So the failure mode is **amber accuracy, never a hang and
never an unbounded wait** - the loop keeps relaunching on the least-bad lattice point.

**Accuracy: no, not always** - the two rows above.

**The unbounded case that the framing worried about does not exist in the shipped model, but
would exist in the assumed one.** Under a park-vs-moon synodic model, `T_syn = T_park*T_moon /
|T_moon - T_park|` diverges as the park period approaches a moon's. Computed for circular Jool
parks:

| Park radius (alt) | T_park (s) | syn(Laythe) | syn(Vall) | syn(Tylo) | syn(Bop) | syn(Pol) |
|---|---|---|---|---|---|---|
| 8.0 Mm (2,000 km) | 8,458.3 | 10,065 | 9,192 | 8,810 | 8,592 | 8,538 |
| 15.0 Mm (9,000 km) | 21,716.3 | 36,800 | 27,314 | 24,196 | 22,618 | 22,252 |
| 21.0 Mm (15,000 km) | 35,973.1 | 112,060 | 54,463 | 43,328 | 38,518 | 37,468 |
| 26.0 Mm (20,000 km) | 49,557.5 | **766,950** | 93,099 | 64,683 | 54,520 | 52,439 |
| 40.0 Mm (34,000 km) | 94,566.9 | 120,479 | **879,361** | 170,768 | 114,443 | 105,644 |
| 60.0 Mm (54,000 km) | 173,730.5 | 76,227 | 271,644 | **963,927** | 255,133 | 215,180 |

The bolded cells are parks near-co-orbital with the named moon; the synodic diverges there. The
shipped code never evaluates any of these numbers, so the divergence is not a live hazard - but
any future design that introduces a park-relative recurrence must guard the near-co-orbital park.

Hohmann times of flight from a 15 Mm park, for scale: Laythe 18,105 s (5.0 h), Vall 29,304 s
(8.1 h), Tylo 50,420 s (14.0 h). All are short against the recurrence periods, so a P2 mission's
span sits comfortably inside one lattice step.

### 3.5 P2 in one line - **MEASURED at Duna->Ike, 2026-08-18**

A Jool-park recording targeting **one** moon emits exactly one constraint (`Orbital(moon)`), which
`Solve` handles as `count == 1 -> method = "single-orbital", residual 0, withinTolerance true`
(`MissionPeriodicity.cs:725-733`); `TryBuildRelaunchSchedule` returns false on `rawCount < 2`
(`MissionPeriodicity.cs:1269-1271`), so `zeroDrift=no` and the cadence is a whole multiple of the
moon's period. This is **exact and unbounded** - no horizon, no residual, no amber. Phase-lock
does not merely suffice for the single-moon Jool-park case; it is optimal.

**THIS IS NO LONGER A CODE WALK.** `V14M-ike-player-loop` run `2026-08-18_2336` flew the nearest
real analogue - a DUNA-PARK-rooted recording targeting its one moon Ike - and printed exactly this
road:

    ExtractConstraints: ... members=1 launchBody=Duna support=Supported constraints=1
      [Orbital(Ike) same-parent P=65517.862134808071 off=17082.834318690002]
    PhaseLock APPLIED: ... P=65517.862134808071 method=single-orbital
      cadence 21212.067403893918->65517.862134808071
      fixedCadenceResidual=0 fixedCadenceWithinTol=yes zeroDrift=no

One constraint, residual 0, cadence exactly one moon period, no schedule - the paragraph above,
line for line.

**AND THE REASON IS MORE GENERAL THAN THIS DOCUMENT ASSUMED.** Section 7 predicted the Duna->Ike
lane would reach a *different* road (`tidal-collapse`) because Ike's tidal lock puts
`Rotation(Duna)` and `Orbital(Ike)` inside the 1e-6 equality band. It does not, because **there is
no `Rotation(Duna)` constraint to collapse**: the extraction emits a Rotation constraint for a
SURFACE/PAD-ROOTED launch, and a recording that BEGINS IN ORBIT has no surface phase. So a
park-rooted single-moon subject reaches `single-orbital` **by construction, whatever the moon's
rotation state** - the tidal lock is irrelevant to it, and the equality-band collapse is reachable
only from a PAD-ROOTED same-parent subject (one that carries a Rotation constraint in the first
place).

For P2 that is strictly better news than the tidal-collapse story would have been: it does not
depend on Laythe/Vall/Tylo being tidally locked to Jool, and it does not depend on the live
ephemerides landing inside a 1e-6 band. A Jool-park recording targeting one moon gets exact
phase-lock because it has one constraint, full stop.

A Jool-park recording aerobraking through Jool's atmosphere would additionally emit
`Rotation(Jool)` (P = 36,000 s, duty 6.94e-4) and take over as the lattice anchor - a shape worth
a fixture but not analysed further here. **The V14M measurement raises this note's stakes**: since
the Rotation constraint is what a surface/atmospheric phase contributes, the aerobraking shape is
now the ONLY route by which a Jool-park single-moon subject could stop being single-constraint,
and it is also the only route by which the equality-band collapse could ever fire at Jool. If a
collapse reading is wanted, that is the fixture to build - a PAD-ROOTED (or atmosphere-touching)
same-parent recording, not another park-rooted one.

---

## 4. (b) Two moving endpoints: moon-to-moon hops

### 4.1 What today's code actually does with them

Per section 2.2, a Laythe->Tylo recording is **cross-parent** and routes to re-aim, not to a
two-constraint schedule. `ApplyReaim` feeds `ReaimWindowPlanner.Plan` with
`bodyInfo.OrbitPeriod(plan.LaunchBody)` and `...(plan.TargetBody)`
(`MissionLoopUnitBuilder.cs:699-704`) - for P3 those are the *Jool-frame* periods, so the window
spacing is the **moon-moon synodic**:

| Pair | Period ratio | Synodic (s) | Synodic (h) |
|---|---|---|---|
| Laythe-Vall | 2.00001 | 105,961.4 | 29.4 |
| **Laythe-Tylo** | **4.00005** | **70,640.9** | **19.6** |
| Vall-Tylo | 2.00002 | 211,922.0 | 58.9 |
| Laythe-Bop | 10.27743 | 58,691.6 | 16.3 |
| Vall-Bop | 5.13870 | 131,564.8 | 36.5 |
| Vall-Pol | 8.51156 | 120,068.6 | 33.4 |
| Tylo-Bop | 2.56932 | 346,969.5 | 96.4 |
| Tylo-Pol | 4.25574 | 277,019.6 | 76.9 |
| Bop-Pol | 1.65636 | 1,374,088.7 | 381.7 |

Every one of these is playable (16 h to 16 days), because the Jool moons' periods are all short
and none of the pairs is near-co-orbital. So the **window-availability** half of the moon-to-moon
problem is trivially solved - by the existing re-aim planner, with no changes.

Note the resonance makes Laythe:Tylo commensurate (1:4). For re-aim that is not the special case
it would be for a scheduler: the synodic formula does not care, and 70,640.9 s = (4/3) * P_Laythe
exactly. The commensurability *would* matter to a phase-lock treatment - see 4.2.

### 4.2 If a moon-to-moon hop were routed to phase-lock instead

Suppose `IsSameParentTarget` were widened to a sibling test (so P3 became `Supported`). A
Laythe-launched hop with a surface segment emits `Rotation(Laythe)` (P = 52,980.9 s, tol =
`P * 0.25/360` = 36.8 s, `RotationToleranceFraction` at `MissionPeriodicity.cs:545`) plus
`Orbital(Tylo)` (P = 211,926.4, tol 5,345.7). Because Laythe is tidally locked its rotation period
**equals** its orbital period, so this is a two-period problem, not three.

- **Laythe->Tylo (commensurate, 1:4).** `AllDroppedSharePeriod` is false (52,980.9 vs 211,926.4),
  so it goes to the joint best-fit / lattice. Anchor = `Rotation(Laythe)` (duty 6.94e-4 vs Tylo's
  2.52e-2). First joint k = 4, residual 2.8 s. **Trivially satisfiable, every 4 Laythe rotations.**
  This is the special case the resonance buys.
- **Vall->Bop (incommensurate, ratio 5.1387).** Anchor = `Rotation(Vall)` (duty 6.94e-4 vs Bop's
  1.51e-3). The Bop filter has density `2 * 823.5 / 544,507.4 = 3.03e-3`, so the expected first
  good k is ~331 Vall periods = `3.5e7 s = 3.8 Kerbin years` - well inside the 4,096 lookahead.
  **Also satisfiable**, at a coarser cadence.

So a *pair* of incommensurate constraints is a soft problem: one two-sided band of relative width
`2*duty` is hit within `1/(2*duty)` steps, and the smallest duty in the Jool system is Pol's
9.22e-4, giving ~542 steps worst case. The problem only becomes a genuine simultaneous Diophantine
approximation at **three or more** independent periods, where the expected wait is the *product*
of the reciprocal densities - the 25,611 / 8.47e6 figures in 3.3. That is the real boundary:
**two moving bodies are fine; four are not.**

### 4.3 The fallback when nothing fits, verbatim from the code

Covered in 3.4 - `TryFindNextScheduleK` bounded-best, `AllLaunchesWithinTolerance` latched false,
`PhaseLock APPLIED ... scheduleWithinTol=no`, `Solve` Warn. On the arrival-hold side the
equivalent is `ArrivalHoldPlanner` returning an amber reason that reaches
`LoopUnit.ArrivalAmberReason` and the Missions T- cell tint
(`design-mission-multimoon-alignment.md:139`). Nothing silently degrades and nothing stalls.

---

## 5. (c) Would `ReaimTransferSynthesizer` generalize to a Jool-centric frame?

### 5.1 The mechanical answer: yes, almost entirely

`TrySynthesizeTransfer` (`ReaimTransferSynthesizer.cs:402`) is body-agnostic apart from one gate:

- `CelestialBody parent = launchBody.referenceBody; if (parent == null || targetBody.referenceBody
  != parent) fail` (`ReaimTransferSynthesizer.cs:426-431`). For Laythe/Tylo this **passes**;
  `parent` resolves to Jool.
- `double mu = parent.gravParameter` (`:440`) - a parameter, exactly as the M-MIS-7 reuse mandate
  assumed for `UvLambert`.
- `r1`/`r2` come from `launchBody.orbit.getRelativePositionAtUT(...)` /
  `targetBody.orbit...` (`:458-460`), which are parent-relative - Jool-relative here - and are in
  the same frame by construction.
- The conic is built with `transfer.UpdateFromStateVectors(r1.xzy, v1.xzy, parent, departureUT)`
  (`:500-501`), i.e. Jool-relative.
- The plane-tilt correction measures inclination against world +Y
  (`AchievablePlaneInclinationDegrees`, `ReaimTransferSynthesizer.cs:145`, and the frame note at
  `:472-474`). KSP's reference plane is the same absolute plane for every body, so moon
  inclinations (Laythe 0, Vall 0, Tylo 0.025, Pol 4.25, Bop 15 deg) are measured against it too
  and `InclinationBoundDegrees` (`:93-98`) behaves.
- `IsSaneTransferConic` requires `0 <= e < 1, a > 0` (`:36-41`). A moon-to-moon Hohmann in the
  Jool frame is elliptic; passes.

**So the synthesizer already works in a Jool-centric frame for P3 today, unmodified**, and per
section 2.3 the classifier engages. That is the single most surprising finding in this
investigation: Parsek may already be re-aiming moon-to-moon hops. It is untested, unmeasured, and
unrepresented in any harness lane.

### 5.2 The physical answer: the seam term does not survive the frame change

Re-aim substitutes ONLY the common-ancestor coast with a fresh **centre-to-centre** Lambert and
splices it onto the recorded escape/capture legs, which end at the SOI shell. The resulting
discontinuity is of order one SOI radius - measured at Duna as a departure jump of 1.043x Kerbin's
SOI and an arrival jump of 1.027x Duna's SOI
(`docs/dev/research/reaim-seam-investigation.md:24-30`).

That error is *absolutely* similar at Jool but *relatively* far worse, because the SOI is a much
larger fraction of the orbit:

| Body (about its parent) | SOI (m) | SMA (m) | SOI / SMA |
|---|---|---|---|
| Duna (Sun) | 47,921,949 | 20,726,155,264 | **0.23 %** |
| Kerbin (Sun) | 84,159,286 | 13,599,840,256 | **0.62 %** |
| Jool (Sun) | 2,455,985,185 | 68,773,560,320 | 3.57 % |
| Pol (Jool) | 1,042,138.9 | 179,890,000 | 0.58 % |
| Bop (Jool) | 1,221,060.9 | 128,500,000 | 0.95 % |
| Vall (Jool) | 2,406,401.4 | 43,152,000 | 5.58 % |
| **Laythe (Jool)** | 3,723,645.8 | 27,184,000 | **13.70 %** |
| **Tylo (Jool)** | 10,856,518.0 | 68,500,000 | **15.85 %** |
| Ike (Duna) | 1,049,598.9 | 3,200,000 | 32.80 % |
| Mun (Kerbin) | 2,429,559.1 | 12,000,000 | 20.25 % |

Jool SOI is the asset-derived 2,455,985,185 (`harness/scenarios/B22-jool-orbit.toml:206-209`; the
wiki's 2,455,985,200 is ~14.6 m high). Mun SOI/velocity are the V6M pins
(`harness/scenarios/V6M-mun-player-loop.toml:45`, 2,429,559.1 / 542.49).

A Laythe->Tylo transfer arc spans 41.3 Mm (conjunction) to 95.7 Mm (opposition). The two seam gaps
are 3.72 Mm + 10.86 Mm = 14.58 Mm, i.e. **15 % - 35 % of the arc**, against **~0.25 %** for
Kerbin->Duna. Same defect, 60x-140x the relative magnitude. Both gaps are 3-11x the 1,000 km
threshold of the shipped seam-teleport detector (`CHANGELOG.md:102`), which fires at
trajectory-segment boundaries when ghost render tracing is on - i.e. at exactly the two seams.

The corollary is stronger than "it would look worse": at Laythe/Tylo SOI fractions the
centre-to-centre patched-conic *approximation itself* is stressed, not just the render splice. Any
same-parent re-aim at moon scale needs the departure/arrival endpoints placed **on the SOI shell**
(the S4-restitch direction), and that is a precondition, not a polish item.

### 5.3 Minimal contract changes to admit P2 (Jool-park -> moon)

Not an implementation - the smallest set of contract statements that would have to change:

1. **`ReaimClassifier`'s transfer-leg definition** (`ReaimClassifier.cs:104-119`) must widen from
   "the first segment whose body is a STRICT ancestor of the launch body" to "the first segment
   whose body is an ancestor-**or-self** of the launch body **and** is the parent of the target".
   Concretely: `launchAncestors.Remove(launchBody)` at `:108` must become conditional.
2. **The cross-parent confirmation** (`:167-174`) must stop requiring `launchToAnc.Count > 0`. For
   P2 the LCA of Jool and Laythe **is** Jool, and the launch body **is** the common ancestor; the
   current clause exists specifically to keep same-parent shapes out.
3. **`ReaimMissionPlan.ParkingOrbit`'s meaning** (`ReaimClassifier.cs:27`, populated at
   `:125-139`) must change: for P2 there is no "last launch-body orbit before the leg" distinct
   from the leg itself - the park **is** a Jool orbit, which is also the transfer frame. Either a
   new field or an explicit "departure is from a parent-frame park" flag (the shape
   `DepartedFromHeliocentricPark` at `:50` already models, but for a *co-orbital solar* park -
   `IsHeliocentricParkingDeparture` at `:314` gates on ecc <= 0.1 and sma within 10 % of the
   launch body's own solar SMA, neither of which is meaningful when the launch body *is* the
   parent).
4. **`ReaimTransferSynthesizer`'s shared-parent gate** (`:426-431`) must admit
   `launchBody == parent` and take the departure state from the park (the
   `hasDepartureOverride` path at `:457-459` and `:492-494` already exists for exactly this
   "transfer emanates from the vessel's state, not a body centre" case - it is the closest
   existing precedent and should be reused, not duplicated).
5. **The routing gate** (`MissionPeriodicity.cs:476` -> `MissionLoopUnitBuilder.cs:453`) must gain
   a way for a *same-parent* mission to opt into re-aim, since `ApplyReaim` today runs only when
   phase-lock declined. Given 3.5, this should be an explicit opt-in, not a widening: for P2
   phase-lock is exact and re-aim would be strictly worse.
6. **The seam precondition from 5.2** must be a stated entry condition, not a follow-up.

Item 5 is the one that changes the shape of the answer: for P2 there is nothing for re-aim to fix.

---

## 6. Recommendation

**NEEDS BOUNDED EXTENSION** - not "phase-lock suffices", and emphatically not "full same-parent
re-aim".

Argued from the numbers:

- **Phase-lock genuinely suffices, exactly, for P2 single-moon** (3.5: one constraint, residual 0,
  no horizon) - **and this is now MEASURED, not walked, and re-tested as a contract**:
  `V14M-ike-player-loop` (`2026-08-18_2336`) flew a Duna-park -> Ike subject and printed
  `constraints=1` / `method=single-orbital` / `fixedCadenceResidual=0` / `zeroDrift=no` /
  cadence = exactly one moon period; its armed re-flight (`2026-08-19_0001`) re-flew the same
  shape with those values pinned as required tokens and passed attempt 1. It also generalises the reason: a park-rooted subject is single-constraint because it
  emits no `Rotation(launchBody)`, so the P2 claim does not depend on the Jool moons' tidal
  locking or on any equality band. **And suffices for ~44 Kerbin years / ~40 Kerbin->Jool windows
  for the resonant inner three** (3.3: `T_config = 211,924.2 s`, contiguous in-tolerance prefix k <= 3,850). Those
  two cover the overwhelming majority of what players actually fly at Jool.
- **Phase-lock provably cannot cover the outer moons**, and the miss is not marginal: an
  inner-three-plus-Bop configuration needs a 74,597,514 s recurrence, **7.4x the entire
  10,090,902 s Kerbin->Jool window it would have to fit inside**; adding Pol finds no in-tolerance
  lattice point within the 4,096-multiple lookahead at all (best residual 7,062.8 s against Pol's
  831.6 s band). M-MIS-6's D4 fail-closed is arithmetically correct and cannot be tuned away.
- **But full same-parent Lambert re-aim is the wrong remedy for that gap**, because the
  centre-to-centre substitution's seam error is 15-35 % of a Laythe->Tylo arc versus ~0.25 % for
  Kerbin->Duna (5.2). It would trade a bounded, honest, already-surfaced amber for an unbounded
  visual artifact on every inter-moon leg - and would do so on a code path that, per 5.1, is
  already reachable and already unguarded for P3.

The bounded extension, in priority order:

1. **Cover P3 before extending anything.** Build a moon-to-moon fixture and measure what the
   already-engaging re-aim path actually renders (5.1). Today's belief that "same-parent Jool
   shapes stay faithful" is false for moon-to-moon, and the harness has no lane that would notice.
   If the seam is as bad as 5.2 predicts, the correct immediate action is a **decline**, not a
   fix: add a guard so a transfer whose SOI/SMA ratio exceeds some threshold (Duna 0.23 %, Kerbin
   0.62 %, Jool 3.57 % are engaged today; Laythe 13.70 %, Tylo 15.85 % are not) stays faithful.
2. **Relax D4 from all-or-nothing to align-the-resonant-subset**, the alternative
   `design-mission-multimoon-alignment.md:81-83` explicitly deferred to M-MIS-7 evidence. The
   arithmetic supports it: the inner three align at k=2 with 2.2 s of residual whether or not Bop
   is in the set. Giving Bop/Pol legs Loose tolerance / per-leg amber recovers the common Jool-5
   -plus-Pol-flyby shape without any new solver.
3. **Only then consider per-leg re-solve**, and only with the SOI-shell endpoint precondition
   (5.2) satisfied first.

What this recommendation does **not** claim: that the M-MIS-6 config hold behaves as designed in a
real flight. That evidence is still the open M-MIS-7 go/no-go gate
(`todo-and-known-bugs.md`, M-MIS-6 "M-MIS-7 go/no-go"). Everything above is arithmetic and code
reading.

---

## 7. MEASURED - the V14M splice points (CLOSED: reading `2026-08-18_2336`, armed
`2026-08-19_0001`, negative control `2026-08-19_0003`)

The Duna->Ike lane is the nearest same-parent analogue to P2 that will actually fly: launch body
Duna, single moon target Ike, tidally locked. Its reading run's `ExtractConstraints:` summary
(`MissionPeriodicity.cs:2336`), `PhaseLock APPLIED:` line
(`MissionLoopUnitBuilder.cs:1414-1420`) and cadence values fill the table below.

Pre-registered predictions (derived here from `Duna` rotation 65,517.86 s pinned at
`Source/Parsek.Tests/MissionPeriodicityTests.cs:102`, Ike SMA 3,200,000 m, `mu_Duna` =
3.0136321e11, Ike SOI 1,049,598.9 m), against the measured values from
`harness/results/2026-08-18_2336_V14M-ike-player-loop_shots/KSP.log` lines 10571 / 10574.

**PROVENANCE, AND WHY THESE DIGITS ARE NOT A SINGLE-RUN READING.** The values come from the
READING run, which is the only one of the three that collected a log (the harness collects on a
non-PASS, and all three V14M runs but that one were PASSes - the reading run's log survives
because it was collected deliberately). What makes them more than one sample is that the ARMED
re-flight `2026-08-19_0001` re-flew the identical shape with every one of these values PINNED as a
required token - the routing conjunction `method=single-orbital ... zeroDrift=no`, the cohesion
`exoCoastBodyChangeKept=1`, the Ike body-frame rebind - and passed on attempt 1. A second run did
not re-print these digits into an archive; it re-tested them as a contract. And negative control
`2026-08-19_0003` (a temporary `supersedeRows = { min = 1 }`) confirmed the gating machinery reds
when inverted, so the pass is not vacuous.

**SCORECARD: 8 of 12 held, 4 fell together.** Rows 2, 5, 6 and 7 are ONE failure, not four - #2
predicted a `Rotation(Duna)` constraint that does not exist, and #5 (its tolerance), #6 (the
period gap that would be compared) and #7 (the collapse it would trigger) are all downstream of
it. Everything not downstream of #2 held, including both OUTCOME rows (#9, #11) that the collapse
prediction and the true road happen to share.

| # | Quantity | Predicted | Measured (V14M) | Notes |
|---|---|---|---|---|
| # | Quantity | Predicted | Measured (V14M) | Verdict / notes |
|---|---|---|---|---|
| 1 | `launchBody` in `ExtractConstraints:` | `Duna` | `Duna` | **HELD.** The B23 fixture really is Duna-rooted |
| 2 | Constraint list | `Rotation(Duna) P=65,517.86` + `Orbital(Ike) P=65,517.862 same-parent` | `constraints=1` - `Orbital(Ike) same-parent P=65517.862134808071 off=17082.834318690002` ONLY | **REFUTED, and this is the finding.** No Rotation constraint at all: it is emitted for a SURFACE/PAD-ROOTED launch, and this recording BEGINS IN ORBIT. Rows 5-7 are downstream of this one |
| 3 | `Support` | `Supported` | `Supported` | **HELD.** `IsSameParentTarget("Ike","Duna")` true, and an ORBIT departure does not demote it |
| 4 | Ike SOI tolerance | 3,420.2 s (= 1,049,598.9 / 306.881) | not printed | the summary line does not carry per-constraint tolerances; unmeasured, not refuted |
| 5 | Rotation(Duna) tolerance | 45.50 s | **N/A** | downstream of #2 - the constraint does not exist |
| 6 | `\|P_rot(Duna) - P_orb(Ike)\|` | 0.0033 s (band 0.0655 s) | **never evaluated** | downstream of #2 - `AllDroppedSharePeriod` is only reached with >= 2 constraints |
| 7 | `PhaseLock APPLIED ... method=` | **`tidal-collapse`** | **`single-orbital`** | **REFUTED**, downstream of #2. `Solve` takes `count == 1` (`MissionPeriodicity.cs:726-733`) before the collapse branch is considered |
| 8 | `fixedCadenceResidual=` / `fixedCadenceWithinTol=` | `0` / `yes` | `0` / `yes` | **HELD** - by the single-constraint road, which sets residual 0 for the same reason tidal-collapse would |
| 9 | `zeroDrift=` | **`no`** | **`no`** | **HELD.** Note it holds under EITHER road, so this token alone could not have discriminated them - which is why V14M pins the whole `method=...zeroDrift=no` conjunction on one line |
| 10 | `P=` (solution period) | 65,517.862 s | `65517.862134808071` | **HELD to 9 significant figures.** Ike's orbital period, as the dominant (and here the only) constraint |
| 11 | `cadence -> effectiveCadence` | a whole multiple of 65,517.862 s | `21212.067403893918 -> 65517.862134808071` | **HELD** - exactly 1x P (`QuantizeCadenceToMultipleOfP`, k = ceil(21,212/65,518) = 1) |
| 12 | Re-aim engagement | **none** - `ApplyReaim` never called | zero `[ReaimDiag]`, zero `ENGAGED re-aim`, zero `FORCED FAITHFUL`; `factory chain ... reaimed=False` | **HELD**, and now a REQUIRED/FORBIDDEN contract in the V14M spec - re-tested green on the armed run `2026-08-19_0001` |

Two further measurements the table did not pre-register, both worth carrying:

- **The phase anchor.** `NextWindow(ut0, P, referenceUT)` with ut0 = the recording's explicit
  start and referenceUT = the save clock gives k = 1 -> predicted 9,225,915.966; measured
  `phaseAnchorUt = relaunchUt = 9225915.9258263968`, and cycle 2 at `9291433.7879612055` =
  cycle 1 + exactly one P. **0.04 s agreement**, and it survived the refutation of #7 because both
  roads produce the same P and the same `NextWindow()`.
- **`off=17082.834318690002`.** The constraint's phase offset equals the seam UT minus the
  recording's EXPLICIT start (not its first orbit segment's start, which is 2.5 s later). That
  confirms the V6M convention on a second lane.

**The pre-registered contingency was written for the wrong failure mode**, and it is kept here
because the way it missed is instructive. It read: *"If #7 reads `joint-best-fit` or
`dominant-intercept` and #9 reads `zeroDrift=yes`, the live KSP ephemerides put
`|P_rot(Duna) - P_orb(Ike)|` outside 0.0655 s and the whole Duna->Ike lane is a drifting config."*
Neither happened. #7 read `single-orbital` and #9 read `no` - the config is not drifting and not
collapsed; it is **single-constraint**, a case the contingency did not consider because the
prediction took the two-constraint list for granted.

The correction it asked for lands anyway, and lands harder: section 3.5's framing IS corrected -
a park-rooted single-moon subject is single-constraint **by construction** rather than by tidal
luck - and its arithmetic is untouched, exactly as the contingency anticipated for its own
scenario. The lesson for the next pre-registration is to predict the CONSTRAINT LIST as carefully
as the branch it feeds; every failed row here is downstream of one unexamined assumption about
what an orbit-rooted recording emits.

Jool-side numbers that a future Jool lane would fill in the same way. **NO SUCH LANE EXISTS
TODAY** - this table is the standing request, not a pending result, and the V14 pair is the
worked example of how to fill one (pre-register the predictions, fly a reading run, splice, arm,
control):

| # | Quantity | Predicted | Measured | Notes |
|---|---|---|---|---|
| J1 | Live `P_Laythe / P_Vall / P_Tylo` | 52,980.9 / 105,962.1 / 211,926.4 | | fixture constants, `MultiMoonAlignmentTests.cs:22-25` |
| J2 | Live moon SOI radii | 3,723,645.8 / 2,406,401.4 / 10,856,518.0 | | wiki figures; the Jool asset-vs-wiki gap (`B22:206`) says re-read these from the live bodies |
| J3 | `ARRIVAL HOLD kind=config` `T_config=` | 211,924.2 s | | `= 2 * P_Vall` |
| J4 | `alignedWindows=` | ~40 | | `CountAlignedWindowPrefix`; 3.3 derives k <= 3,850 |
| J5 | Bop-inclusive set outcome | amber, D6 slack gate | | required hold 7.4x the 10,090,902 s window |

---

## 8. What this document does not establish

- **No JOOL-SIDE flight measurement.** Sections 7 and 9 are closed, but at DUNA->IKE and
  EVE->GILLY: together they certify the single-moon park-rooted road (3.5, section 6 bullet 1) on
  real flights, at two parents and across the full stock eccentricity range, section 7 through a
  full reading / armed / negative-control discipline - and nothing else. Every Jool-specific number in
  sections 3, 4 and 5 remains arithmetic and code reading, and the J1-J5 table is still empty
  because no Jool lane exists to fill it.
- The P3 claim in 5.1 ("the classifier and synthesizer engage on a Laythe->Tylo recording today")
  is a **code walk**, not an executed test. It should be confirmed by a synthetic
  `ReaimClassifier.Classify` fixture with a Jool-frame segment chain before being acted on. It is
  cheap to write and it is the highest-value next step in this investigation.
- The seam-magnitude argument in 5.2 is a geometric ratio applied to a measurement taken at Duna.
  It predicts the *scale* of the artifact, not its rendered appearance; the shipped S4 restitch and
  the 2026-08 SOI-seam work change what happens at the arrival end and are not modelled here.
- Bop/Pol tolerances assume circular orbital velocity. At e = 0.235 / 0.17085 the true tolerance
  swings materially; every Bop/Pol conclusion above is a "cannot possibly fit" argument with
  7.4x-and-1000x margins, so the eccentricity uncertainty does not flip any of them, but it would
  flip a marginal one. **NOW BACKED BY A MEASUREMENT rather than an assertion** - section 9.2
  measures the swing at Gilly (e = 0.55, tolerance 133 -> 460 s around a 247.6 s circular figure,
  19 % tighter at the flown intercept) and the conclusion is unchanged.
- **NO MULTI-CYCLE (k > 1) MEASUREMENT EXISTS, at any body.** Every phase-lock recurrence result
  in this document - Ike's and Gilly's alike - is a k = 1 reading, because the census lenses are
  rate-limited on a shared key and both lanes' cycles are ~1.4 wall-seconds apart (section 9.3).
  The k > 1 behaviour is precisely what the Jool lattice arithmetic in 3.3 / 3.4 reasons about,
  and it is unmeasured. Closing that gap is cheap (wall-space the brackets) and is a precondition
  for any future recurrence claim.

## 9. MEASURED at an ECCENTRIC moon - the Gilly addendum (CLOSED: B24 `2026-08-19_1655`; V15M reading `_1736` / armed `_1808`; V15T reading `_1739` / armed `_1809`; shared negative control `_1810`)

Section 7 closed the single-moon park-rooted road at DUNA->IKE, a moon that is very nearly
circular (e = 0.03), very nearly equatorial (i = 0.2 deg) and whose SOI is 32.80 % of its own
orbital radius. Every Bop/Pol conclusion in sections 3 and 6 rests on arithmetic whose one
acknowledged soft spot is eccentricity (3.1's note, and section 8's last bullet). This section
is the first measurement at the other extreme.

THE SUBJECT: `B24-gilly-orbit` (run `2026-08-19_1655`, PASS attempt 1) flew a crewed stage from
an Eve park to GILLY and committed the tree there, producing the `gilly-orbit-recorded` fixture;
`V15M-gilly-player-loop` (`2026-08-19_1736`, PASS attempt 1) and `V15T-gilly-ts-arrival`
(`2026-08-19_1739`, PARSEK-FAIL(anomaly) with all 16 steps green - a pre-registered correct
catch on an unrelated render token) then read it from FLIGHT and from the tracking station.
**BOTH LANES ARE ARMED OFF THEIR OWN BYTES AND HAVE CLOSED THE FULL THREE-RUN DISCIPLINE**:
armed re-flights `2026-08-19_1808` (V15M) and `2026-08-19_1809` (V15T), both PASS attempt 1 with
every gated window green, plus ONE negative control `2026-08-19_1810` across the pair (a
temporary `supersedeRows = { min = 1 }` on V15M, correctly PARSEK-FAIL(save-structure),
reverted). So every digit below is a CONTRACT re-tested on a second flight, not a single
reading - the same standard section 7 applies to the Ike measurement. Gilly is e = 0.55, i = 12 deg, SOI 126,123.27 m =
**0.40 % of its orbital radius** - smaller than Pol's 0.58 % and Bop's 0.95 %, i.e. the stock
system's most extreme case of exactly the shape the outer Jool moons have.

### 9.1 The routing held at a SECOND orbit-rooted subject

Section 3.5's general claim - *a park-rooted single-moon subject reaches `single-orbital` by
construction, because it emits no `Rotation(launchBody)`* - was measured once, at Ike. It is now
measured twice, at a different parent, a different moon and a wildly different orbit shape.
`harness/results/2026-08-19_1736_V15M-gilly-player-loop_shots/KSP.log` lines 10595 / 10598:

    ExtractConstraints: tree=355840bc81bf45f8868b7d2508ca6de4 members=1 launchBody=Eve
      ut0=15764033.005015271 support=Supported constraints=1
      [Orbital(Gilly) same-parent P=388587.37684792886 off=114979.43693914078]
    PhaseLock APPLIED: mission='Kerbal X' tree=355840bc81bf45f8868b7d2508ca6de4
      anchor 15879398.351458656->16152620.3818632 P=388587.37684792886
      method=single-orbital cadence 115362.42644345015->388587.37684792886
      fixedCadenceResidual=0 fixedCadenceWithinTol=yes zeroDrift=no

`constraints=1`, `method=single-orbital`, `fixedCadenceResidual=0`, `zeroDrift=no`, and a cadence
of **exactly one moon period** (span 115,362 s is 0.297 of P, so `QuantizeCadenceToMultipleOfP`
takes k = 1). Cycle 2 resolved to `relaunchUt=16541207.758711128` = cycle 1 + exactly one P.

TWO THINGS THIS ADDS BEYOND A REPEAT. (1) The road does not depend on the moon's orbit being
circular or coplanar - e = 0.55 and i = 12 deg change nothing about a single-constraint solve,
which is what section 3.5 asserted structurally and had only a circular datum for. (2) The
ANCHOR ARITHMETIC is reproducible from committed bytes at an eccentric target: the anchor was
DERIVED before the flight from the recording's `explicitStartUT` / `explicitEndUT` / ORBIT_SEGMENT
seam plus the stock elements (16,152,620.423) and the product printed 16,152,620.3818632 -
**0.041 s**, the same residue V14M measured, coming from the product reading `ut0` 0.040 s below
the fixture node's `explicitStartUT`. Both lanes' jump tables were derived, not tuned, and no
jump UT moved at arming.

Both routing lines are now REQUIRED tokens on both V15 lanes (the whole
`method=single-orbital ... zeroDrift=no` conjunction on one line, plus `Orbital(Gilly)
same-parent` and `support=Supported`), and the re-aim trio is FORBIDDEN on both - **and the
armed re-flights `_1808` / `_1809` re-flew the identical shapes with every one of those pinned
and passed attempt 1**. So this is a contract re-tested on a second flight, not an archived
digit, exactly as section 7 describes for V14M.

### 9.2 The tolerance, measured where it is tightest

`ToleranceSecondsFor` = `SoiRadius / OrbitalVelocity`. At Gilly:

| quantity | value | vs Ike |
|---|---|---|
| SOI | 126,123.27 m | 8.3x smaller |
| v_orb (circular, the 3.1-table convention) | 509.3 m/s | 1.66x faster |
| **tol (circular)** | **247.6 s** | **13.8x tighter** |
| v_orb at the FLOWN intercept (r = 24,903,858 m) | **630.0 m/s** | - |
| **tol at the flown intercept** | **200.2 s** | **17.1x tighter** |
| v_orb range over the orbit (peri -> apo) | 945.3 -> 274.4 m/s | - |
| tol range over the orbit | 133 -> 460 s | - |
| duty = tol / P | 6.37e-4 | tightest in the stock system (Pol 9.22e-4) |

The intercept radius is read off the committed `.prec.txt` transfer segments
(`sma = 15,385,793.566 ecc = 0.618627`, apoapsis 24,903,858 m); nothing was tuned to place it
there - it is where MechJeb's transfer window put it, 34 % of the way up Gilly's
14,175,000 -> 48,825,000 m band, on the FAST half.

**AND THE REPLAY STAYED VALID AT +1 P.** `V15M`'s cycle-1 census reads (line 10831):

    seam-endpoint summary evaluated=1 outsideSoi=0

i.e. the lens evaluated the replayed Eve->Gilly SOI-entry endpoint at the re-anchored epoch and
found it still INSIDE Gilly's 126,123 m SOI. That is the falsifiable form of "the phase lock is
exact" at the tightest tolerance the stock system can offer.

**IT IS NOW AN ARMED REGRESSION FLOOR, AND IT HAS BEEN RE-TESTED.** `V15M` requires
`seam-endpoint summary evaluated=[1-9]\d* outsideSoi=0` (the measuring form, value pinned) and
`V15T` requires the presence form `evaluated=\d+ outsideSoi=\d+` (its TS dwell parks the drive
clock inside the recording's last segment and reads the structural `evaluated=0
skip.no-cross-body-successor=1`, so a value pin there would red on a legitimate zero). Both
survived their armed re-flights. A change that starts putting the replayed SOI-entry point
OUTSIDE the moon now reds a lane and forces a re-read - which is the whole point of arming a
value rather than a presence, and it is the GS-3 precedent.

Read it with 9.3's limit attached: **it is a k = 1 result, on both lanes.**

WHY THIS MATTERS TO THE BOP/POL ROWS. Section 3.1's eccentricity note said the Bop/Pol
tolerances "carry that uncertainty"; 9.2 shows what the uncertainty looks like when it is
measured - a 3.44x swing at e = 0.55, with the operative value set by WHERE the encounter lands.
Section 6's Bop/Pol arithmetic survives it untouched, because those are 7.4x and 1000x margins
and a factor of ~1.7 either way does not flip them (section 8's last bullet already said so, and
is now backed by a measurement instead of an assertion). What it WOULD flip is any marginal
case, and it means a Bop/Pol tolerance quoted from the 3.1 table should be treated as a
mid-range figure.

### 9.3 THE NAMED GAP: multi-cycle recurrence is currently UNMEASURABLE at seam pacing

This is the section's most consequential result and it is a statement about the HARNESS, not
about the product.

V15M was designed to compare the census at CYCLE 1 against CYCLE 2 - the direct instrument for
"does a residual amplify linearly in k?", which is the Bop/Pol question in miniature. **It could
not be read.** The run emitted the `seam-endpoint summary` and the `faithful-parity summary`
exactly ONCE each, at cycle 1; there is no cycle-2 emission of either in the log.

THE MECHANISM, named precisely - and NOT a regression of a fix that already exists. Both lenses
log through `ParsekLog.VerboseRateLimited(..., 5.0)`. The 2026-08-14 `line-blink-census` work
already CLASS-SPLIT the census key into `seam-endpoint-summary-measured` / `-skip-only`, so that
a skip-only pass can no longer shadow a measuring one
(`docs/dev/todo-and-known-bugs.md` -> SEAM-ENDPOINT-CENSUS-UNREADABLE-ON-A-SHORT-LANE), and
**that split is visibly working here**: every V15M run prints BOTH classes (`evaluated=1
outsideSoi=0` and `evaluated=0 ... skip.body-mismatch=1`). What the split deliberately left
alone is the 5.0 s limiter WITHIN a class - correct for every lane that has ONE arrival. A
multi-cycle lane has TWO, both in the measuring class.

Why the window is so easy to hit: every `TimeJump` is an instantaneous Planetarium clock set, so
the 388,587 GAME-seconds between the two cycles cost NO wall time - the two arrival brackets are
~1.4 WALL-seconds apart (cycle-1 summaries at 20:37:42.015, the cycle-2 bracket at 20:37:43.4).
The cycle-2 measuring pass therefore lands inside the window the cycle-1 measuring pass already
spent, and is dropped. This is the V6M `probe frame summary is NOT pinnable` precedent one layer
in: a per-pass summary on a per-CLASS key is unreadable TWICE inside one fast-stepped run.

MEASURED ON ALL THREE V15M RUNS (`_1736` reading, `_1808` armed, `_1810` control): exactly two
census lines each, byte-identically, and never a cycle-2 measuring pass. Filed as a second,
different case under the entry above.

**WHAT IT BOUNDS.** Everything this document can currently say about single-moon phase-lock
recurrence is a **k = 1** statement - and that is now true of an ARMED contract on two lanes
rather than of a single reading, which makes the gap harder to notice and therefore worth
stating this loudly. The k > 1 behaviour - whether a residual accumulates, and
whether it accumulates linearly - is exactly what sections 3.3 and 3.4 reason about
arithmetically for the Jool lattice and exactly what the Bop/Pol "cannot possibly fit" argument
would need if it were ever marginal. There is no measurement of it, at any body.

THREE RECOURSES, in increasing cost (recorded in `V15M-gilly-player-loop.toml` header 2b):

1. **Wall-space the brackets.** A variant inserting > 5 wall-seconds (10 for margin) between
   the cycle-1 and cycle-2 arrival brackets (`RecordingState` spacers are the established
   instrument - V8 used them to re-pace a bracket for the line-blink straddle) puts the second
   emission outside the limiter window. Cheapest; changes pacing only, not the measurement, and
   costs log volume on NO other lane - which is why it is preferred over a per-cycle key.
2. **Use a different lens.** The engine's per-frame `engine-frame-iter` / map-render truth lines
   are emitted per ghost per cycle and are what 9.4's separation reading came off. They carry
   POSITION, not the census verdict, so this is a reconstruction rather than a read.
3. **Two runs, one per cycle.** Most expensive and least comparable, since the runs then differ
   in more than k.

NO PRODUCT CHANGE IS PROPOSED. The rate limiter is doing its job; what is recorded is that a
fast-stepped multi-cycle lane cannot read a shared-key summary twice, and that closing this gap
is the precondition for the harness saying anything about k > 1.

### 9.4 A measured consequence of phase-locked replay for any CO-ORBITING observer

V15M's observer is the fixture's own parked stage, in the same Gilly orbit the ghost replays.
The engine's own per-frame line at each cycle's park epoch reads:

| epoch | line | reading |
|---|---|---|
| cycle-1 park | 11247 | `zone=Physics rdist=775m` |
| cycle-2 park | 12147 | `zone=Visual rdist=115640m` |

**775 m -> 115.6 km, and it is NOT a ghost-vs-moon desync.** It is co-orbital PHASE walking. The
loop cadence is one Gilly period (388,587.377 s); the observer's own 27,024 x 26,321 m Gilly park
has a period of ~17,244 s and Gilly's rotation is 28,255 s, and neither is commensurate with the
cadence - so the observer sits at a different point in its own orbit each time the ghost is
replayed. It is a fixed per-cycle geometric fact that moves only when the phase anchor does, and
a retry reproduces it exactly (V6M's "the separation is not a draw" banner, now with a number).

WHY IT BELONGS IN THIS DOCUMENT: a Jool-park observer watching a phase-locked single-moon replay
would see the same thing, and the magnitude scales with the observer's own orbit rather than with
the moon's. Two consequences worth carrying into any P2 UX reasoning. (1) A player parked
anywhere near the arrival will NOT find the replay in the same place on successive cycles unless
their park period divides the cadence - which for a Jool park and a moon period it generally will
not. (2) It is what makes the watch-mode result below possible at all on cycle 1 and irrelevant
on cycle 2.

TWO SIDE MEASUREMENTS FROM THE SAME RUN, both filed rather than analysed here:

- **The suite's second-ever watch-mode ENTRY** (after V7M's), and its first at a non-Kerbin
  parent: `enterwatchmode complete:` at a 775 m separation. The cycle-2 attempt answered
  `already-watching`, which measures idempotency and survival across a loop re-arm rather than a
  second entry decision.
- **306 stock NREs on every frame from the cycle-boundary camera fallback to scene end**, filed
  as `docs/dev/todo-and-known-bugs.md` -> WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM. Report-only,
  no Parsek frame in any stack, and the only run in the suite that has ever held watch mode
  across a loop-cycle boundary - which is the plausible trigger. REPRODUCED on the armed
  re-flight at 443 against 447, a 0.9 % spread on a byte-identical shape.
- **One INTERMITTENT `line-blink` raise at the CYCLE-2 PARK epoch** (control run `_1810`, 1-of-3
  across the three V15M runs), on the ghost's Gilly proto orbit line, dark edge, with the OFF
  decided by `director-traced-path-suppress` - a case the window-exit exemption deliberately
  does not cover, so the detector behaved as documented. Filed as the 14th archived raise under
  LINE-BLINK-JUMP-STRADDLE-DETECTOR-GAP. It is noted HERE because the shape is a consequence of
  9.4's geometry: at the cycle-2 park the ghost is 115.6 km away and the Director's TracedPath
  takes the leg, which is a handoff the cycle-1 park (775 m) never performs.

### 9.5 Effect on the recommendation (section 6): IT STANDS

Nothing here moves the verdict, and the two halves move in opposite directions by design:

- **Bullet 1 is STRENGTHENED, with one qualifier added.** "Phase-lock genuinely suffices,
  exactly, for P2 single-moon" is now measured at TWO parents and across the full eccentricity
  range the stock system offers - e = 0.03 at Ike and e = 0.55 at Gilly, with the tightest duty
  cycle in the game - and it produced `residual=0`, cadence = one P and a census reading of
  `outsideSoi=0`, all of it now ARMED as required tokens on two lanes and re-tested green on
  their armed re-flights. THE QUALIFIER: **at k = 1**. Section 9.3 is the reason that qualifier now
  appears, and it applies to the Ike measurement retroactively too (V14M's shape has the same
  pacing and the same shared limiter key, so its cycle-2 census was equally unread).
- **Bullet 2 is untouched.** The outer-moon impossibility rests on 7.4x and 1000x margins that a
  1.7x tolerance revision cannot approach. 9.2 replaces the eccentricity caveat's assertion with
  a measurement and the conclusion is unchanged.
- **Bullet 3 is untouched.** Gilly says nothing about moon-to-moon seam magnitude; it is a
  park-to-moon subject like Ike.
- **The priority order is unchanged**, with one addition at the bottom: closing 9.3's
  census-pacing gap is now a precondition for any future claim about multi-cycle recurrence, and
  it is cheap (recourse 1). It does NOT outrank item 1 - covering P3 is still the highest-value
  next step, because P3 is an unguarded code path and 9.3 is a measurement gap.

---
